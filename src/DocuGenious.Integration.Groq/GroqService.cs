using System.ClientModel;
using System.IO;
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

        // The OpenAI .NET SDK supports custom endpoints — Groq exposes an OpenAI-compatible API.
        // NetworkTimeout is set to 10 minutes here so the SDK's own HttpClient never
        // interferes with a long streaming response. The actual per-call absolute limit is
        // controlled by the CancellationToken created from Groq:TimeoutSeconds (default 300 s).
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint       = new Uri(_settings.BaseUrl),
            NetworkTimeout = TimeSpan.FromMinutes(10)
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

    private static string BuildUserPrompt(
        DocumentationType docType,
        string? additionalContext,
        string sourceData,
        int sourceCount)
    {
        var extra = string.IsNullOrWhiteSpace(additionalContext)
            ? string.Empty
            : $"Additional context: {additionalContext.Trim()}\n";

        return GetFocusInstructions(docType) + "\n" +
               extra +
               "SOURCE DATA:\n" + sourceData + "\n" +
               "Output JSON:\n" + GetJsonSchema(docType);
    }


    // Retry delays for transient Groq server errors (5xx). 429 has its own back-off logic.
    private static readonly int[] RetryDelaysMs = [1500, 4000, 9000];

    private async Task<AnalysisResult> CallOpenAiAsync(
        string systemPrompt, string userPrompt, DocumentationType docType, string sourceInfo)
    {
        var truncatedUserPrompt = TruncateIfNeeded(userPrompt);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(truncatedUserPrompt)
        };

        // Cap output tokens so input + output never exceeds the model's per-minute
        // token budget. This is critical for llama-3.1-8b-instant (TpmLimit = 6,000)
        // where input alone can be 400-800 tokens, leaving ~5,200 for output.
        // For compound-beta (TpmLimit = 70,000) the cap has no practical effect.
        // Always guarantee at least 1,000 output tokens even on large prompts.
        var estimatedInputTokens = (systemPrompt.Length + truncatedUserPrompt.Length) / 4;
        var effectiveMaxTokens   = Math.Max(1000,
            Math.Min(_settings.MaxTokens, _settings.TpmLimit - estimatedInputTokens - 200));

        _logger.LogInformation(
            "Groq call: ~{Input} input tokens, {Output} max output tokens (model: {Model})",
            estimatedInputTokens, effectiveMaxTokens, _settings.Model);

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = effectiveMaxTokens,
            Temperature         = 0.4f
        };

        // Allow one automatic back-off when the free-tier token bucket is full.
        // Groq's 429 body tells us exactly how long to wait ("try again in 38.4s"),
        // so if the wait is ≤ 65 s we pause and retry transparently.
        int rateLimitRetries = 0;

        for (int attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.TimeoutSeconds));

            try
            {
                _logger.LogInformation(
                    "Calling Groq (attempt {Attempt}/{Max}, docType={DocType}, maxTokens={Tokens})...",
                    attempt + 1, RetryDelaysMs.Length + 1, docType, _settings.MaxTokens);

                // ── Non-streaming call ──────────────────────────────────────────
                // compound-beta is an agentic model that performs tool calls and web
                // searches internally before returning. Its streaming chunks cause
                // NullReferenceException inside the OpenAI SDK for all non-text updates,
                // which made the accumulated content empty. The non-streaming call waits
                // for the fully-assembled response in one round trip — simpler and more
                // reliable for agentic models. For standard models the difference is
                // negligible since we always wait for the full response anyway.
                var response = await _chatClient.CompleteChatAsync(messages, options, cts.Token);

                var sb = new StringBuilder();
                foreach (var part in response.Value.Content)
                    if (part.Kind == ChatMessageContentPartKind.Text && part.Text is not null)
                        sb.Append(part.Text);

                var content = sb.ToString();
                _logger.LogInformation("Groq call complete — {Chars} chars", content.Length);

                var result = ParseAnalysisResult(content, docType, sourceInfo);
                ValidateOutputQuality(result, docType);
                return result;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"Groq did not respond within {_settings.TimeoutSeconds} seconds. " +
                    "Try increasing Groq:TimeoutSeconds in appsettings.json.");
            }
            catch (ClientResultException ex) when (ex.Status == 429)
            {
                // ── Free-tier token-bucket exhaustion ─────────────────────────
                var waitSec = ParseRetryAfterSeconds(ex.Message);

                if (rateLimitRetries == 0 && waitSec is > 0 and <= 65)
                {
                    rateLimitRetries++;
                    var waitMs = (int)(waitSec.Value * 1000) + 1500;
                    _logger.LogWarning(
                        "Groq 429 — token bucket full. Auto-waiting {Sec:F1} s before retrying…",
                        waitSec.Value);
                    await Task.Delay(waitMs);
                    attempt--;
                    continue;
                }

                var hint = waitSec.HasValue
                    ? $"Please wait about {(int)Math.Ceiling(waitSec.Value)} seconds and try again."
                    : "Please wait about a minute and try again.";

                throw new InvalidOperationException(
                    $"Groq rate limit exceeded for model '{_settings.Model}' " +
                    $"({_settings.TpmLimit:N0} TPM on the free plan). " +
                    $"This request used approximately {estimatedInputTokens + effectiveMaxTokens} tokens. " +
                    $"{hint} " +
                    $"You can also reduce Groq:MaxTokens in appsettings.json, " +
                    $"or upgrade your plan at https://console.groq.com", ex);
            }
            catch (ClientResultException ex) when (ex.Status == 413)
            {
                throw new InvalidOperationException(
                    $"The request exceeded the Groq token limit for model '{_settings.Model}' " +
                    $"({_settings.TpmLimit:N0} TPM). " +
                    $"Estimated size was {estimatedInputTokens + effectiveMaxTokens} tokens. " +
                    $"Try selecting fewer JIRA tickets, a more focused document type, " +
                    $"or upgrading your plan at https://console.groq.com/settings/billing.", ex);
            }
            catch (ClientResultException ex) when (ex.Status == 401)
            {
                throw new InvalidOperationException(
                    "Groq API key is invalid. Verify your key at https://console.groq.com/keys", ex);
            }
            catch (ClientResultException ex) when (ex.Status >= 500 && attempt < RetryDelaysMs.Length)
            {
                _logger.LogWarning(
                    "Groq server error {Status} on attempt {Attempt}. Retrying in {Delay}ms…",
                    ex.Status, attempt + 1, RetryDelaysMs[attempt]);
                await Task.Delay(RetryDelaysMs[attempt]);
            }
            catch (Exception ex) when (ex is not InvalidOperationException && attempt < RetryDelaysMs.Length)
            {
                _logger.LogWarning(ex,
                    "Groq call failed on attempt {Attempt} ({Type}). Retrying in {Delay}ms…",
                    attempt + 1, ex.GetType().Name, RetryDelaysMs[attempt]);
                await Task.Delay(RetryDelaysMs[attempt]);
            }
        }

        // Final attempt — let any exception propagate naturally
        using var finalCts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
        var finalResponse = await _chatClient.CompleteChatAsync(messages, options, finalCts.Token);
        var finalSb = new StringBuilder();
        foreach (var part in finalResponse.Value.Content)
            if (part.Kind == ChatMessageContentPartKind.Text && part.Text is not null)
                finalSb.Append(part.Text);
        var finalContent = finalSb.ToString();
        var finalResult  = ParseAnalysisResult(finalContent, docType, sourceInfo);
        ValidateOutputQuality(finalResult, docType);
        return finalResult;
    }

    // Reuse a single options instance — creating JsonSerializerOptions inline is expensive
    // and can trigger validation issues in .NET 9/10.
    // FlexibleXxxConverters handle the cases where the model returns a list field in an
    // unexpected shape (strings, wrong property names, single object instead of array).
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(),
            new FlexibleApiEndpointListConverter(),
            new FlexibleFeatureListConverter(),
            new FlexibleStringListConverter()
        }
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
                if (result != null)
                {
                    NormalizeResult(result, docType);
                    if (HasMeaningfulContent(result))
                    {
                        result.DocumentationType = docType;
                        result.SourceInfo        = sourceInfo;
                        result.GeneratedAt       = DateTime.UtcNow;
                        _logger.LogDebug("Groq response parsed successfully using candidate {Index}", index);
                        return result;
                    }
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

        // Log raw response so it's visible in the API logs
        _logger.LogError(
            "All {Count} Groq response candidates failed to parse. " +
            "Raw content (first 500 chars): {Raw}",
            candidates.Count,
            rawContent.Length > 500 ? rawContent[..500] + "…" : rawContent);

        // Last-ditch: extract whatever fields we can via regex, normalise, and return a partial result
        var partial = TryExtractPartialResult(rawContent, docType, sourceInfo);
        if (partial != null)
        {
            _logger.LogWarning("Returning partial result extracted via regex fallback.");
            return partial;
        }

        throw new InvalidOperationException(
            "The AI response could not be parsed into a valid document. " +
            "Please try again. " +
            $"(Preview: {(rawContent.Length > 200 ? rawContent[..200] + "…" : rawContent)})");
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
    /// Parses the retry-after delay (seconds) from a Groq 429 error message.
    /// Groq embeds the wait time in the message body, e.g.:
    ///   "Please try again in 38.4s."
    ///   "Please try again in 1m2.5s."
    /// Returns null when no recognisable pattern is found.
    /// </summary>
    private static double? ParseRetryAfterSeconds(string message)
    {
        // "Xm Y.Ys" — minutes + fractional seconds
        var mMatch = Regex.Match(message,
            @"try again in (\d+)m(\d+(?:\.\d+)?)s", RegexOptions.IgnoreCase);
        if (mMatch.Success &&
            double.TryParse(mMatch.Groups[1].Value, out var mins) &&
            double.TryParse(mMatch.Groups[2].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var secs))
            return mins * 60 + secs;

        // "X.Xs" — fractional seconds only
        var sMatch = Regex.Match(message,
            @"try again in (\d+(?:\.\d+)?)s", RegexOptions.IgnoreCase);
        if (sMatch.Success &&
            double.TryParse(sMatch.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var sec))
            return sec;

        return null;
    }

    /// <summary>
    /// Yields JSON string candidates from the model response, from most to least specific.
    /// For each candidate both the raw version and a "repaired" version (with literal
    /// newlines inside string values escaped) are tried, because llama-3.1-8b-instant
    /// frequently emits bare \n / \r characters inside multi-line string values which
    /// makes the JSON syntactically invalid.
    ///
    /// Strategy 1 — raw text (model returned clean JSON)
    /// Strategy 2 — extract from ```json ... ``` fence
    /// Strategy 3 — extract from ``` ... ``` fence
    /// Strategy 4 — brace extraction: first { to the matching top-level }
    ///              Skips braces inside quoted strings so embedded JSON examples
    ///              (e.g. requestBody / responseBody fields) don't fool the extractor.
    /// Strategy 5 — truncated-JSON repair: closes unclosed braces/brackets left when
    ///              MaxOutputTokenCount is hit before the LLM finished the response.
    /// Strategy 6 — nested-object flattening: converts fields that should be strings
    ///              but were returned as objects (e.g. {"executiveSummary":{"description":"…"}})
    ///              into plain strings so deserialisation can succeed.
    /// </summary>
    private static IEnumerable<string> ExtractJsonCandidates(string raw)
    {
        // Helper: yield the candidate and, if different, its repaired variant
        static IEnumerable<string> WithRepaired(string candidate)
        {
            yield return candidate;
            var repaired = FixLiteralNewlinesInStrings(candidate);
            if (repaired != candidate)
                yield return repaired;
        }

        var trimmed = raw.Trim();

        // 1. Raw text as-is (+ repaired)
        foreach (var c in WithRepaired(trimmed)) yield return c;

        // 2. ```json ... ``` fence (Groq/LLaMA often wraps output this way)
        var jsonFence = Regex.Match(raw, @"```json\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
        if (jsonFence.Success)
            foreach (var c in WithRepaired(jsonFence.Groups[1].Value.Trim())) yield return c;

        // 3. Generic ``` ... ``` fence
        var genericFence = Regex.Match(raw, @"```\s*([\s\S]*?)\s*```");
        if (genericFence.Success)
            foreach (var c in WithRepaired(genericFence.Groups[1].Value.Trim())) yield return c;

        // 4. Brace extraction — find matching top-level { ... }
        //    Walks character by character, tracking string context so that
        //    braces inside "requestBody": "{ ... }" strings are not counted.
        var extracted = ExtractTopLevelJson(raw);
        if (extracted != null && extracted != trimmed)
            foreach (var c in WithRepaired(extracted)) yield return c;

        // 5. Truncated-JSON repair — handles responses cut off when MaxOutputTokenCount
        //    is reached before the LLM could emit all closing braces/brackets.
        //    Reconstructs the missing closers from the open-bracket stack.
        var truncRepaired = TryRepairTruncatedJson(raw);
        if (truncRepaired != null && truncRepaired != trimmed && truncRepaired != extracted)
            foreach (var c in WithRepaired(truncRepaired)) yield return c;

        // 6. Nested-object flattening — model sometimes returns
        //    {"executiveSummary":{"description":"…"}} instead of {"executiveSummary":"…"}.
        //    Flatten those object-valued string fields to plain strings and retry.
        var flattened = FlattenStringFields(trimmed);
        if (flattened != null)
            foreach (var c in WithRepaired(flattened)) yield return c;
    }

    /// <summary>
    /// Repairs a JSON string where the LLM has emitted literal control characters inside
    /// quoted string values (instead of the required escape sequences).
    /// Walks the text character-by-character, tracking whether the cursor is inside a JSON
    /// string, and escapes any bare control characters (U+0000–U+001F, U+007F) it finds.
    /// Handles \n, \r, \t, \b, \f explicitly and all others as \uXXXX.
    /// </summary>
    private static string FixLiteralNewlinesInStrings(string json)
    {
        var sb      = new StringBuilder(json.Length + 64);
        bool inStr  = false;
        bool escaped = false;

        foreach (var ch in json)
        {
            if (escaped)
            {
                // Whatever follows a backslash is the escape payload — emit verbatim
                sb.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\' && inStr)
            {
                sb.Append(ch);
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                inStr = !inStr;
                sb.Append(ch);
                continue;
            }

            if (inStr)
            {
                // Escape ALL control characters that are illegal raw in JSON strings
                switch (ch)
                {
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    default:
                        if (ch < '\x20' || ch == '\x7F')
                            sb.Append($"\\u{(int)ch:x4}");
                        else
                            sb.Append(ch);
                        break;
                }
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Repairs JSON that has been truncated mid-stream (e.g. because MaxOutputTokenCount
    /// was reached before the LLM finished writing).  Rebuilds the closing bracket/brace
    /// stack from the characters already emitted and appends the missing closers.
    /// Returns null if the JSON is already balanced or cannot be repaired.
    /// </summary>
    private static string? TryRepairTruncatedJson(string raw)
    {
        var  stack   = new Stack<char>();
        bool inStr   = false;
        bool escaped = false;
        bool hasOpenBrace = false;

        foreach (var ch in raw)
        {
            if (escaped) { escaped = false; continue; }
            if (ch == '\\' && inStr) { escaped = true; continue; }
            if (ch == '"') { inStr = !inStr; continue; }
            if (inStr) continue;

            switch (ch)
            {
                case '{': stack.Push('}'); hasOpenBrace = true; break;
                case '[': stack.Push(']'); break;
                case '}': if (stack.Count > 0 && stack.Peek() == '}') stack.Pop(); break;
                case ']': if (stack.Count > 0 && stack.Peek() == ']') stack.Pop(); break;
            }
        }

        // Already balanced, over-closed, or not a JSON object
        if (stack.Count == 0 || !hasOpenBrace) return null;

        var sb = new StringBuilder(raw.TrimEnd());

        // If we ended inside a string, close it first
        if (inStr) sb.Append('"');

        // Remove a trailing comma or colon left before the cut-off point
        while (sb.Length > 0 && sb[sb.Length - 1] is ',' or ':')
            sb.Length--;

        // Close all open structures in reverse order
        while (stack.Count > 0)
            sb.Append(stack.Pop());

        return sb.ToString();
    }

    /// <summary>
    /// Walks <paramref name="raw"/> and returns the substring from the first top-level
    /// '{' to its matching '}', ignoring braces that appear inside quoted strings.
    /// Uses proper escape-state tracking (handles \\" correctly).
    /// Returns null if no balanced pair is found.
    /// </summary>
    private static string? ExtractTopLevelJson(string raw)
    {
        int  start   = -1;
        int  depth   = 0;
        bool inStr   = false;
        bool escaped = false;

        for (int i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];

            if (escaped) { escaped = false; continue; }
            if (ch == '\\' && inStr) { escaped = true; continue; }

            if (ch == '"')
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

    // The AnalysisResult string fields that the model may mistakenly return as nested objects.
    private static readonly HashSet<string> _stringFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "executiveSummary", "technicalOverview", "architectureDescription",
        "userGuide", "setupInstructions", "configurationGuide"
    };

    /// <summary>
    /// Detects top-level properties that should be strings but were returned as objects
    /// (e.g. <c>{"executiveSummary":{"description":"…","techStack":"…"}}</c>) and rewrites
    /// them as flat strings, using the property names as <c>## heading</c> separators.
    /// Returns null when no nested-object fields are found so callers can skip it cheaply.
    /// </summary>
    private static string? FlattenStringFields(string json)
    {
        try
        {
            var docOpts = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling     = JsonCommentHandling.Skip
            };
            using var doc = JsonDocument.Parse(json, docOpts);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            // Check whether any string field is actually an object — bail early if not
            bool hasNested = doc.RootElement.EnumerateObject()
                .Any(p => _stringFields.Contains(p.Name) &&
                          p.Value.ValueKind == JsonValueKind.Object);
            if (!hasNested) return null;

            // Rewrite the JSON, flattening object-valued string fields
            var mem    = new MemoryStream();
            var writer = new Utf8JsonWriter(mem);
            writer.WriteStartObject();

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                writer.WritePropertyName(prop.Name);

                if (_stringFields.Contains(prop.Name) &&
                    prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var sb = new StringBuilder();
                    ExtractStringsFromElement(prop.Value, sb, depth: 0);
                    writer.WriteStringValue(sb.ToString().Trim());
                }
                else
                {
                    prop.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(mem.ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Recursively extracts text from a <see cref="JsonElement"/>, formatting
    /// object property names as <c>## Heading</c> markers and array items as bullets.
    /// </summary>
    private static void ExtractStringsFromElement(JsonElement el, StringBuilder sb, int depth)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(el.GetString());
                break;

            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (sb.Length > 0) sb.AppendLine();
                    // Use ## for first depth, ### for deeper nesting
                    sb.AppendLine(depth == 0 ? $"## {prop.Name}" : $"### {prop.Name}");
                    ExtractStringsFromElement(prop.Value, sb, depth + 1);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append($"- {item.GetString()}");
                    }
                    else
                    {
                        ExtractStringsFromElement(item, sb, depth);
                    }
                }
                break;
        }
    }

    private static bool HasMeaningfulContent(AnalysisResult r)
    {
        // Reject candidates where a text field contains raw JSON structure —
        // this means the model wrapped its entire response inside one field.
        static bool LooksLikeJson(string? s) =>
            !string.IsNullOrWhiteSpace(s) &&
            (s.TrimStart().StartsWith('{') ||
             s.Contains("\"executiveSummary\":", StringComparison.Ordinal) ||
             s.Contains("\"technicalOverview\":", StringComparison.Ordinal));

        if (LooksLikeJson(r.ExecutiveSummary) ||
            LooksLikeJson(r.TechnicalOverview) ||
            LooksLikeJson(r.UserGuide))
            return false;

        return !string.IsNullOrWhiteSpace(r.ExecutiveSummary)  ||
               !string.IsNullOrWhiteSpace(r.TechnicalOverview) ||
               !string.IsNullOrWhiteSpace(r.UserGuide)         ||
               r.Features.Count > 0;
    }

    // =========================================================================
    // Post-parse normalisation
    // =========================================================================

    /// <summary>
    /// Normalises a freshly-deserialised <see cref="AnalysisResult"/> in two passes:
    /// 1. <c>RedistributeSections</c> — if the model dumped multiple ## sections into
    ///    one field (e.g. everything in executiveSummary), reroutes each section to the
    ///    correct target field based on heading keywords.
    /// 2. <c>CleanPlaceholderArrays</c> — removes schema-example placeholders that the
    ///    model copied verbatim (e.g. features named "Feature name").
    /// </summary>
    private static void NormalizeResult(AnalysisResult r, DocumentationType docType)
    {
        RedistributeSections(r);
        CleanPlaceholderArrays(r);
    }

    private static void RedistributeSections(AnalysisResult r)
    {
        // Process each text field that might contain dumped sections.
        // Order matters: process executiveSummary first (most likely to be overloaded).
        RedistributeFromField(r, r.ExecutiveSummary,        v => r.ExecutiveSummary        = v);
        RedistributeFromField(r, r.TechnicalOverview,       v => r.TechnicalOverview       = v);
        RedistributeFromField(r, r.UserGuide,               v => r.UserGuide               = v);
        RedistributeFromField(r, r.ArchitectureDescription, v => r.ArchitectureDescription = v);
    }

    private static void RedistributeFromField(
        AnalysisResult r, string? fieldValue, Action<string> setField)
    {
        if (string.IsNullOrWhiteSpace(fieldValue)) return;

        // Normalise inline ## headings that share a line: "## A ## B" → "## A\n## B"
        var normalized = Regex.Replace(fieldValue, @"(?<!\n)(##\s+)", "\n$1");
        var blocks = SplitIntoSectionBlocks(normalized);

        // Nothing to redistribute if there is only one logical block
        if (blocks.Count <= 1) return;

        var keepBlocks = new List<(string Heading, string Body)>();

        foreach (var (heading, body) in blocks)
        {
            var target = ClassifySectionHeading(heading);
            switch (target)
            {
                case "TechnicalOverview":
                    r.TechnicalOverview = AppendSection(r.TechnicalOverview, heading, body);
                    break;
                case "ArchitectureDescription":
                    r.ArchitectureDescription = AppendSection(r.ArchitectureDescription, heading, body);
                    break;
                case "UserGuide":
                    r.UserGuide = AppendSection(r.UserGuide, heading, body);
                    break;
                case "SetupInstructions":
                    r.SetupInstructions = AppendSection(r.SetupInstructions, heading, body);
                    break;
                case "ConfigurationGuide":
                    r.ConfigurationGuide = AppendSection(r.ConfigurationGuide, heading, body);
                    break;
                default:
                    keepBlocks.Add((heading, body));
                    break;
            }
        }

        // Rebuild the source field with only the sections that still belong there
        if (keepBlocks.Count < blocks.Count)
        {
            var sb = new StringBuilder();
            foreach (var (heading, body) in keepBlocks)
            {
                if (!string.IsNullOrWhiteSpace(heading))
                    sb.AppendLine($"## {heading}");
                if (!string.IsNullOrWhiteSpace(body))
                {
                    sb.AppendLine(body.Trim());
                    sb.AppendLine();
                }
            }
            setField(sb.ToString().Trim());
        }
    }

    /// <summary>
    /// Maps a section heading to the <see cref="AnalysisResult"/> field it belongs in.
    /// Returns "Keep" when the section should remain in the source field.
    /// </summary>
    private static string ClassifySectionHeading(string heading)
    {
        var h = heading.ToLowerInvariant();

        if (h.Contains("tech") || h.Contains("stack") || h.Contains("framework") ||
            h.Contains("language") || h.Contains("runtime") || h.Contains("library") ||
            h.Contains("technical overview") || h.Contains("dependencies") ||
            h.Contains("package") || h == "overview" && h.Length < 10)
            return "TechnicalOverview";

        if (h.Contains("architect") || h.Contains("component") || h.Contains("data flow") ||
            h.Contains("security") || h.Contains("integration") || h.Contains("pattern") ||
            h.Contains("system design") || h.Contains("infrastructure"))
            return "ArchitectureDescription";

        if (h.Contains("user guide") || h.Contains("getting started") || h.Contains("how to") ||
            h.Contains("usage") || h.Contains("workflow") || h.Contains("tutorial") ||
            h.Contains("walkthrough") || h.Contains("key features"))
            return "UserGuide";

        if (h.Contains("setup") || h.Contains("install") || h.Contains("prerequisite") ||
            h.Contains("running") || h.Contains("deployment") || h.Contains("quick start") ||
            h.Contains("run ") || h == "run")
            return "SetupInstructions";

        if (h.Contains("config") || h.Contains("setting") || h.Contains("environment") ||
            h.Contains("appsettings") || h.Contains("env var"))
            return "ConfigurationGuide";

        // Keep: "purpose", "summary", "introduction", "about", "executive summary", short headings, etc.
        return "Keep";
    }

    private static string AppendSection(string? existing, string heading, string body)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            sb.Append(existing.Trim());
            sb.AppendLine();
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(heading))
            sb.AppendLine($"## {heading}");
        if (!string.IsNullOrWhiteSpace(body))
            sb.Append(body.Trim());
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Splits <paramref name="text"/> into (Heading, Body) blocks at <c>## </c> markers.
    /// A potential heading is only treated as a heading if its word-count is ≤ 5 — longer
    /// strings after <c>##</c> are body text for the current section (handles the case where
    /// the model writes inline headings like <c>## Purpose ## UCITMS is a web application…</c>
    /// which the normaliser converts to <c>## Purpose\n## UCITMS is a web application…</c>).
    /// </summary>
    private static List<(string Heading, string Body)> SplitIntoSectionBlocks(string text)
    {
        var result  = new List<(string, string)>();
        var lines   = text.Replace("\r\n", "\n").Split('\n');
        var heading = string.Empty;
        var body    = new StringBuilder();

        void Flush()
        {
            result.Add((heading, body.ToString().Trim()));
            heading = string.Empty;
            body.Clear();
        }

        foreach (var line in lines)
        {
            var m = Regex.Match(line, @"^##\s+(.+)");
            if (m.Success)
            {
                var potentialHeading = m.Groups[1].Value.Trim();
                var wordCount = potentialHeading
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

                if (wordCount <= 5)
                {
                    // Genuine heading — start a new block
                    Flush();
                    heading = potentialHeading;
                }
                else
                {
                    // Long sentence masquerading as a heading — treat as body text
                    if (body.Length > 0) body.AppendLine();
                    body.Append(potentialHeading);
                }
            }
            else
            {
                if (body.Length > 0) body.AppendLine();
                body.Append(line);
            }
        }

        Flush();
        return result;
    }

    private static void CleanPlaceholderArrays(AnalysisResult r)
    {
        r.Features = r.Features
            .Where(f => !string.IsNullOrWhiteSpace(f.Name) &&
                        !f.Name.Equals("Feature name", StringComparison.OrdinalIgnoreCase) &&
                        !f.Name.StartsWith("Feature ", StringComparison.OrdinalIgnoreCase) &&
                        !f.Name.Equals("", StringComparison.Ordinal))
            .ToList();

        static List<string> Clean(List<string> items) =>
            items.Where(s => !string.IsNullOrWhiteSpace(s) && s != "\"\"").ToList();

        r.Dependencies    = Clean(r.Dependencies);
        r.Recommendations = Clean(r.Recommendations);
        r.KnownIssues     = Clean(r.KnownIssues);
    }

    /// <summary>
    /// Last-ditch fallback: extracts whatever string fields it can from a malformed JSON
    /// response using regex, then calls <see cref="NormalizeResult"/> on the partial result.
    /// Returns null only if no meaningful field could be extracted at all.
    /// </summary>
    private static AnalysisResult? TryExtractPartialResult(
        string raw, DocumentationType docType, string sourceInfo)
    {
        // Matches "fieldName": "value with possible \"escaped\" quotes and \\backslashes"
        static string? Extract(string json, string field)
        {
            var m = Regex.Match(json,
                $"\"{Regex.Escape(field)}\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
                RegexOptions.Singleline);
            if (!m.Success) return null;
            // Unescape the extracted value
            return m.Groups[1].Value
                .Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t")
                .Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        var summary     = Extract(raw, "executiveSummary");
        var techOver    = Extract(raw, "technicalOverview");
        var archDesc    = Extract(raw, "architectureDescription");
        var userGuide   = Extract(raw, "userGuide");
        var setupInstr  = Extract(raw, "setupInstructions");
        var configGuide = Extract(raw, "configurationGuide");

        if (string.IsNullOrWhiteSpace(summary) &&
            string.IsNullOrWhiteSpace(techOver) &&
            string.IsNullOrWhiteSpace(userGuide))
            return null;

        const string Notice =
            "\n\n*(Note: the document was partially generated — some sections may be incomplete.)*";

        var result = new AnalysisResult
        {
            ExecutiveSummary        = string.IsNullOrWhiteSpace(summary) ? string.Empty : summary + Notice,
            TechnicalOverview       = techOver    ?? string.Empty,
            ArchitectureDescription = archDesc    ?? string.Empty,
            UserGuide               = userGuide   ?? string.Empty,
            SetupInstructions       = setupInstr  ?? string.Empty,
            ConfigurationGuide      = configGuide ?? string.Empty,
            DocumentationType       = docType,
            SourceInfo              = sourceInfo,
            GeneratedAt             = DateTime.UtcNow
        };

        NormalizeResult(result, docType);
        return result;
    }

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
                sb.AppendLine(Truncate(t.Description, 600));
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
                sb.AppendLine("Comments:");
                foreach (var c in t.Comments.Take(2))
                    sb.AppendLine($"  [{c.Author}]: {Truncate(c.Body, 100)}");
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
            sb.AppendLine("\nRecent Commits:");
            foreach (var c in repo.RecentCommits.Take(8))
                sb.AppendLine($"  {c.Date:yyyy-MM-dd} {c.Author}: {c.Message}");
        }

        // Source file summaries
        if (repo.Files.Count > 0)
        {
            sb.AppendLine($"\nSource Files:");
            foreach (var f in repo.Files.Where(x => x.Content != null).Take(8))
            {
                sb.AppendLine($"\n--- {f.Path} ---");
                sb.AppendLine(Truncate(f.Content!, 400));
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

    // ─── Per-doc-type: focus instructions (kept short to save tokens) ───────────────

    private static string GetFocusInstructions(DocumentationType docType) => docType switch
    {
        DocumentationType.UserGuide =>
            "Write a USER GUIDE for non-technical users. Plain English, no jargon. Include step-by-step instructions, common Q&A, and troubleshooting tips.",

        DocumentationType.TechnicalDocumentation =>
            "Write TECHNICAL DOCUMENTATION for developers. Use actual class names, methods, and config keys from SOURCE DATA. Include setup commands and configuration details.",

        DocumentationType.ApiDocumentation =>
            "Write API REFERENCE for developers. Document every endpoint with method, path, and plain-text request/response descriptions from SOURCE DATA. Do NOT embed raw JSON inside string values.",

        DocumentationType.ArchitectureOverview =>
            "Write an ARCHITECTURE OVERVIEW for senior engineers. Cover components, data flow, external integrations, and technology choices from SOURCE DATA.",

        _ =>
            "Write COMPREHENSIVE DOCUMENTATION covering all audiences: executive summary, technical details, user guide, setup, API endpoints, and architecture."
    };

    // ─── Per-doc-type: compact JSON schemas (short hints save tokens) ───────────────

    private static string GetJsonSchema(DocumentationType docType) => docType switch
    {
        DocumentationType.UserGuide =>
            """{"executiveSummary":"what/who/value","userGuide":"## sections with numbered steps","features":[{"name":"","description":"","usageExample":""}],"recommendations":[""],"knownIssues":[""]}""",

        DocumentationType.TechnicalDocumentation =>
            """{"executiveSummary":"purpose/stack/scope","technicalOverview":"## Purpose ## Architecture ## Stack","architectureDescription":"## Components ## Data Flow ## Integrations","setupInstructions":"## Prerequisites ## Install ## Configure ## Run","configurationGuide":"key=type (default) — effect","features":[{"name":"","description":"","usageExample":""}],"apiEndpoints":[{"method":"GET","path":"/api/example","description":"What this endpoint does","requestBody":"Describe parameters in plain text","responseBody":"Describe response fields in plain text"}],"dependencies":["Package vX — reason"],"recommendations":[""],"knownIssues":[""]}""",

        DocumentationType.ApiDocumentation =>
            """{"executiveSummary":"base URL/auth/purpose","technicalOverview":"## Auth ## Headers ## Errors ## Status Codes","apiEndpoints":[{"method":"GET","path":"/api/example","description":"What this endpoint does","requestBody":"Describe parameters in plain text, e.g. id (int, required)","responseBody":"Describe response fields in plain text, e.g. returns user object with id and name"}],"dependencies":[""],"recommendations":[""],"knownIssues":[""]}""",

        DocumentationType.ArchitectureOverview =>
            """{"executiveSummary":"system/philosophy/stack","technicalOverview":"## Stack ## Dependencies ## Runtime","architectureDescription":"## Overview ## Components ## Data Flow ## Integrations ## Scalability ## Security","configurationGuide":"infra/env/deployment config","dependencies":["Library vX — architectural role"],"recommendations":[""],"knownIssues":[""]}""",

        _ /* FullDocumentation */ =>
            """{"executiveSummary":"what/who/stack/value","technicalOverview":"## Purpose ## Architecture ## Stack","architectureDescription":"## Components ## Data Flow ## Security","userGuide":"## Getting Started ## Key Features ## How To Use","setupInstructions":"## Prerequisites ## Install ## Configure ## Run","configurationGuide":"key=type (default) — purpose","features":[{"name":"","description":"","usageExample":""}],"dependencies":["Package vX — purpose"],"recommendations":[""],"knownIssues":[""]}"""
    };

    // ─── System prompt (concise — every token counts on free tier) ──────────────────

    private static string GetSystemPrompt(DocumentationType docType)
    {
        var role = docType switch
        {
            DocumentationType.UserGuide             => "You are a technical writer for non-technical users.",
            DocumentationType.TechnicalDocumentation => "You are a senior software engineer writing developer docs.",
            DocumentationType.ApiDocumentation      => "You are an API documentation specialist.",
            DocumentationType.ArchitectureOverview  => "You are a principal software architect.",
            _                                        => "You are a technical writer covering all audiences."
        };

        return $"{role} Rules: " +
               "1) Document ONLY the project in SOURCE DATA — never Docu-Genius or this tool. " +
               "2) Output ONLY a raw JSON object, first char { last char }, no markdown fences. " +
               "3) Use ## headings and bullet lists inside string values for structure. " +
               "4) NEVER embed raw JSON inside string values — describe request/response fields in plain English. " +
               "5) Use real names and values from SOURCE DATA, not generic placeholders.";
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "... [truncated]";

    // Hard-cap the user prompt at 40,000 chars (≈10,000 tokens).
    // This is a safety ceiling — the dynamic budget in CallOpenAiAsync further caps
    // the output token count so input + output never exceeds the configured TpmLimit.
    // 40K chars is generous enough for rich JIRA + Git combined contexts while keeping
    // well within the 131K context window of groq/compound (or 128K for other models).
    private static string TruncateIfNeeded(string prompt) =>
        prompt.Length > 40_000 ? prompt[..40_000] + "\n\n[Source data truncated to fit token limits]" : prompt;
}
