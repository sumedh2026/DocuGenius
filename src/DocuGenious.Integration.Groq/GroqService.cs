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

    // Groq free tier: 6,000 tokens per minute (input + output combined per request).
    // Leave a 200-token safety margin so we never hit HTTP 413 "Request too large".
    private const int TpmBudget     = 5800;
    private const int MinOutputTokens = 1500; // always leave enough room to generate something useful

    private async Task<AnalysisResult> CallOpenAiAsync(
        string systemPrompt, string userPrompt, DocumentationType docType, string sourceInfo)
    {
        // Truncate early so we can measure the actual prompt length for the budget calc.
        var truncatedUserPrompt = TruncateIfNeeded(userPrompt);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(truncatedUserPrompt)
        };

        // ── Per-doc-type desired output size ─────────────────────────────────────
        // Some doc types request many sections and need a bigger budget. These are
        // the desired maximums; the TPM cap below may reduce them further.
        var desiredMaxTokens = docType switch
        {
            DocumentationType.FullDocumentation       => Math.Max(_settings.MaxTokens, 5000),
            DocumentationType.TechnicalDocumentation  => Math.Max(_settings.MaxTokens, 5000),
            DocumentationType.ArchitectureOverview    => Math.Max(_settings.MaxTokens, 4500),
            _                                         => _settings.MaxTokens
        };

        // ── TPM budget enforcement ────────────────────────────────────────────────
        // Rough estimate: 1 token ≈ 4 chars.  The system prompt is counted too because
        // Groq includes it in the total.  If the prompt is large we shrink the output
        // budget rather than letting the request exceed the 6,000 TPM cap.
        var estimatedInputTokens = (systemPrompt.Length + truncatedUserPrompt.Length) / 4;
        var outputBudget         = Math.Max(MinOutputTokens, TpmBudget - estimatedInputTokens);
        var effectiveMaxTokens   = Math.Min(desiredMaxTokens, outputBudget);

        _logger.LogInformation(
            "Token budget: ~{Input} input + {Output} output ≈ {Total} / {Limit} TPM cap",
            estimatedInputTokens, effectiveMaxTokens,
            estimatedInputTokens + effectiveMaxTokens, TpmBudget + 200);

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = effectiveMaxTokens,
            // 0.4 gives deterministic output while allowing natural language variation
            Temperature = 0.4f
        };

        // Allow one automatic back-off when the free-tier token bucket is full.
        // Groq's 429 body tells us exactly how long to wait ("try again in 38.4s"),
        // so if the wait is ≤ 65 s we pause and retry transparently.
        int rateLimitRetries = 0;

        for (int attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
        {
            // Absolute ceiling per attempt. With streaming the network timer resets on
            // every received chunk, so this only fires if Groq stops sending entirely.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.TimeoutSeconds));

            try
            {
                _logger.LogInformation(
                    "Calling Groq streaming (attempt {Attempt}/{Max}, docType={DocType}, maxTokens={Tokens})...",
                    attempt + 1, RetryDelaysMs.Length + 1, docType, _settings.MaxTokens);

                // ── Streaming call ──────────────────────────────────────────────
                // Tokens stream back as they are generated, so no single "wait for
                // the whole response" hang. Each received chunk resets the read timer.
                var sb = new StringBuilder();
                await foreach (var update in
                    _chatClient.CompleteChatStreamingAsync(messages, options, cts.Token))
                {
                    foreach (var part in update.ContentUpdate)
                        sb.Append(part.Text);
                }

                var content = sb.ToString();
                _logger.LogInformation("Groq stream complete — {Chars} chars", content.Length);

                var result = ParseAnalysisResult(content, docType, sourceInfo);
                ValidateOutputQuality(result, docType);
                return result;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Absolute ceiling reached — no point retrying a hung model
                throw new InvalidOperationException(
                    $"Groq stopped sending data after {_settings.TimeoutSeconds} seconds. " +
                    "Try increasing Groq:TimeoutSeconds or reducing Groq:MaxTokens in appsettings.json.");
            }
            catch (ClientResultException ex) when (ex.Status == 429)
            {
                // ── Free-tier token-bucket exhaustion (6,000 TPM limit) ─────────
                // Groq includes the exact wait in the error: "Please try again in 38.4s."
                var waitSec = ParseRetryAfterSeconds(ex.Message);

                if (rateLimitRetries == 0 && waitSec is > 0 and <= 65)
                {
                    // Auto-wait for the bucket to refill, then retry transparently.
                    rateLimitRetries++;
                    var waitMs = (int)(waitSec.Value * 1000) + 1500; // +1.5 s buffer
                    _logger.LogWarning(
                        "Groq 429 — free-tier token bucket full. " +
                        "Auto-waiting {Sec:F1} s for it to refill before retrying…", waitSec.Value);
                    await Task.Delay(waitMs);
                    attempt--;  // don't consume a 5xx retry slot; loop increment restores it
                    continue;
                }

                // Already auto-retried once, or the wait is too long — surface clearly.
                var hint = waitSec.HasValue
                    ? $"Please wait about {(int)Math.Ceiling(waitSec.Value)} seconds and try again."
                    : "Please wait about a minute and try again.";

                throw new InvalidOperationException(
                    $"Groq free-tier rate limit exceeded. " +
                    $"The llama-3.1-8b-instant model allows 6,000 tokens per minute on the free plan, " +
                    $"and this request used approximately {estimatedInputTokens + effectiveMaxTokens} tokens. " +
                    $"{hint} " +
                    $"You can also reduce Groq:MaxTokens in appsettings.json, " +
                    $"or upgrade your plan at https://console.groq.com", ex);
            }
            catch (ClientResultException ex) when (ex.Status == 413)
            {
                // HTTP 413 means the total request size (input + output) exceeded the model's
                // per-request limit even after our budget calculation.  This can happen when
                // the system prompt or schema adds more tokens than our rough estimate.
                throw new InvalidOperationException(
                    $"The request exceeded the Groq free-tier token limit (6,000 tokens total). " +
                    $"Estimated size was {estimatedInputTokens + effectiveMaxTokens} tokens. " +
                    $"Try selecting fewer JIRA tickets, using a more focused document type " +
                    $"(e.g. User Guide instead of Full Documentation), or upgrading your plan at " +
                    $"https://console.groq.com/settings/billing.", ex);
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
                // Network blip — retry
                _logger.LogWarning(ex,
                    "Groq stream failed on attempt {Attempt} ({Type}). Retrying in {Delay}ms…",
                    attempt + 1, ex.GetType().Name, RetryDelaysMs[attempt]);
                await Task.Delay(RetryDelaysMs[attempt]);
            }
        }

        // Final attempt — let any exception propagate naturally
        using var finalCts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
        var finalSb = new StringBuilder();
        await foreach (var update in
            _chatClient.CompleteChatStreamingAsync(messages, options, finalCts.Token))
        {
            foreach (var part in update.ContentUpdate)
                finalSb.Append(part.Text);
        }
        var finalContent = finalSb.ToString();
        var finalResult  = ParseAnalysisResult(finalContent, docType, sourceInfo);
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

    // Hard-cap the user prompt at 7,000 chars (≈1,750 tokens).
    // Combined with the system prompt (~80 tokens) this keeps total input under ~1,830 tokens,
    // which leaves ~3,970 tokens for output within the Groq free-tier 6,000 TPM cap.
    // The dynamic budget calculation in CallOpenAiAsync fine-tunes the output limit further.
    private static string TruncateIfNeeded(string prompt) =>
        prompt.Length > 7_000 ? prompt[..7_000] + "\n\n[Source data truncated to fit within the 6,000 token free-tier limit]" : prompt;
}
