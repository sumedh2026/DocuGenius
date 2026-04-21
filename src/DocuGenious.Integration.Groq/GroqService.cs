using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocuGenious.Core.Configuration;
using DocuGenious.Core.Interfaces;
using DocuGenious.Core.Models;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace DocuGenious.Integration.Groq;

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
        List<JiraTicket> tickets, DocumentationType docType, string? additionalContext = null)
    {
        _logger.LogInformation("Analysing {Count} JIRA ticket(s) with Groq...", tickets.Count);

        var ticketContext = BuildJiraContext(tickets);
        var userPrompt = $"""
            {GetFocusInstructions(docType)}
            {BuildAdditionalContextSection(additionalContext)}
            === JIRA TICKETS ===
            {ticketContext}

            Respond with a valid JSON object following this exact schema:
            {GetJsonSchema(docType)}
            """;

        return await CallOpenAiAsync(GetSystemPrompt(docType), userPrompt, docType,
            $"JIRA: {string.Join(", ", tickets.Select(t => t.Key))}");
    }

    public async Task<AnalysisResult> AnalyzeGitRepositoryAsync(
        GitRepositoryInfo repoInfo, DocumentationType docType, string? additionalContext = null)
    {
        _logger.LogInformation("Analysing Git repository at {Path} with Groq...", repoInfo.RepositoryPath);

        var repoContext = BuildGitContext(repoInfo);
        var userPrompt = $"""
            {GetFocusInstructions(docType)}
            {BuildAdditionalContextSection(additionalContext)}
            === GIT REPOSITORY ===
            {repoContext}

            Respond with a valid JSON object following this exact schema:
            {GetJsonSchema(docType)}
            """;

        return await CallOpenAiAsync(GetSystemPrompt(docType), userPrompt, docType,
            $"Repository: {repoInfo.RepositoryUrl ?? repoInfo.RepositoryPath}");
    }

    public async Task<AnalysisResult> AnalyzeCombinedAsync(
        List<JiraTicket> tickets, GitRepositoryInfo repoInfo, DocumentationType docType, string? additionalContext = null)
    {
        _logger.LogInformation("Analysing combined JIRA + Git context with Groq...");

        var ticketContext = BuildJiraContext(tickets);
        var repoContext   = BuildGitContext(repoInfo);
        var userPrompt = $"""
            {GetFocusInstructions(docType)}
            {BuildAdditionalContextSection(additionalContext)}
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

    private static string BuildAdditionalContextSection(string? additionalContext) =>
        string.IsNullOrWhiteSpace(additionalContext)
            ? string.Empty
            : $"""

            === ADDITIONAL CONTEXT ===
            {additionalContext.Trim()}

            """;


    // Retry delays for transient Groq server errors (5xx). 429/401 are not retried.
    private static readonly int[] RetryDelaysMs = [1500, 4000, 9000];

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
            // Increased from 4096 → 8192 for more comprehensive, untruncated output
            MaxOutputTokenCount = Math.Max(_settings.MaxTokens, 8192),
            // 0.4 gives deterministic output while allowing natural language variation
            Temperature = 0.4f
        };

        for (int attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
        {
            try
            {
                _logger.LogInformation(
                    "Calling Groq (attempt {Attempt}/{Max}, docType={DocType})...",
                    attempt + 1, RetryDelaysMs.Length + 1, docType);

                var response = await _chatClient.CompleteChatAsync(messages, options);
                var content  = response.Value.Content[0].Text;

                _logger.LogInformation(
                    "Groq responded — {Chars} chars, finish={Finish}",
                    content.Length,
                    response.Value.FinishReason);

                var result = ParseAnalysisResult(content, docType, sourceInfo);
                ValidateOutputQuality(result, docType);
                return result;
            }
            catch (ClientResultException ex) when (ex.Status == 429)
            {
                // Quota exhausted — no point retrying
                throw new InvalidOperationException(
                    "Groq quota exhausted. Check your plan at https://console.groq.com", ex);
            }
            catch (ClientResultException ex) when (ex.Status == 401)
            {
                // Bad key — no point retrying
                throw new InvalidOperationException(
                    "Groq API key is invalid. Verify your key at https://console.groq.com/keys", ex);
            }
            catch (ClientResultException ex) when (ex.Status >= 500 && attempt < RetryDelaysMs.Length)
            {
                // Transient server error — retry after delay
                _logger.LogWarning(
                    "Groq server error {Status} on attempt {Attempt}. Retrying in {Delay}ms…",
                    ex.Status, attempt + 1, RetryDelaysMs[attempt]);
                await Task.Delay(RetryDelaysMs[attempt]);
            }
            catch (Exception ex) when (ex is not InvalidOperationException && attempt < RetryDelaysMs.Length)
            {
                // Network / timeout — retry
                _logger.LogWarning(ex,
                    "Groq call failed on attempt {Attempt} ({Type}). Retrying in {Delay}ms…",
                    attempt + 1, ex.GetType().Name, RetryDelaysMs[attempt]);
                await Task.Delay(RetryDelaysMs[attempt]);
            }
        }

        // Final attempt — let any exception propagate naturally
        var finalResponse = await _chatClient.CompleteChatAsync(messages, options);
        var finalContent  = finalResponse.Value.Content[0].Text;
        var finalResult   = ParseAnalysisResult(finalContent, docType, sourceInfo);
        ValidateOutputQuality(finalResult, docType);
        return finalResult;
    }

    // Reuse a single options instance — creating JsonSerializerOptions inline is expensive
    // and can trigger validation issues in .NET 9/10. JsonStringEnumConverter prevents
    // enum fields (e.g. DocumentationType) from causing a JsonException.
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private AnalysisResult ParseAnalysisResult(string rawContent, DocumentationType docType, string sourceInfo)
    {
        var candidates = ExtractJsonCandidates(rawContent).ToList();

        // Try each candidate in order — stop at the first successful parse
        foreach (var (candidate, index) in candidates.Select((c, i) => (c, i)))
        {
            try
            {
                var result = JsonSerializer.Deserialize<AnalysisResult>(candidate, _jsonOptions);
                if (result != null && HasMeaningfulContent(result))
                {
                    result.DocumentationType = docType;
                    result.SourceInfo        = sourceInfo;
                    result.GeneratedAt       = DateTime.UtcNow;
                    _logger.LogDebug("Groq response parsed successfully using candidate {Index}", index);
                    return result;
                }

                _logger.LogWarning(
                    "Candidate {Index} deserialised but HasMeaningfulContent=false. " +
                    "ExecutiveSummary='{ES}' TechnicalOverview='{TO}' Features={FC}",
                    index,
                    result?.ExecutiveSummary?[..Math.Min(80, result.ExecutiveSummary.Length)] ?? "(null)",
                    result?.TechnicalOverview?[..Math.Min(80, result.TechnicalOverview.Length)] ?? "(null)",
                    result?.Features.Count ?? 0);
            }
            catch (JsonException jex)
            {
                _logger.LogWarning(
                    "Candidate {Index} failed JSON deserialisation: {Msg} (path: {Path})",
                    index, jex.Message, jex.Path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Candidate {Index} threw unexpected exception", index);
            }
        }

        // Hard fallback — log the raw response so it's visible in the API logs
        _logger.LogError(
            "All {Count} Groq response candidates failed to parse. " +
            "Raw content (first 500 chars): {Raw}",
            candidates.Count,
            rawContent.Length > 500 ? rawContent[..500] + "…" : rawContent);

        return new AnalysisResult
        {
            ExecutiveSummary  = rawContent,
            DocumentationType = docType,
            SourceInfo        = sourceInfo,
            GeneratedAt       = DateTime.UtcNow
        };
    }

    // =========================================================================
    // Output quality validator
    // =========================================================================

    /// <summary>
    /// Scores the generated output and logs warnings for thin or missing sections.
    /// Does NOT block generation — purely diagnostic so developers can improve prompts.
    /// </summary>
    private void ValidateOutputQuality(AnalysisResult result, DocumentationType docType)
    {
        var issues  = new List<string>();
        int score   = 100;

        // ── Shared checks ─────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(result.ExecutiveSummary))
        { issues.Add("executiveSummary is empty"); score -= 20; }
        else if (result.ExecutiveSummary.Split(' ').Length < 30)
        { issues.Add($"executiveSummary is very short ({result.ExecutiveSummary.Split(' ').Length} words, expected 50+)"); score -= 10; }

        // ── Doc-type specific checks ───────────────────────────────────────────
        switch (docType)
        {
            case DocumentationType.TechnicalDocumentation:
            case DocumentationType.FullDocumentation:
                if (string.IsNullOrWhiteSpace(result.TechnicalOverview))
                { issues.Add("technicalOverview is empty"); score -= 15; }
                else if (result.TechnicalOverview.Split(' ').Length < 80)
                { issues.Add($"technicalOverview is thin ({result.TechnicalOverview.Split(' ').Length} words)"); score -= 8; }

                if (string.IsNullOrWhiteSpace(result.SetupInstructions))
                { issues.Add("setupInstructions is empty"); score -= 10; }

                if (string.IsNullOrWhiteSpace(result.ConfigurationGuide))
                { issues.Add("configurationGuide is empty"); score -= 8; }

                if (result.Dependencies.Count == 0)
                { issues.Add("dependencies list is empty"); score -= 5; }
                break;

            case DocumentationType.UserGuide:
                if (string.IsNullOrWhiteSpace(result.UserGuide))
                { issues.Add("userGuide body is empty"); score -= 25; }
                else if (result.UserGuide.Split(' ').Length < 100)
                { issues.Add($"userGuide body is thin ({result.UserGuide.Split(' ').Length} words, expected 200+)"); score -= 12; }
                break;

            case DocumentationType.ApiDocumentation:
                if (result.ApiEndpoints.Count == 0)
                { issues.Add("no API endpoints documented"); score -= 30; }
                else
                {
                    var emptyBodies = result.ApiEndpoints.Count(e =>
                        string.IsNullOrWhiteSpace(e.RequestBody) && string.IsNullOrWhiteSpace(e.ResponseBody));
                    if (emptyBodies > 0)
                        issues.Add($"{emptyBodies} endpoint(s) have no request/response bodies");
                }
                break;

            case DocumentationType.ArchitectureOverview:
                if (string.IsNullOrWhiteSpace(result.ArchitectureDescription))
                { issues.Add("architectureDescription is empty"); score -= 20; }
                else if (result.ArchitectureDescription.Split(' ').Length < 80)
                { issues.Add($"architectureDescription is thin ({result.ArchitectureDescription.Split(' ').Length} words)"); score -= 10; }
                break;
        }

        // ── Universal array checks ─────────────────────────────────────────────
        if (result.Recommendations.Count == 0)
        { issues.Add("recommendations list is empty"); score -= 5; }

        score = Math.Max(0, score);

        if (issues.Count == 0)
        {
            _logger.LogInformation("Output quality check PASSED — score {Score}/100", score);
        }
        else
        {
            _logger.LogWarning(
                "Output quality score {Score}/100 — {Count} issue(s): {Issues}",
                score, issues.Count, string.Join(" | ", issues));
        }
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
            with NO programming knowledge. They have never seen source code.

            TONE: Friendly, warm, jargon-free. Replace every technical term with plain English.
            Write "click the blue Submit button" not "invoke the POST endpoint".

            REQUIRED SECTIONS (use ## headings in the userGuide field):
            ## What This Does          — 2-3 sentences: benefit to the user, not technical detail
            ## Getting Started         — prerequisites the user needs (account, access, browser)
            ## Step-by-Step Guide      — numbered steps for every task; describe what the user SEES at each step
            ## Common Questions        — 3+ real questions a user would ask, with clear answers
            ## Troubleshooting         — 3+ common problems and exactly how to fix them

            FEATURES ARRAY: Name each feature as the user knows it (e.g. "Generate a Document",
            "Download PDF"). Describe the user benefit. UsageExample must be a realistic
            scenario: "Sarah opens the app, selects JIRA Only, types PROJ-42, clicks Create Document,
            then downloads the PDF in 30 seconds."

            EXCLUDE: Source code, architecture, class names, API internals, deployment steps,
            database schemas, configuration files.
            """,

        DocumentationType.TechnicalDocumentation => """
            Produce TECHNICAL DOCUMENTATION for this codebase/feature.

            TARGET AUDIENCE: Software engineers, DevOps engineers, and maintainers who will
            build on, debug, or deploy this system.

            TONE: Precise, dense, engineering-focused. Reference actual class names, method
            signatures, file paths, and config keys visible in the source data.

            REQUIRED SECTIONS:
            technicalOverview   — explain the system's purpose, runtime environment, and key design
                                  decisions; name the actual projects/modules and their responsibilities
            architectureDescription — describe components and their relationships; explain data flow
                                  from API request to PDF response; list integration points
            setupInstructions   — every command verbatim:
                                  ## Prerequisites (SDK versions, tools)
                                  ## Clone & Build (git clone, dotnet build, dotnet restore)
                                  ## Configuration (which file to edit, which keys to set)
                                  ## Running Locally (dotnet run, which port, what to open)
            configurationGuide  — every key in appsettings.json: name, type, example value, effect

            FEATURES ARRAY: One entry per major module/service (e.g. "JIRA Integration",
            "Groq Analysis", "PDF Generation"). Description = which classes are involved and
            how they work. UsageExample = code snippet or curl command.

            API ENDPOINTS: Document every controller endpoint found in the source with realistic
            request/response JSON matching the actual model properties.

            DEPENDENCIES: Every NuGet package with version and specific reason it is used.
            """,

        DocumentationType.ApiDocumentation => """
            Produce API REFERENCE DOCUMENTATION for this system.

            TARGET AUDIENCE: Developers integrating with or consuming this API for the first time.
            They need to be productive immediately — no guessing, no assumptions.

            TONE: Concise, precise, reference-style. Every endpoint must be immediately usable.

            REQUIRED CONTENT:
            executiveSummary    — base URL, authentication method, content-type, brief purpose
            technicalOverview   — authentication flow step-by-step; common headers; standard error
                                  response shape; rate limits if known; API versioning

            API ENDPOINTS ARRAY — document EVERY endpoint visible in the source code:
            • method / path     — exact HTTP verb and full route (e.g. POST /api/documentation/generate)
            • description       — what it does, when to call it, any side effects, auth required
            • requestBody       — complete JSON with EVERY field, realistic example values, and
                                  comments showing required vs optional (embed as JSON string)
            • responseBody      — success shape AND error shape (400, 500) with realistic values

            EXCLUDE: UI/UX instructions, business narrative, deployment steps.
            """,

        DocumentationType.ArchitectureOverview => """
            Produce an ARCHITECTURE OVERVIEW for this system.

            TARGET AUDIENCE: Senior engineers, architects, and tech leads who need to understand
            the system holistically before extending, scaling, or reviewing it.

            TONE: High-level, strategic. Use diagram-description prose. Name actual services.

            REQUIRED SECTIONS (use ## headings in architectureDescription):
            ## System Overview      — what problem it solves; overall design philosophy
            ## Components           — every project/service; its role; what it owns
            ## Data Flow            — trace a request end-to-end from UI input to PDF download
            ## External Integrations— name actual third-party services, APIs, their purpose
            ## Technology Stack     — each major technology, version, and WHY it was chosen
            ## Scalability & Limits — current bottlenecks; where horizontal scaling would help
            ## Security Considerations — credential handling, CORS, auth, known gaps
            ## Recommendations      — top 3 architectural improvements with rationale

            DEPENDENCIES: Name actual libraries with their architectural role
            (e.g. "LibGit2Sharp 0.30 — used for local Git repo analysis and branch checkout").

            EXCLUDE: Step-by-step user tasks, low-level implementation code, line-by-line analysis.
            """,

        _ => """
            Produce COMPREHENSIVE FULL DOCUMENTATION covering all audiences.

            This document will be read by business stakeholders, end users, developers, and architects.
            Every section must be complete and audience-appropriate.

            REQUIRED CONTENT:
            executiveSummary        — what the system is, who uses it, and the core value it delivers
            technicalOverview       — architecture, key components, technology stack, data flow
            architectureDescription — components, integrations, communication patterns, scalability notes
            userGuide               — plain-English step-by-step guide; ## sections; numbered steps
            setupInstructions       — every command needed to clone, configure, and run locally
            configurationGuide      — every config key with type, example value, and effect
            features[]              — every feature with a user-friendly name, description, and example
            apiEndpoints[]          — every endpoint with realistic request/response JSON
            dependencies[]          — every package with version and purpose
            recommendations[]       — at least 5 actionable improvement suggestions
            knownIssues[]           — real limitations and technical debt observed in the source

            Quality bar: a developer reading this should be able to run the system locally within
            30 minutes. A non-technical manager should understand what it does in 2 minutes.
            """
    };

    // ─── Per-doc-type: tailored JSON schemas ──────────────────────────────────────

    private static string GetJsonSchema(DocumentationType docType) => docType switch
    {
        DocumentationType.UserGuide => """
            {
              "executiveSummary": "3 plain-English sentences: (1) what this product does, (2) who uses it, (3) the core value it delivers. No technical terms. Example: 'Docu-Genius automatically creates professional documentation from your project's JIRA tickets and source code. It is designed for project managers, developers, and business analysts who need up-to-date docs without writing them manually. In under a minute, you get a polished PDF ready to share with your team or stakeholders.'",
              "userGuide": "## Getting Started\n[Prerequisites and access requirements]\n\n## How to Create a Document\n1. [First step with what user sees]\n2. [Second step]\n3. [Continue for every step]\n\n## Understanding Your Results\n[What the output looks like, how to read it]\n\n## Downloading and Sharing\n[Steps to download and share the PDF]\n\n## Common Questions\n[3+ Q&A pairs]\n\n## Troubleshooting\n[3+ problems and exact solutions]",
              "features": [
                {
                  "name": "User-facing feature name (e.g. 'Generate from JIRA Ticket')",
                  "description": "Plain-English benefit: what problem it solves for the user. No technical terms.",
                  "usageExample": "Concrete scenario: 'Maria selects JIRA Only, types TMS-45 into the ticket field, adds context note about sprint 3, clicks Create Document, and downloads a 12-page PDF within 40 seconds.'"
                }
              ],
              "recommendations": [
                "Practical tip a user should know — e.g. 'For best results, ensure your JIRA ticket has a detailed description and acceptance criteria before generating documentation.'"
              ],
              "knownIssues": [
                "Known limitation with its workaround — e.g. 'If the document shows incomplete content, the JIRA ticket may have had minimal description. Add more detail to the ticket and regenerate.'"
              ]
            }
            """,

        DocumentationType.TechnicalDocumentation => """
            {
              "executiveSummary": "3-4 sentences: purpose of the system, technology stack (framework + language + key libraries), deployment target, and scope. Reference actual project names from the source.",
              "technicalOverview": "## System Purpose\n[What problem this solves and for whom]\n\n## Project Structure\n[Each project/module, its responsibility, and key classes]\n\n## Request Lifecycle\n[Step-by-step: how a request flows from entry point through services to response]\n\n## Key Design Decisions\n[Why specific patterns/libraries were chosen — reference actual names from source]",
              "architectureDescription": "## Architecture Pattern\n[Pattern used — e.g. layered, clean, microservices — with justification]\n\n## Component Breakdown\n[Every project with its role and dependencies]\n\n## Data Flow\n[Trace a specific use case end-to-end with actual class/method names]\n\n## External Integrations\n[Every third-party service: name, SDK, purpose, authentication method]",
              "setupInstructions": "## Prerequisites\n- [SDK name and minimum version]\n- [Other required tools]\n\n## Clone the Repository\n```\ngit clone [url]\ncd [folder]\n```\n\n## Restore Dependencies\n```\ndotnet restore\n```\n\n## Configure Settings\n1. Copy appsettings.json to appsettings.Development.json\n2. Fill in: [list every key that needs a real value]\n\n## Run Locally\n```\ndotnet run --project src/[ProjectName]\n```\nAPI available at: [url]\n\n## Verify\n[How to confirm it is working — e.g. open Swagger at /swagger]",
              "configurationGuide": "## appsettings.json Keys\n\n[Section.Key] — Type: string | Required: yes | Default: none\nEffect: [what this controls]\nExample: \"[realistic example value]\"\n\n[Repeat for every key in the source]",
              "features": [
                {
                  "name": "Actual module name from source (e.g. 'JIRA Integration Service')",
                  "description": "Technical description: which interface/class implements this, what it does internally, key methods, error handling approach.",
                  "usageExample": "Code snippet or curl command showing real usage with actual field names from the source model."
                }
              ],
              "apiEndpoints": [
                {
                  "method": "POST",
                  "path": "/api/actual/path",
                  "description": "What this endpoint does, when to call it, what it returns, required auth headers.",
                  "requestBody": "{ \"actualField1\": \"string value\", \"actualField2\": 0, \"optionalField\": null }",
                  "responseBody": "{ \"success\": true, \"actualResponseField\": \"value\", \"errorMessage\": null }"
                }
              ],
              "dependencies": [
                "PackageName vX.Y.Z — specific reason: e.g. 'LibGit2Sharp v0.30.0 — used to clone remote repositories and enumerate commit history for analysis'"
              ],
              "recommendations": [
                "Specific, actionable improvement with rationale — e.g. 'Add IMemoryCache to cache JIRA ticket data for 5 minutes to reduce API calls during batch processing.'"
              ],
              "knownIssues": [
                "Specific technical issue observed in the source with suggested fix — e.g. 'The Git clone directory is never cleaned up after analysis, causing disk usage to grow. Add a cleanup step in GitService after analysis completes.'"
              ]
            }
            """,

        DocumentationType.ApiDocumentation => """
            {
              "executiveSummary": "Base URL, authentication method (Bearer token / API key / none), content-type (application/json), and 1-sentence purpose. Example: 'The Docu-Genius API (base URL: https://localhost:60735) accepts JSON requests authenticated via CORS from approved origins. It generates AI-powered documentation PDFs from JIRA tickets and Git repositories.'",
              "technicalOverview": "## Authentication\n[Step-by-step: how to authenticate, which header, what value format]\n\n## Common Headers\n[Every header with name, value format, and whether required]\n\n## Error Response Format\n[Standard error shape with field descriptions]\nExample: { \"message\": \"string\", \"traceId\": \"string\" }\n\n## HTTP Status Codes Used\n[Every status code returned by this API with meaning]",
              "apiEndpoints": [
                {
                  "method": "POST",
                  "path": "/api/actual/route",
                  "description": "Full description: what this does, when to call it, prerequisites, side effects (e.g. 'Creates a PDF file on the server and returns its filename. The file is available for download via GET /api/documentation/download/{fileName} for the duration of the server session.').",
                  "requestBody": "{ \"requiredField\": \"actual-example-value\", \"anotherRequired\": \"JiraOnly\", \"optionalField\": null }",
                  "responseBody": "{ \"success\": true, \"fileName\": \"PROJ-123_FullDocumentation_20250421.pdf\", \"filePath\": \"./output/PROJ-123_FullDocumentation_20250421.pdf\" }"
                }
              ],
              "dependencies": [
                "External service name — how this API depends on it and what happens if it is unavailable"
              ],
              "recommendations": [
                "Specific API improvement — e.g. 'Add a GET /api/documentation/status/{jobId} endpoint to support async generation with polling instead of a blocking 60-second POST request.'"
              ],
              "knownIssues": [
                "Real API limitation — e.g. 'The /generate endpoint blocks the HTTP connection for the full generation time (20-60 seconds). Large repos or multiple tickets may cause client timeouts. Implement background jobs with a polling endpoint to fix this.'"
              ]
            }
            """,

        DocumentationType.ArchitectureOverview => """
            {
              "executiveSummary": "1 substantial paragraph: what the system is, its design philosophy (e.g. clean architecture, layered), the core workflow from user input to output, and the primary technology choices.",
              "technicalOverview": "## Technology Stack\n[Every major technology with version and WHY it was chosen over alternatives]\n\n## Project Dependencies\n[How projects reference each other — which project depends on which]\n\n## Runtime Environment\n[OS, SDK version, hosting model, expected load]",
              "architectureDescription": "## System Overview\n[Design goals and architectural constraints]\n\n## Components and Responsibilities\n[Every project/service with its single responsibility and the interfaces it exposes]\n\n## Data Flow\n[Trace the full lifecycle: UI input → API → JIRA/Git fetch → Groq analysis → PDF generation → download]\n\n## External Integrations\n[Every third-party system: name, purpose, authentication, failure behaviour]\n\n## Scalability Considerations\n[Current bottlenecks, where the system would break under load, recommended fixes]\n\n## Security Posture\n[Credential handling, CORS configuration, known gaps, recommendations]",
              "configurationGuide": "Infrastructure-level configuration: environment variables, server settings, CORS allowed origins, output directory, and how to configure each for production vs development.",
              "dependencies": [
                "LibraryName vX.Y — architectural role: e.g. 'QuestPDF v2024.10.2 — chosen for its code-first PDF generation API that integrates naturally with C# models, avoiding template files and allowing dynamic layout.'"
              ],
              "recommendations": [
                "Architectural recommendation with clear rationale and estimated effort — e.g. 'Extract document generation into a background job (e.g. Hangfire) to eliminate the blocking 60-second HTTP request. Estimated effort: 1-2 days. Benefit: eliminates timeout risk and enables progress streaming.'"
              ],
              "knownIssues": [
                "Architectural weakness observed in source — e.g. 'All services are registered as Singleton, including GitService which clones repos to disk. Concurrent requests will share state and may conflict. Switch Git operations to Scoped or use a semaphore.'"
              ]
            }
            """,

        _ /* FullDocumentation */ => """
            {
              "executiveSummary": "3-4 sentences suitable for a non-technical manager AND a developer: what the system does, who uses it, the technology stack, and the core value delivered.",
              "technicalOverview": "## Purpose and Scope\n[Problem solved, system boundaries]\n\n## Architecture\n[Key components and how they interact — reference actual names]\n\n## Request Lifecycle\n[End-to-end flow with actual class/method names]\n\n## Technology Stack\n[Every major technology with justification]",
              "architectureDescription": "## Components\n[Every project with its responsibility]\n\n## Data Flow\n[UI → API → integrations → output — step by step]\n\n## External Services\n[Every third-party with auth method and failure behaviour]\n\n## Scalability & Security\n[Current limits and known gaps]",
              "userGuide": "## What This Does\n[Plain-English benefit]\n\n## Getting Started\n[Prerequisites for end users]\n\n## Step-by-Step: Create a Document\n1. [Step with what user sees]\n2. [Continue all steps]\n\n## Download Your Document\n[Steps]\n\n## Troubleshooting\n[3+ common problems with solutions]",
              "setupInstructions": "## Prerequisites\n- [SDK + version]\n- [Tools]\n\n## Clone & Build\n```\ngit clone [url]\ndotnet restore\ndotnet build\n```\n\n## Configure\n[Which file, which keys, example values]\n\n## Run\n```\ndotnet run --project src/[Project]\n```",
              "configurationGuide": "[Every appsettings key: name, type, example, effect]",
              "features": [
                {
                  "name": "Actual feature name from source",
                  "description": "Technical + user-facing description. Reference actual classes and user benefit.",
                  "usageExample": "Concrete realistic example — code snippet OR user scenario."
                }
              ],
              "apiEndpoints": [
                {
                  "method": "POST",
                  "path": "/api/actual/path",
                  "description": "Purpose, required fields, side effects.",
                  "requestBody": "{ \"actualField\": \"realistic-value\", \"sourceType\": \"JiraOnly\" }",
                  "responseBody": "{ \"success\": true, \"fileName\": \"output.pdf\" }"
                }
              ],
              "dependencies": [
                "PackageName vX.Y — specific purpose in this project"
              ],
              "recommendations": [
                "Specific actionable improvement with rationale and estimated effort"
              ],
              "knownIssues": [
                "Specific issue from source with suggested fix"
              ]
            }
            """
    };

    // ─── System prompt (role + output rules) ─────────────────────────────────────

    private static string GetSystemPrompt(DocumentationType docType)
    {
        var role = docType switch
        {
            DocumentationType.UserGuide =>
                "You are a friendly technical writer creating documentation for non-technical business users. " +
                "You never use programming or technical jargon. Write as if explaining to someone who has " +
                "never seen source code — describe what the user sees and does, not how it is built.",

            DocumentationType.TechnicalDocumentation =>
                "You are a senior software engineer and technical writer creating internal developer documentation. " +
                "Be precise, reference the actual class names, method signatures, file paths, and configuration " +
                "keys visible in the source data. Assume the reader can read code and wants depth, not generalities.",

            DocumentationType.ApiDocumentation =>
                "You are an API documentation specialist creating a reference guide developers can use to " +
                "integrate immediately. Document every endpoint found in the source with complete, realistic " +
                "JSON request and response examples — not generic placeholders.",

            DocumentationType.ArchitectureOverview =>
                "You are a principal software architect creating an architecture overview for senior engineers " +
                "and tech leads. Name actual components, services, databases, and communication channels from " +
                "the source. Explain design decisions, trade-offs, and how the pieces fit together.",

            _ =>
                "You are an expert technical writer creating comprehensive documentation for a mixed audience " +
                "of business users and developers. Keep user-facing sections jargon-free and step-by-step; " +
                "keep technical sections precise, deep, and referencing actual source details."
        };

        return role + """


            ═══════════════════════════════════════════════════════
            CONTENT QUALITY STANDARDS — these are strictly enforced
            ═══════════════════════════════════════════════════════

            1. SPECIFICITY — use actual names and values from the source data, never invent them.
               ✗ WRONG: "The service processes user requests and returns results."
               ✓ RIGHT: "The DocumentationController.GenerateDocumentation() endpoint accepts a POST
                          request containing SourceType, JiraTicketIds, and GitRepositoryUrl, then
                          orchestrates calls to JiraService, GroqService, and PdfService in sequence."

            2. DEPTH — each major text field must be substantive:
               • executiveSummary       → minimum 3 sentences covering WHAT, WHY, and WHO
               • technicalOverview      → minimum 3 paragraphs; use ## headings for each topic
               • architectureDescription→ minimum 3 paragraphs; describe components, data flow, integrations
               • userGuide              → minimum 4 numbered steps per task; include what the user sees
               • setupInstructions      → every command listed explicitly; no "install dependencies" without the command
               • configurationGuide     → every config key, its type, default value, and effect

            3. ACCURACY — never hallucinate. Only describe what is explicitly present in the source.
               If data is sparse, write: "Based on available source information, [what is known].
               Full documentation of [area] would require [what is missing]."

            4. ARRAYS — populate every list with at least 2–3 substantive items when the source supports it.
               Empty arrays [] are only acceptable when the source genuinely has nothing to list.

            5. NO TRUNCATION — write complete sentences; never end with "etc.", "…", or "and more".
               If a section needs more space, keep writing — do not cut it short.

            ═══════════════════════════════════════════════════════
            OUTPUT FORMAT — follow exactly
            ═══════════════════════════════════════════════════════

            • Respond with ONLY a raw JSON object. Zero prose, zero explanations, zero ```json fences.
            • First character must be {, last character must be }. Nothing before or after.
            • String field values use markdown: ## for headings, - for bullets, 1. 2. 3. for steps.
            • Do NOT embed JSON objects inside string values EXCEPT apiEndpoints.requestBody and
              apiEndpoints.responseBody, which must contain realistic JSON payload examples.
            • requestBody / responseBody: show actual field names and realistic value types — not descriptions.
            """;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "... [truncated]";

    // Rough token estimate: 1 token ≈ 4 characters. Keep prompt under ~60k chars.
    private static string TruncateIfNeeded(string prompt) =>
        prompt.Length > 60_000 ? prompt[..60_000] + "\n\n[Content truncated to fit token limits]" : prompt;
}
