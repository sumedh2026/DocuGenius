using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocuGenious.Configuration;
using DocuGenious.Models;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace DocuGenious.Services;

public class GroqService : IGroqService
{
    private readonly ChatClient _chatClient;
    private readonly GroqSettings _settings;
    private readonly ILogger<GroqService> _logger;

    public GroqService(AppSettings settings, ILogger<GroqService> logger)
    {
        _settings = settings.Groq;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            throw new InvalidOperationException(
                "Groq API key is not configured. Please set Groq:ApiKey in appsettings.json.");

        // The OpenAI .NET SDK supports custom endpoints — Groq exposes an OpenAI-compatible API
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(_settings.BaseUrl)
        };
        var groqClient = new OpenAIClient(new ApiKeyCredential(_settings.ApiKey), clientOptions);
        _chatClient = groqClient.GetChatClient(_settings.Model);
    }

    public async Task<bool> ValidateConnectionAsync()
    {
        try
        {
            var messages = new List<ChatMessage> { new UserChatMessage("Say 'OK'.") };
            var response = await _chatClient.CompleteChatAsync(messages);
            return response?.Value != null;
        }
        catch (ClientResultException ex) when (ex.Status == 429)
        {
            _logger.LogError("Groq quota exhausted (HTTP 429). Check your plan at https://console.groq.com");
            throw new InvalidOperationException(
                "Groq quota exhausted. Check your plan at https://console.groq.com", ex);
        }
        catch (ClientResultException ex) when (ex.Status == 401)
        {
            _logger.LogError("Groq API key is invalid (HTTP 401). Check Groq:ApiKey in appsettings.json.");
            throw new InvalidOperationException(
                "Groq API key is invalid. Verify your key at https://console.groq.com/keys", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate Groq connection.");
            return false;
        }
    }

    public async Task<AnalysisResult> AnalyzeJiraTicketsAsync(
        List<JiraTicket> tickets, DocumentationType docType)
    {
        _logger.LogInformation("Analysing {Count} JIRA ticket(s) with Groq...", tickets.Count);

        var ticketContext = BuildJiraContext(tickets);
        var userPrompt = $"""
            {GetFocusInstructions(docType)}

            === JIRA TICKETS ===
            {ticketContext}

            Respond with a valid JSON object following this exact schema:
            {GetJsonSchema(docType)}
            """;

        return await CallOpenAiAsync(GetSystemPrompt(docType), userPrompt, docType,
            $"JIRA: {string.Join(", ", tickets.Select(t => t.Key))}");
    }

    public async Task<AnalysisResult> AnalyzeGitRepositoryAsync(
        GitRepositoryInfo repoInfo, DocumentationType docType)
    {
        _logger.LogInformation("Analysing Git repository at {Path} with Groq...", repoInfo.RepositoryPath);

        var repoContext = BuildGitContext(repoInfo);
        var userPrompt = $"""
            {GetFocusInstructions(docType)}

            === GIT REPOSITORY ===
            {repoContext}

            Respond with a valid JSON object following this exact schema:
            {GetJsonSchema(docType)}
            """;

        return await CallOpenAiAsync(GetSystemPrompt(docType), userPrompt, docType,
            $"Repository: {repoInfo.RepositoryUrl ?? repoInfo.RepositoryPath}");
    }

    public async Task<AnalysisResult> AnalyzeCombinedAsync(
        List<JiraTicket> tickets, GitRepositoryInfo repoInfo, DocumentationType docType)
    {
        _logger.LogInformation("Analysing combined JIRA + Git context with Groq...");

        var ticketContext = BuildJiraContext(tickets);
        var repoContext   = BuildGitContext(repoInfo);
        var userPrompt = $"""
            {GetFocusInstructions(docType)}
            The JIRA tickets define the requirements; the Git repository shows the implementation.

            === JIRA TICKETS ===
            {ticketContext}

            === GIT REPOSITORY ===
            {repoContext}

            Respond with a valid JSON object following this exact schema:
            {GetJsonSchema(docType)}
            """;

        return await CallOpenAiAsync(GetSystemPrompt(docType), userPrompt, docType,
            $"JIRA: {string.Join(", ", tickets.Select(t => t.Key))} | Repo: {repoInfo.RepositoryUrl ?? repoInfo.RepositoryPath}");
    }

    private async Task<AnalysisResult> CallOpenAiAsync(
        string systemPrompt, string userPrompt, DocumentationType docType, string sourceInfo)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(TruncateIfNeeded(userPrompt))
        };

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = _settings.MaxTokens,
            Temperature = 0.3f  // Lower temperature for more deterministic documentation
        };

        try
        {
            var response = await _chatClient.CompleteChatAsync(messages, options);
            var content = response.Value.Content[0].Text;
            return ParseAnalysisResult(content, docType, sourceInfo);
        }
        catch (ClientResultException ex) when (ex.Status == 429)
        {
            throw new InvalidOperationException(
                "Groq quota exhausted. Check your plan at https://console.groq.com", ex);
        }
        catch (ClientResultException ex) when (ex.Status == 401)
        {
            throw new InvalidOperationException(
                "Groq API key is invalid. Verify your key at https://console.groq.com/keys", ex);
        }
    }

    private static AnalysisResult ParseAnalysisResult(string rawContent, DocumentationType docType, string sourceInfo)
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // Try each candidate in order — stop at the first successful parse
        foreach (var candidate in ExtractJsonCandidates(rawContent))
        {
            try
            {
                var result = JsonSerializer.Deserialize<AnalysisResult>(candidate, jsonOptions);
                if (result != null && HasMeaningfulContent(result))
                {
                    result.DocumentationType = docType;
                    result.SourceInfo        = sourceInfo;
                    result.GeneratedAt       = DateTime.UtcNow;
                    return result;
                }
            }
            catch { /* try next candidate */ }
        }

        // Hard fallback: surface the raw response so the user sees something
        return new AnalysisResult
        {
            ExecutiveSummary  = rawContent,
            DocumentationType = docType,
            SourceInfo        = sourceInfo,
            GeneratedAt       = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Yields JSON string candidates from the model response, from most to least specific.
    /// Strategy 1 — raw text (model returned clean JSON)
    /// Strategy 2 — extract from ```json ... ``` fence
    /// Strategy 3 — extract from ``` ... ``` fence
    /// Strategy 4 — extract from first { to last } (handles any leading/trailing prose)
    /// </summary>
    private static IEnumerable<string> ExtractJsonCandidates(string raw)
    {
        var trimmed = raw.Trim();

        // 1. Raw text as-is
        yield return trimmed;

        // 2. ```json ... ``` fence (Groq/LLaMA often wraps output this way)
        var jsonFence = Regex.Match(raw, @"```json\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
        if (jsonFence.Success)
            yield return jsonFence.Groups[1].Value.Trim();

        // 3. Generic ``` ... ``` fence
        var genericFence = Regex.Match(raw, @"```\s*([\s\S]*?)\s*```");
        if (genericFence.Success)
            yield return genericFence.Groups[1].Value.Trim();

        // 4. Brace extraction — grab everything from first { to last }
        //    This is the most resilient: works even when the model adds a preamble
        var firstBrace = raw.IndexOf('{');
        var lastBrace  = raw.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            yield return raw[firstBrace..(lastBrace + 1)];
    }

    private static bool HasMeaningfulContent(AnalysisResult r) =>
        !string.IsNullOrWhiteSpace(r.ExecutiveSummary)   ||
        !string.IsNullOrWhiteSpace(r.TechnicalOverview)  ||
        !string.IsNullOrWhiteSpace(r.UserGuide)          ||
        r.Features.Count > 0;

    private static string BuildJiraContext(List<JiraTicket> tickets)
    {
        var sb = new StringBuilder();

        foreach (var t in tickets)
        {
            sb.AppendLine($"Ticket: {t.Key} [{t.IssueType}] — {t.Summary}");
            sb.AppendLine($"Status: {t.Status} | Priority: {t.Priority}");
            sb.AppendLine($"Project: {t.ProjectName ?? t.ProjectKey}");

            if (!string.IsNullOrWhiteSpace(t.Description))
            {
                sb.AppendLine("Description:");
                sb.AppendLine(Truncate(t.Description, 2000));
            }

            if (t.AcceptanceCriteria.Count > 0)
            {
                sb.AppendLine("Acceptance Criteria:");
                foreach (var ac in t.AcceptanceCriteria)
                    sb.AppendLine($"  - {ac}");
            }

            if (t.Labels.Count > 0)
                sb.AppendLine($"Labels: {string.Join(", ", t.Labels)}");

            if (t.Components.Count > 0)
                sb.AppendLine($"Components: {string.Join(", ", t.Components)}");

            if (t.SubTasks.Count > 0)
            {
                sb.AppendLine("Sub-tasks:");
                foreach (var st in t.SubTasks)
                    sb.AppendLine($"  - {st.Key}: {st.Summary} [{st.Status}]");
            }

            if (t.Comments.Count > 0)
            {
                sb.AppendLine("Recent Comments:");
                foreach (var c in t.Comments.Take(5))
                    sb.AppendLine($"  [{c.Author}]: {Truncate(c.Body, 300)}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildGitContext(GitRepositoryInfo repo)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Repository: {repo.RepositoryUrl ?? repo.RepositoryPath}");
        sb.AppendLine($"Current Branch: {repo.CurrentBranch}");
        sb.AppendLine($"Total Commits: {repo.TotalCommits}");
        sb.AppendLine($"Contributors: {string.Join(", ", repo.Contributors.Take(10))}");

        if (repo.Technologies.Count > 0)
            sb.AppendLine($"Technologies: {string.Join(", ", repo.Technologies)}");

        if (repo.Branches.Count > 0)
            sb.AppendLine($"Branches: {string.Join(", ", repo.Branches.Take(10))}");

        // Directory structure
        if (repo.Structure != null)
        {
            sb.AppendLine("\nDirectory Structure:");
            AppendStructure(sb, repo.Structure, 0);
        }

        // Recent commits
        if (repo.RecentCommits.Count > 0)
        {
            sb.AppendLine("\nRecent Commits (last 15):");
            foreach (var c in repo.RecentCommits.Take(15))
            {
                sb.AppendLine($"  [{c.Sha}] {c.Date:yyyy-MM-dd} {c.Author}: {c.Message}");
                if (c.ChangedFiles.Count > 0)
                    sb.AppendLine($"    Files: {string.Join(", ", c.ChangedFiles.Take(5))}");
            }
        }

        // Source file summaries
        if (repo.Files.Count > 0)
        {
            sb.AppendLine($"\nSource Files ({repo.Files.Count} analysed):");
            foreach (var f in repo.Files.Where(x => x.Content != null).Take(20))
            {
                sb.AppendLine($"\n--- {f.Path} ---");
                sb.AppendLine(Truncate(f.Content!, 800));
            }
        }

        return sb.ToString();
    }

    private static void AppendStructure(StringBuilder sb, DirectoryStructure node, int indent)
    {
        var prefix = new string(' ', indent * 2);
        var icon = node.IsDirectory ? "📁" : "📄";
        sb.AppendLine($"{prefix}{icon} {node.Name}");

        foreach (var child in node.Children)
            AppendStructure(sb, child, indent + 1);
    }

    // ─── Per-doc-type: focus instructions (user prompt prefix) ───────────────────

    private static string GetFocusInstructions(DocumentationType docType) => docType switch
    {
        DocumentationType.UserGuide => """
            Produce a USER GUIDE for this feature/product.
            TARGET AUDIENCE: Non-technical end users — business users, customers, or general staff
            who have NO programming knowledge. Assume they have never seen source code.
            TONE: Friendly, simple, jargon-free. Replace every technical term with plain English.
            FOCUS ON:
            - What the product does and the value it gives the user (not how it is built)
            - Step-by-step tasks a user will perform (login, navigate, fill forms, submit, etc.)
            - What the user sees on screen at each step
            - Common mistakes and how to recover from them
            EXCLUDE: Source code, architecture, database schemas, API internals, deployment steps.
            """,

        DocumentationType.TechnicalDocumentation => """
            Produce TECHNICAL DOCUMENTATION for this codebase/feature.
            TARGET AUDIENCE: Software engineers, developers, and DevOps engineers who will
            work on, maintain, or integrate with this system.
            TONE: Precise, detailed, engineering-focused.
            FOCUS ON:
            - System architecture, design patterns, and component responsibilities
            - How the code is structured and why key decisions were made
            - Setup, build, and deployment instructions with exact commands
            - All configuration options and environment variables
            - API contracts (endpoints, request/response JSON payloads)
            - Dependencies with versions and their purpose
            - Known technical issues, gotchas, and improvement recommendations
            INCLUDE: Code references, technical terms, implementation details.
            """,

        DocumentationType.ApiDocumentation => """
            Produce API DOCUMENTATION for this system.
            TARGET AUDIENCE: Developers integrating with or consuming this API.
            TONE: Precise, reference-style.
            FOCUS ON:
            - Every API endpoint: HTTP method, full path, purpose
            - Exact request body JSON with all fields, types, and whether required/optional
            - Exact response body JSON including success and error shapes
            - Authentication mechanism and how to pass credentials
            - HTTP status codes and what each means
            - Rate limits, pagination, and versioning if applicable
            EXCLUDE: UI instructions, business narrative, deployment steps.
            """,

        DocumentationType.ArchitectureOverview => """
            Produce an ARCHITECTURE OVERVIEW for this system.
            TARGET AUDIENCE: Senior engineers, architects, and tech leads evaluating or extending the system.
            TONE: High-level, strategic, diagram-description style.
            FOCUS ON:
            - Overall system design and the rationale behind it
            - Major components/services and their responsibilities
            - How components communicate (REST, events, queues, databases)
            - Data flow from user action to storage and back
            - Technology stack choices and why they were selected
            - Scalability, resilience, and security considerations
            - What to change first if the system needs to grow
            EXCLUDE: Step-by-step user tasks, low-level implementation code.
            """,

        _ => """
            Produce FULL DOCUMENTATION covering all audiences for this system.
            Include an executive summary, technical overview, architecture, a non-technical
            user guide, setup instructions, configuration guide, API endpoints,
            dependencies, recommendations, and known issues.
            Balance clarity for non-technical readers in the user guide section while
            being precise and detailed in all technical sections.
            """
    };

    // ─── Per-doc-type: tailored JSON schemas ──────────────────────────────────────

    private static string GetJsonSchema(DocumentationType docType) => docType switch
    {
        DocumentationType.UserGuide => """
            {
              "executiveSummary": "What this product does and who it helps — 2-3 plain-English sentences, no technical terms",
              "userGuide": "Full step-by-step guide using ## headings for sections and numbered lists for steps. Use plain English only.",
              "features": [
                {
                  "name": "Feature name as the user knows it (e.g. 'Submit Timesheet')",
                  "description": "What benefit this gives the user in plain English",
                  "usageExample": "A realistic example of a user performing this task step by step"
                }
              ],
              "recommendations": ["Helpful tip or best practice for end users"],
              "knownIssues": ["Known limitation or common problem the user may encounter, with workaround"]
            }
            """,

        DocumentationType.TechnicalDocumentation => """
            {
              "executiveSummary": "Technical summary of the system — purpose, stack, and scope",
              "technicalOverview": "Detailed explanation of how the system works internally, key classes/modules, data flow",
              "architectureDescription": "Architecture patterns used, component breakdown, database design, integrations",
              "setupInstructions": "## Prerequisites\n- list tools needed\n\n## Installation\n1. step one\n2. step two\n\n## Running Locally\n1. step",
              "configurationGuide": "All environment variables and config file options with types, defaults, and descriptions",
              "features": [
                {
                  "name": "Feature / module name",
                  "description": "Technical description: what it does, which classes are involved, how it works",
                  "usageExample": "Code snippet or command showing how to use this feature"
                }
              ],
              "apiEndpoints": [
                {
                  "method": "POST",
                  "path": "/api/example",
                  "description": "What this endpoint does and when to call it",
                  "requestBody": "{ \"field1\": \"string\", \"field2\": 0 }",
                  "responseBody": "{ \"id\": \"string\", \"status\": \"success\" }"
                }
              ],
              "dependencies": ["package-name vX.Y — reason it is used"],
              "recommendations": ["Technical improvement or refactoring recommendation"],
              "knownIssues": ["Known bug or technical debt item with suggested fix"]
            }
            """,

        DocumentationType.ApiDocumentation => """
            {
              "executiveSummary": "What this API does, its base URL, and authentication method",
              "technicalOverview": "Authentication flow, common headers, error format, rate limits, versioning",
              "apiEndpoints": [
                {
                  "method": "POST",
                  "path": "/api/resource",
                  "description": "Full description: purpose, required auth, side effects",
                  "requestBody": "{ \"requiredField\": \"string\", \"optionalField\": 0 }",
                  "responseBody": "{ \"id\": \"uuid\", \"createdAt\": \"ISO8601\", \"status\": \"string\" }"
                }
              ],
              "dependencies": ["External service or library this API depends on"],
              "recommendations": ["API design improvement or missing endpoint suggestion"],
              "knownIssues": ["Known API limitation or bug"]
            }
            """,

        DocumentationType.ArchitectureOverview => """
            {
              "executiveSummary": "One-paragraph architectural summary: what the system is and its design philosophy",
              "technicalOverview": "Technology stack with versions and rationale for each choice",
              "architectureDescription": "## Components\n- describe each\n\n## Data Flow\n- describe\n\n## Integrations\n- list\n\n## Scalability\n- notes",
              "configurationGuide": "Key infrastructure configuration and environment-specific settings",
              "dependencies": ["Major dependency and architectural role it plays"],
              "recommendations": ["Architectural improvement or scaling recommendation"],
              "knownIssues": ["Known architectural weakness or technical debt"]
            }
            """,

        _ /* FullDocumentation */ => """
            {
              "executiveSummary": "High-level overview for all audiences",
              "technicalOverview": "Technical description for developers",
              "architectureDescription": "System architecture and design patterns",
              "userGuide": "Step-by-step guide written in plain English for non-technical end users. No jargon.",
              "setupInstructions": "Developer setup and deployment steps with exact commands",
              "configurationGuide": "All configuration options and environment variables",
              "features": [
                {
                  "name": "Feature name",
                  "description": "What it does (technical) and the user benefit (non-technical)",
                  "usageExample": "How to use it — plain English for users or code for developers"
                }
              ],
              "apiEndpoints": [
                {
                  "method": "GET",
                  "path": "/api/resource",
                  "description": "What this endpoint does",
                  "requestBody": "{ \"field\": \"type\" }",
                  "responseBody": "{ \"field\": \"type\" }"
                }
              ],
              "dependencies": ["dependency — version and purpose"],
              "recommendations": ["Improvement recommendation"],
              "knownIssues": ["Known issue or limitation"]
            }
            """
    };

    // ─── System prompt (role + output rules) ─────────────────────────────────────

    private static string GetSystemPrompt(DocumentationType docType)
    {
        var role = docType switch
        {
            DocumentationType.UserGuide =>
                "You are a friendly technical writer who creates documentation for non-technical business users. " +
                "You never use programming terms. You write as if explaining to someone who has never used a computer professionally.",

            DocumentationType.TechnicalDocumentation =>
                "You are a senior software engineer and technical writer creating internal developer documentation. " +
                "Be precise, reference specific classes/modules/commands, and assume the reader can read code.",

            DocumentationType.ApiDocumentation =>
                "You are an API documentation specialist. " +
                "You write clear, accurate REST API reference docs that developers can use to integrate immediately.",

            DocumentationType.ArchitectureOverview =>
                "You are a principal software architect creating an architecture overview for senior engineers and tech leads. " +
                "Focus on system design decisions, trade-offs, and structural concerns.",

            _ =>
                "You are an expert technical writer creating documentation for a mixed audience of business users and developers. " +
                "Keep user-facing sections simple and jargon-free; keep technical sections precise and detailed."
        };

        return role + """

             IMPORTANT OUTPUT RULES — follow exactly:
             - Respond with ONLY a raw JSON object. No explanations, no markdown prose, no ```json fences.
             - Start your response with { and end with }. Nothing before or after.
             - String values use markdown for formatting: ## headings, - bullets, numbered lists as "1. Step".
             - Do NOT nest JSON objects inside string values EXCEPT apiEndpoints.requestBody and
               apiEndpoints.responseBody, which must be valid JSON payload strings.
             - requestBody and responseBody must show realistic field names and value types, not plain text descriptions.
             """;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "... [truncated]";

    // Rough token estimate: 1 token ≈ 4 characters. Keep prompt under ~60k chars.
    private static string TruncateIfNeeded(string prompt) =>
        prompt.Length > 60_000 ? prompt[..60_000] + "\n\n[Content truncated to fit token limits]" : prompt;
}
