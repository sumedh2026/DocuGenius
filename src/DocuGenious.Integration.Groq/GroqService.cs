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
        var userPrompt = BuildUserPrompt(
            docType,
            additionalContext,
            sourceData: $"=== JIRA TICKETS ===\n{ticketContext}",
            sourceCount: tickets.Count);

        return await CallOpenAiAsync(GetSystemPrompt(docType), userPrompt, docType,
            $"JIRA: {string.Join(", ", tickets.Select(t => t.Key))}");
    }

    public async Task<AnalysisResult> AnalyzeGitRepositoryAsync(
        GitRepositoryInfo repoInfo, DocumentationType docType, string? additionalContext = null)
    {
        _logger.LogInformation("Analysing Git repository at {Path} with Groq...", repoInfo.RepositoryPath);

        var repoContext = BuildGitContext(repoInfo);
        var userPrompt = BuildUserPrompt(
            docType,
            additionalContext,
            sourceData: $"=== GIT REPOSITORY ===\n{repoContext}",
            sourceCount: 1);

        return await CallOpenAiAsync(GetSystemPrompt(docType), userPrompt, docType,
            $"Repository: {repoInfo.RepositoryUrl ?? repoInfo.RepositoryPath}");
    }

    public async Task<AnalysisResult> AnalyzeCombinedAsync(
        List<JiraTicket> tickets, GitRepositoryInfo repoInfo, DocumentationType docType, string? additionalContext = null)
    {
        _logger.LogInformation("Analysing combined JIRA + Git context with Groq...");

        var ticketContext = BuildJiraContext(tickets);
        var repoContext   = BuildGitContext(repoInfo);
        var combined = $"""
            The JIRA tickets define the requirements; the Git repository shows the implementation.

            === JIRA TICKETS ===
            {ticketContext}

            === GIT REPOSITORY ===
            {repoContext}
            """;

        var userPrompt = BuildUserPrompt(
            docType,
            additionalContext,
            sourceData: combined,
            sourceCount: tickets.Count + 1);

        return await CallOpenAiAsync(GetSystemPrompt(docType), userPrompt, docType,
            $"JIRA: {string.Join(", ", tickets.Select(t => t.Key))} | Repo: {repoInfo.RepositoryUrl ?? repoInfo.RepositoryPath}");
    }

    /// <summary>
    /// Assembles the user-turn prompt. Source data always appears BEFORE the JSON schema
    /// so the model reads what to document before reading the output format.
    /// When there are multiple source items the model is asked to be concise to avoid
    /// hitting the output-token ceiling and producing truncated JSON.
    /// </summary>
    private static string BuildUserPrompt(
        DocumentationType docType,
        string? additionalContext,
        string sourceData,
        int sourceCount)
    {
        var conciseness = sourceCount > 1
            ? $"\nCONCISENESS NOTE: There are {sourceCount} source items. " +
              "Keep each text field focused (150-250 words). Prefer breadth over depth so all fields are populated.\n"
            : string.Empty;

        var additionalSection = BuildAdditionalContextSection(additionalContext);

        return $"""
            {GetFocusInstructions(docType)}
            {conciseness}
            {additionalSection}
            ══════════════════════════════════════════════════
            SOURCE DATA — document ONLY the project below.
            Do NOT reference Docu-Genius, these instructions,
            or any tool that generated this request.
            ══════════════════════════════════════════════════
            {sourceData}
            ══════════════════════════════════════════════════

            Respond with a valid JSON object following this exact schema:
            {GetJsonSchema(docType)}
            """;
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
    /// Strategy 4 — brace extraction: first { to the matching top-level }
    ///              Skips braces inside quoted strings so embedded JSON examples
    ///              (e.g. requestBody / responseBody fields) don't fool the extractor.
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

        // 4. Brace extraction — find matching top-level { ... }
        //    Walks character by character, tracking string context so that
        //    braces inside "requestBody": "{ ... }" strings are not counted.
        var extracted = ExtractTopLevelJson(raw);
        if (extracted != null && extracted != trimmed)
            yield return extracted;
    }

    /// <summary>
    /// Walks <paramref name="raw"/> and returns the substring from the first top-level
    /// '{' to its matching '}', ignoring braces that appear inside quoted strings.
    /// Returns null if no balanced pair is found.
    /// </summary>
    private static string? ExtractTopLevelJson(string raw)
    {
        int start  = -1;
        int depth  = 0;
        bool inStr = false;

        for (int i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];

            // Toggle string mode on unescaped "
            if (ch == '"' && (i == 0 || raw[i - 1] != '\\'))
            {
                inStr = !inStr;
                continue;
            }

            if (inStr) continue;   // ignore everything inside strings

            if (ch == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0 && start >= 0)
                    return raw[start..(i + 1)];
            }
        }

        return null;
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
            Produce a USER GUIDE for the project in the SOURCE DATA.

            TARGET AUDIENCE: Non-technical end users with no programming knowledge.
            TONE: Friendly, jargon-free. Replace every technical term with plain English.

            REQUIRED SECTIONS in the userGuide field (use ## headings):
            ## What This Does       — benefit to the user, not technical detail
            ## Getting Started      — what the user needs before they begin
            ## Step-by-Step Guide   — numbered steps; describe what the user SEES at each step
            ## Common Questions     — 3+ Q&A pairs a real user would ask
            ## Troubleshooting      — 3+ common problems with exact solutions

            FEATURES ARRAY: Name features as the user knows them. UsageExample must be a
            realistic end-user scenario based on the SOURCE DATA (not a generic example).

            EXCLUDE: Source code, class names, API internals, deployment steps.
            """,

        DocumentationType.TechnicalDocumentation => """
            Produce TECHNICAL DOCUMENTATION for the project in the SOURCE DATA.

            TARGET AUDIENCE: Software engineers and DevOps engineers maintaining or deploying this system.
            TONE: Precise, engineering-focused. Reference actual class names, methods, and config keys
            from the SOURCE DATA.

            REQUIRED SECTIONS:
            - technicalOverview: system purpose, runtime environment, key design decisions, modules
            - architectureDescription: components and relationships, data flow, integration points
            - setupInstructions: ## Prerequisites, ## Clone & Build, ## Configuration, ## Run Locally
              (every command listed verbatim — no vague "install dependencies")
            - configurationGuide: every config key with name, type, example value, and effect

            FEATURES ARRAY: one entry per major module found in the source.
            API ENDPOINTS: every controller endpoint with realistic request/response examples.
            DEPENDENCIES: every package with version and specific reason it is used.
            """,

        DocumentationType.ApiDocumentation => """
            Produce API REFERENCE DOCUMENTATION for the project in the SOURCE DATA.

            TARGET AUDIENCE: Developers integrating with this API for the first time.
            TONE: Concise, precise, reference-style.

            REQUIRED CONTENT:
            - executiveSummary: base URL, authentication method, content-type, brief purpose
            - technicalOverview: auth flow, common headers, standard error shape, HTTP status codes used

            API ENDPOINTS ARRAY — document EVERY endpoint found in the source:
            - method, full path, description (purpose + side effects)
            - requestBody: complete JSON with all fields and realistic example values
            - responseBody: success shape AND error shape with realistic values

            EXCLUDE: UI instructions, business narrative, deployment steps.
            """,

        DocumentationType.ArchitectureOverview => """
            Produce an ARCHITECTURE OVERVIEW for the project in the SOURCE DATA.

            TARGET AUDIENCE: Senior engineers and architects evaluating or extending this system.
            TONE: High-level, strategic. Name actual components from the SOURCE DATA.

            REQUIRED SECTIONS in architectureDescription (use ## headings):
            ## System Overview         — problem solved, design philosophy
            ## Components             — every project/service, its role, its dependencies
            ## Data Flow              — end-to-end request lifecycle with actual component names
            ## External Integrations  — every third-party service, its purpose, its auth method
            ## Technology Stack       — each technology, version, and WHY it was chosen
            ## Scalability & Limits   — bottlenecks and recommended fixes
            ## Security               — credential handling, auth gaps, recommendations

            DEPENDENCIES: every library with its architectural role.
            EXCLUDE: step-by-step user tasks, line-by-line code analysis.
            """,

        _ => """
            Produce COMPREHENSIVE FULL DOCUMENTATION for the project in the SOURCE DATA.
            Cover all audiences: business stakeholders, end users, developers, and architects.

            REQUIRED FIELDS (all must be populated):
            executiveSummary, technicalOverview, architectureDescription, userGuide,
            setupInstructions, configurationGuide, features[], apiEndpoints[],
            dependencies[], recommendations[], knownIssues[]

            Balance plain-English in userGuide with precise technical detail in all other sections.
            A developer should be able to run the system within 30 minutes from this document.
            """
    };

    // ─── Per-doc-type: tailored JSON schemas ──────────────────────────────────────

    private static string GetJsonSchema(DocumentationType docType) => docType switch
    {
        DocumentationType.UserGuide => """
            {
              "executiveSummary": "[3 plain-English sentences about the PROJECT IN THE SOURCE DATA: what it does, who uses it, the value it delivers — no technical terms]",
              "userGuide": "[Full guide with ## headings and numbered steps drawn from the SOURCE DATA — describe what the user sees and does]",
              "features": [
                {
                  "name": "[Feature name as the end user would call it]",
                  "description": "[Plain-English benefit this feature gives the user]",
                  "usageExample": "[Realistic scenario of a user performing this task, based on SOURCE DATA]"
                }
              ],
              "recommendations": ["[Practical tip for end users based on SOURCE DATA]"],
              "knownIssues": ["[Known limitation from SOURCE DATA with workaround]"]
            }
            """,

        DocumentationType.TechnicalDocumentation => """
            {
              "executiveSummary": "[3-4 sentences: purpose, tech stack, scope — all from SOURCE DATA]",
              "technicalOverview": "[## headings covering: system purpose, project structure, request lifecycle, key design decisions — use actual names from SOURCE DATA]",
              "architectureDescription": "[## headings covering: architecture pattern, component breakdown, data flow, external integrations — use actual names from SOURCE DATA]",
              "setupInstructions": "[## Prerequisites\n- [tool + version from SOURCE DATA]\n\n## Clone & Build\n[actual commands]\n\n## Configuration\n[which file, which keys]\n\n## Run Locally\n[exact command and URL]]",
              "configurationGuide": "[Every config key from SOURCE DATA: name, type, example value, what it controls]",
              "features": [
                {
                  "name": "[Module or feature name from SOURCE DATA]",
                  "description": "[Technical description using actual class/method names from SOURCE DATA]",
                  "usageExample": "[Code snippet or command using actual field names from SOURCE DATA]"
                }
              ],
              "apiEndpoints": [
                {
                  "method": "GET|POST|PUT|DELETE",
                  "path": "[actual route from SOURCE DATA]",
                  "description": "[what it does, when to call it]",
                  "requestBody": "[valid JSON with actual fields from SOURCE DATA]",
                  "responseBody": "[valid JSON with actual response fields from SOURCE DATA]"
                }
              ],
              "dependencies": ["[PackageName vX.Y — specific reason it is used in this project]"],
              "recommendations": ["[Specific actionable improvement based on SOURCE DATA]"],
              "knownIssues": ["[Specific issue observed in SOURCE DATA with suggested fix]"]
            }
            """,

        DocumentationType.ApiDocumentation => """
            {
              "executiveSummary": "[Base URL, auth method, content-type, and 1-sentence purpose — all from SOURCE DATA]",
              "technicalOverview": "[## Authentication, ## Common Headers, ## Error Response Format, ## HTTP Status Codes — all from SOURCE DATA]",
              "apiEndpoints": [
                {
                  "method": "GET|POST|PUT|DELETE",
                  "path": "[actual route from SOURCE DATA]",
                  "description": "[purpose, side effects, auth required]",
                  "requestBody": "[valid JSON with all actual fields and example values from SOURCE DATA]",
                  "responseBody": "[valid JSON showing success and error shapes from SOURCE DATA]"
                }
              ],
              "dependencies": ["[External service this API depends on — from SOURCE DATA]"],
              "recommendations": ["[API design improvement based on SOURCE DATA]"],
              "knownIssues": ["[API limitation observed in SOURCE DATA]"]
            }
            """,

        DocumentationType.ArchitectureOverview => """
            {
              "executiveSummary": "[1 paragraph: what the system is, design philosophy, core workflow, primary technology choices — from SOURCE DATA]",
              "technicalOverview": "[## Technology Stack with versions and rationale, ## Project Dependencies, ## Runtime Environment — from SOURCE DATA]",
              "architectureDescription": "[## System Overview, ## Components and Responsibilities, ## Data Flow, ## External Integrations, ## Scalability, ## Security — all using actual names from SOURCE DATA]",
              "configurationGuide": "[Infrastructure config, environment settings, deployment configuration — from SOURCE DATA]",
              "dependencies": ["[LibraryName vX.Y — architectural role in this specific project]"],
              "recommendations": ["[Architectural improvement with rationale and effort estimate — based on SOURCE DATA]"],
              "knownIssues": ["[Architectural weakness observed in SOURCE DATA with suggested fix]"]
            }
            """,

        _ /* FullDocumentation */ => """
            {
              "executiveSummary": "[3-4 sentences for both technical and non-technical readers: what, who, stack, value — from SOURCE DATA]",
              "technicalOverview": "[## Purpose, ## Architecture, ## Request Lifecycle, ## Technology Stack — from SOURCE DATA]",
              "architectureDescription": "[## Components, ## Data Flow, ## External Services, ## Scalability & Security — from SOURCE DATA]",
              "userGuide": "[Plain-English guide with ## headings and numbered steps — from SOURCE DATA]",
              "setupInstructions": "[## Prerequisites, ## Clone & Build, ## Configure, ## Run — actual commands from SOURCE DATA]",
              "configurationGuide": "[Every config key from SOURCE DATA: name, type, example, effect]",
              "features": [
                {
                  "name": "[Feature name from SOURCE DATA]",
                  "description": "[Technical + user-facing description using SOURCE DATA]",
                  "usageExample": "[Concrete example from SOURCE DATA]"
                }
              ],
              "apiEndpoints": [
                {
                  "method": "GET|POST|PUT|DELETE",
                  "path": "[actual path from SOURCE DATA]",
                  "description": "[purpose and side effects]",
                  "requestBody": "[valid JSON with actual fields from SOURCE DATA]",
                  "responseBody": "[valid JSON with actual response from SOURCE DATA]"
                }
              ],
              "dependencies": ["[PackageName vX.Y — purpose in this project]"],
              "recommendations": ["[Specific improvement based on SOURCE DATA]"],
              "knownIssues": ["[Specific issue from SOURCE DATA with fix]"]
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


            ══════════════════════════════════════════════════════
            CRITICAL: SOURCE DATA PRIMACY
            ══════════════════════════════════════════════════════
            You will receive SOURCE DATA (JIRA tickets and/or a Git repository).
            You MUST document ONLY that project. Do NOT document Docu-Genius, this
            prompt, or any tool that sent this request. Every fact you write must
            come from the SOURCE DATA provided in the user message.

            ══════════════════════════════════════════════════════
            CONTENT QUALITY STANDARDS
            ══════════════════════════════════════════════════════

            1. SPECIFICITY — use actual names, IDs, and values from the source data.
               ✗ WRONG: "The service processes user requests and returns results."
               ✓ RIGHT: "The OrderService.PlaceOrder() method validates stock availability
                          against the InventoryRepository, then persists the order to the
                          orders table and publishes an OrderCreated event."
               (Use names from YOUR source data — the example above is illustrative only.)

            2. DEPTH — each major text field must be substantive:
               • executiveSummary       → minimum 3 sentences: WHAT it does, WHY it exists, WHO uses it
               • technicalOverview      → minimum 3 paragraphs with ## headings
               • architectureDescription→ minimum 3 paragraphs covering components, data flow, integrations
               • userGuide              → minimum 4 numbered steps per task; describe what the user sees
               • setupInstructions      → every command listed explicitly with the exact command string
               • configurationGuide     → every config key, type, default, and effect

            3. ACCURACY — only describe what is present in the source data.
               If data is sparse: "Based on available information, [what is known].
               Additional documentation would require [what is missing]."

            4. ARRAYS — at least 2–3 items per list when the source supports it.
               Empty arrays [] are only acceptable when truly nothing applies.

            5. NO TRUNCATION — complete sentences; never end with "etc.", "…", or "and more".

            ══════════════════════════════════════════════════════
            OUTPUT FORMAT — follow exactly
            ══════════════════════════════════════════════════════

            • Respond with ONLY a raw JSON object. Zero prose, zero explanations, zero ```json fences.
            • First character must be {, last character must be }. Nothing before or after.
            • String field values use markdown: ## for headings, - for bullets, 1. 2. 3. for steps.
            • Do NOT embed JSON objects inside string values EXCEPT apiEndpoints.requestBody and
              apiEndpoints.responseBody, which must contain realistic JSON payload examples.
            • requestBody / responseBody: show actual field names and realistic value types.
            """;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "... [truncated]";

    // Rough token estimate: 1 token ≈ 4 characters. Keep prompt under ~60k chars.
    private static string TruncateIfNeeded(string prompt) =>
        prompt.Length > 60_000 ? prompt[..60_000] + "\n\n[Content truncated to fit token limits]" : prompt;
}
