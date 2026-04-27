using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocuGenious.Core.Configuration;
using DocuGenious.Core.Interfaces;
using DocuGenious.Core.Models;
using Microsoft.Extensions.Logging;

namespace DocuGenious.Integration.Gemini;

public class GeminiService : IGroqService
{
    private readonly GeminiSettings _settings;
    private readonly ILogger<GeminiService> _logger;
    private readonly HttpClient _httpClient;

    public GeminiService(AppSettings settings, ILogger<GeminiService> logger)
    {
        _settings = settings.Gemini;
        _logger   = logger;

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            throw new InvalidOperationException(
                "Gemini API key is not configured. Please set Gemini:ApiKey in appsettings.json.");

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds)
        };
    }

    // =========================================================================
    // IGroqService implementation
    // =========================================================================

    public async Task<bool> ValidateConnectionAsync()
    {
        try
        {
            var response = await CallGeminiAsync("Say 'OK'.", "You are a helpful assistant.", 10);
            return !string.IsNullOrWhiteSpace(response);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogError("Gemini API key is invalid (HTTP 401). Check Gemini:ApiKey in appsettings.json.");
            throw new InvalidOperationException(
                "Gemini API key is invalid. Verify your key at https://aistudio.google.com/app/apikey", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _logger.LogError("Gemini quota exhausted (HTTP 429). Check your plan at https://aistudio.google.com");
            throw new InvalidOperationException(
                "Gemini quota exhausted. Check your plan at https://aistudio.google.com", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate Gemini connection.");
            return false;
        }
    }

    public async Task<AnalysisResult> AnalyzeJiraTicketsAsync(
        List<JiraTicket> tickets, DocumentationType docType, string? additionalContext = null)
    {
        _logger.LogInformation("Analysing {Count} JIRA ticket(s) with Gemini...", tickets.Count);

        var ticketContext = BuildJiraContext(tickets);
        var userPrompt    = BuildUserPrompt(
            docType, additionalContext,
            sourceData: $"=== JIRA TICKETS ===\n{ticketContext}",
            sourceCount: tickets.Count);

        return await CallAndParseAsync(GetSystemPrompt(docType), userPrompt, docType,
            $"JIRA: {string.Join(", ", tickets.Select(t => t.Key))}");
    }

    public async Task<AnalysisResult> AnalyzeGitRepositoryAsync(
        GitRepositoryInfo repoInfo, DocumentationType docType, string? additionalContext = null)
    {
        _logger.LogInformation("Analysing Git repository at {Path} with Gemini...", repoInfo.RepositoryPath);

        var repoContext = BuildGitContext(repoInfo);
        var userPrompt  = BuildUserPrompt(
            docType, additionalContext,
            sourceData: $"=== GIT REPOSITORY ===\n{repoContext}",
            sourceCount: 1);

        return await CallAndParseAsync(GetSystemPrompt(docType), userPrompt, docType,
            $"Repository: {repoInfo.RepositoryUrl ?? repoInfo.RepositoryPath}");
    }

    public async Task<AnalysisResult> AnalyzeCombinedAsync(
        List<JiraTicket> tickets, GitRepositoryInfo repoInfo, DocumentationType docType,
        string? additionalContext = null)
    {
        _logger.LogInformation("Analysing combined JIRA + Git context with Gemini...");

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
            docType, additionalContext,
            sourceData: combined,
            sourceCount: tickets.Count + 1);

        return await CallAndParseAsync(GetSystemPrompt(docType), userPrompt, docType,
            $"JIRA: {string.Join(", ", tickets.Select(t => t.Key))} | Repo: {repoInfo.RepositoryUrl ?? repoInfo.RepositoryPath}");
    }

    // =========================================================================
    // Gemini REST API call
    // =========================================================================

    private async Task<string> CallGeminiAsync(
        string userPrompt, string systemPrompt, int maxOutputTokens,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_settings.BaseUrl}/models/{_settings.Model}:generateContent?key={_settings.ApiKey}";

        var requestBody = new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = systemPrompt } }
            },
            contents = new[]
            {
                new
                {
                    role  = "user",
                    parts = new[] { new { text = userPrompt } }
                }
            },
            generationConfig = new
            {
                maxOutputTokens,
                temperature = 0.4
            }
        };

        var json    = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

            // 1. Try to extract retryDelay from Gemini's structured JSON error body:
            //    error.details[].retryDelay  (e.g. "4s" or "4.197184507s")
            // 2. Fall back to the Retry-After HTTP response header.
            // Either value is embedded into the exception message so CallAndParseAsync
            // can act on it without holding a reference to the HttpResponseMessage.
            var retryAfterSuffix = ExtractRetryDelay(errorBody, response);

            throw new HttpRequestException(
                $"Gemini API returned {(int)response.StatusCode}: {errorBody}{retryAfterSuffix}",
                null, response.StatusCode);
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        // Parse Gemini response: candidates[0].content.parts[0].text
        using var doc = JsonDocument.Parse(responseJson);
        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        return text ?? string.Empty;
    }

    // Retry delays for transient server errors (5xx)
    private static readonly int[] RetryDelaysMs = [1500, 4000, 9000];

    // Rate-limit (429) retry policy.
    // Gemini returns 429 on transient quota bursts even on paid tiers.
    // Default wait of 5 s covers the typical per-minute bucket refill.
    // The actual retryDelay from the Gemini response takes precedence when present.
    private const int    MaxRateLimitRetries    = 3;
    private const double DefaultRateLimitWaitSec = 5.0;

    private async Task<AnalysisResult> CallAndParseAsync(
        string systemPrompt, string userPrompt, DocumentationType docType, string sourceInfo)
    {
        var truncatedUserPrompt = TruncateIfNeeded(userPrompt);

        // gemini-2.0-flash has very generous limits (1M TPM free).
        // Still cap output tokens to the configured maximum.
        var estimatedInputTokens = (systemPrompt.Length + truncatedUserPrompt.Length) / 4;
        var effectiveMaxTokens   = Math.Max(1000, _settings.MaxOutputTokens);

        _logger.LogInformation(
            "Gemini call: ~{Input} input tokens, {Output} max output tokens (model: {Model})",
            estimatedInputTokens, effectiveMaxTokens, _settings.Model);

        int rateLimitRetries = 0;

        for (int attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.TimeoutSeconds));

            try
            {
                _logger.LogInformation(
                    "Calling Gemini (attempt {Attempt}/{Max}, docType={DocType}, maxTokens={Tokens})...",
                    attempt + 1, RetryDelaysMs.Length + 1, docType, effectiveMaxTokens);

                var content = await CallGeminiAsync(
                    truncatedUserPrompt, systemPrompt, effectiveMaxTokens, cts.Token);

                _logger.LogInformation("Gemini call complete — {Chars} chars", content.Length);

                var result = ParseAnalysisResult(content, docType, sourceInfo);
                ValidateOutputQuality(result, docType);
                return result;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"Gemini did not respond within {_settings.TimeoutSeconds} seconds. " +
                    "Try increasing Gemini:TimeoutSeconds in appsettings.json.");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Gemini returns 429 when the per-minute token or request quota is momentarily
                // exhausted. The retryDelay is embedded into the exception message by
                // ExtractRetryDelay (from the JSON details array or Retry-After header).
                // Fall back to DefaultRateLimitWaitSec when no explicit delay is provided.
                var waitSec = ParseRetryAfterSeconds(ex.Message) ?? DefaultRateLimitWaitSec;

                // Auto-retry up to MaxRateLimitRetries times to recover from transient bursts.
                if (rateLimitRetries < MaxRateLimitRetries)
                {
                    rateLimitRetries++;
                    var waitMs = (int)(waitSec * 1000) + 1000; // +1 s buffer
                    _logger.LogWarning(
                        "Gemini 429 — quota momentarily exhausted (retry {R}/{Max}). " +
                        "Auto-waiting {Sec:F1} s before retrying…",
                        rateLimitRetries, MaxRateLimitRetries, waitSec);
                    await Task.Delay(waitMs);
                    attempt--;
                    continue;
                }

                throw new InvalidOperationException(
                    $"Gemini quota exceeded for model '{_settings.Model}' after " +
                    $"{MaxRateLimitRetries} automatic retries. " +
                    $"Please wait about {(int)Math.Ceiling(waitSec)} seconds and try again. " +
                    $"Check your quota at https://ai.dev/rate-limit", ex);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException(
                    "Gemini API key is invalid. Verify your key at https://aistudio.google.com/app/apikey", ex);
            }
            catch (HttpRequestException ex) when (
                ex.StatusCode >= HttpStatusCode.InternalServerError && attempt < RetryDelaysMs.Length)
            {
                _logger.LogWarning(
                    "Gemini server error {Status} on attempt {Attempt}. Retrying in {Delay}ms…",
                    ex.StatusCode, attempt + 1, RetryDelaysMs[attempt]);
                await Task.Delay(RetryDelaysMs[attempt]);
            }
            catch (Exception ex) when (ex is not InvalidOperationException && attempt < RetryDelaysMs.Length)
            {
                _logger.LogWarning(ex,
                    "Gemini call failed on attempt {Attempt} ({Type}). Retrying in {Delay}ms…",
                    attempt + 1, ex.GetType().Name, RetryDelaysMs[attempt]);
                await Task.Delay(RetryDelaysMs[attempt]);
            }
        }

        // Final attempt — let any exception propagate naturally
        using var finalCts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
        var finalContent = await CallGeminiAsync(
            truncatedUserPrompt, systemPrompt, effectiveMaxTokens, finalCts.Token);
        var finalResult = ParseAnalysisResult(finalContent, docType, sourceInfo);
        ValidateOutputQuality(finalResult, docType);
        return finalResult;
    }

    // =========================================================================
    // JSON parsing — identical pipeline to GroqService
    // =========================================================================

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        Converters =
        {
            new JsonStringEnumConverter(),
            new FlexibleApiEndpointListConverter(),
            new FlexibleFeatureListConverter(),
            new FlexibleStringListConverter()
        }
    };

    private AnalysisResult ParseAnalysisResult(string rawContent, DocumentationType docType, string sourceInfo)
    {
        var candidates = ExtractJsonCandidates(rawContent).ToList();

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
                        _logger.LogDebug("Gemini response parsed successfully using candidate {Index}", index);
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

        _logger.LogError(
            "All {Count} Gemini response candidates failed to parse. " +
            "Raw content (first 500 chars): {Raw}",
            candidates.Count,
            rawContent.Length > 500 ? rawContent[..500] + "…" : rawContent);

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

    private void ValidateOutputQuality(AnalysisResult result, DocumentationType docType)
    {
        var issues = new List<string>();
        int score  = 100;

        if (string.IsNullOrWhiteSpace(result.ExecutiveSummary))
        { issues.Add("executiveSummary is empty"); score -= 20; }
        else if (result.ExecutiveSummary.Split(' ').Length < 30)
        { issues.Add($"executiveSummary is very short ({result.ExecutiveSummary.Split(' ').Length} words, expected 50+)"); score -= 10; }

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
                break;

            case DocumentationType.ArchitectureOverview:
                if (string.IsNullOrWhiteSpace(result.ArchitectureDescription))
                { issues.Add("architectureDescription is empty"); score -= 20; }
                else if (result.ArchitectureDescription.Split(' ').Length < 80)
                { issues.Add($"architectureDescription is thin ({result.ArchitectureDescription.Split(' ').Length} words)"); score -= 10; }
                break;
        }

        if (result.Recommendations.Count == 0)
        { issues.Add("recommendations list is empty"); score -= 5; }

        score = Math.Max(0, score);

        if (issues.Count == 0)
            _logger.LogInformation("Output quality check PASSED — score {Score}/100", score);
        else
            _logger.LogWarning(
                "Output quality score {Score}/100 — {Count} issue(s): {Issues}",
                score, issues.Count, string.Join(" | ", issues));
    }

    // =========================================================================
    // JSON extraction strategies (ported from GroqService)
    // =========================================================================

    private static IEnumerable<string> ExtractJsonCandidates(string raw)
    {
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

        // 2. ```json ... ``` fence
        var jsonFence = Regex.Match(raw, @"```json\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
        if (jsonFence.Success)
            foreach (var c in WithRepaired(jsonFence.Groups[1].Value.Trim())) yield return c;

        // 3. Generic ``` ... ``` fence
        var genericFence = Regex.Match(raw, @"```\s*([\s\S]*?)\s*```");
        if (genericFence.Success)
            foreach (var c in WithRepaired(genericFence.Groups[1].Value.Trim())) yield return c;

        // 4. Brace extraction
        var extracted = ExtractTopLevelJson(raw);
        if (extracted != null && extracted != trimmed)
            foreach (var c in WithRepaired(extracted)) yield return c;

        // 5. Truncated-JSON repair
        var truncRepaired = TryRepairTruncatedJson(raw);
        if (truncRepaired != null && truncRepaired != trimmed && truncRepaired != extracted)
            foreach (var c in WithRepaired(truncRepaired)) yield return c;

        // 6. Nested-object flattening (pre-repair first so JsonDocument.Parse succeeds)
        var preRepaired = FixLiteralNewlinesInStrings(trimmed);
        var flattened   = FlattenStringFields(preRepaired);
        if (flattened != null && flattened != preRepaired)
            foreach (var c in WithRepaired(flattened)) yield return c;
    }

    private static string FixLiteralNewlinesInStrings(string json)
    {
        var sb      = new StringBuilder(json.Length + 64);
        bool inStr  = false;
        bool escaped = false;

        foreach (var ch in json)
        {
            if (escaped)
            {
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
                switch (ch)
                {
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\x2028': sb.Append("\\u2028"); break;
                    case '\x2029': sb.Append("\\u2029"); break;
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

        if (stack.Count == 0 || !hasOpenBrace) return null;

        var sb = new StringBuilder(raw.TrimEnd());

        if (inStr) sb.Append('"');

        while (sb.Length > 0 && sb[sb.Length - 1] is ',' or ':')
            sb.Length--;

        while (stack.Count > 0)
            sb.Append(stack.Pop());

        return sb.ToString();
    }

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

            if (inStr) continue;

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

    private static readonly HashSet<string> _stringFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "executiveSummary", "technicalOverview", "architectureDescription",
        "userGuide", "setupInstructions", "configurationGuide"
    };

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

            bool hasNested = doc.RootElement.EnumerateObject()
                .Any(p => _stringFields.Contains(p.Name) &&
                          p.Value.ValueKind == JsonValueKind.Object);
            if (!hasNested) return null;

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
    // Post-parse normalisation (ported from GroqService)
    // =========================================================================

    private static void NormalizeResult(AnalysisResult r, DocumentationType docType)
    {
        RedistributeSections(r);
        CleanPlaceholderArrays(r);
    }

    private static void RedistributeSections(AnalysisResult r)
    {
        RedistributeFromField(r, r.ExecutiveSummary,        v => r.ExecutiveSummary        = v);
        RedistributeFromField(r, r.TechnicalOverview,       v => r.TechnicalOverview       = v);
        RedistributeFromField(r, r.UserGuide,               v => r.UserGuide               = v);
        RedistributeFromField(r, r.ArchitectureDescription, v => r.ArchitectureDescription = v);
    }

    private static void RedistributeFromField(
        AnalysisResult r, string? fieldValue, Action<string> setField)
    {
        if (string.IsNullOrWhiteSpace(fieldValue)) return;

        var normalized = Regex.Replace(fieldValue, @"(?<!\n)(##\s+)", "\n$1");
        var blocks     = SplitIntoSectionBlocks(normalized);

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
                    Flush();
                    heading = potentialHeading;
                }
                else
                {
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

    private static AnalysisResult? TryExtractPartialResult(
        string raw, DocumentationType docType, string sourceInfo)
    {
        static string? Extract(string json, string field)
        {
            var m = Regex.Match(json,
                $"\"{Regex.Escape(field)}\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
                RegexOptions.Singleline);
            if (!m.Success) return null;
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

    // =========================================================================
    // Retry-after extraction helpers
    // =========================================================================

    /// <summary>
    /// Reads <c>error.details[].retryDelay</c> from the Gemini JSON error body, then
    /// falls back to the <c>Retry-After</c> HTTP response header.
    /// Returns a suffix like <c>" [retry after 4.2s]"</c> for embedding in the exception
    /// message, or an empty string when no value is found.
    /// </summary>
    private static string ExtractRetryDelay(string errorBody, HttpResponseMessage response)
    {
        // 1. Parse retryDelay from Gemini's structured JSON error body
        //    Structure: {"error":{"details":[{"@type":"...RetryInfo","retryDelay":"4s"}]}}
        try
        {
            using var errDoc = JsonDocument.Parse(errorBody);
            if (errDoc.RootElement.TryGetProperty("error", out var errEl) &&
                errEl.TryGetProperty("details", out var detailsEl))
            {
                foreach (var detail in detailsEl.EnumerateArray())
                {
                    if (detail.TryGetProperty("retryDelay", out var delayEl))
                    {
                        var delayStr = delayEl.GetString();
                        if (!string.IsNullOrEmpty(delayStr))
                            return $" [retry after {delayStr}]";
                    }
                }
            }
        }
        catch { /* not valid JSON — fall through */ }

        // 2. Fall back to the Retry-After HTTP header
        if (response.Headers.TryGetValues("Retry-After", out var retryValues))
        {
            var headerVal = retryValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(headerVal))
                return $" [retry after {headerVal}s]";
        }

        return string.Empty;
    }

    /// <summary>
    /// Parses a seconds value from the delay embedded in the exception message.
    /// Handles every format Gemini and Groq use:
    ///   • [retry after 4s]          — from retryDelay JSON field ("4s")
    ///   • [retry after 4.197s]      — fractional seconds from retryDelay
    ///   • [retry after 60s]         — from Retry-After header (with appended 's')
    ///   • [retry after 60]          — from Retry-After header (numeric only)
    ///   • Please retry in 4.197s.   — Gemini error message body
    ///   • try again in 38.4s        — Groq error message body
    ///   • try again in 1m2.5s       — Groq minutes+seconds format
    /// Returns null when no recognisable pattern is found.
    /// </summary>
    private static double? ParseRetryAfterSeconds(string message)
    {
        // "[retry after Xs]" or "[retry after X]" — embedded by ExtractRetryDelay / header
        // e.g. "[retry after 4s]", "[retry after 4.197184507s]", "[retry after 60]"
        var bracketMatch = Regex.Match(message,
            @"\[retry after (\d+(?:\.\d+)?)s?\]", RegexOptions.IgnoreCase);
        if (bracketMatch.Success &&
            double.TryParse(bracketMatch.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var bracketSec))
            return bracketSec;

        // "Please retry in X.Xs." — Gemini error message body text
        var geminiMatch = Regex.Match(message,
            @"[Pp]lease retry in (\d+(?:\.\d+)?)s", RegexOptions.IgnoreCase);
        if (geminiMatch.Success &&
            double.TryParse(geminiMatch.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var geminiSec))
            return geminiSec;

        // "try again in Xm Y.Ys" — Groq minutes+seconds
        var mMatch = Regex.Match(message,
            @"try again in (\d+)m(\d+(?:\.\d+)?)s", RegexOptions.IgnoreCase);
        if (mMatch.Success &&
            double.TryParse(mMatch.Groups[1].Value, out var mins) &&
            double.TryParse(mMatch.Groups[2].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var secs))
            return mins * 60 + secs;

        // "try again in X.Xs" — Groq seconds-only
        var sMatch = Regex.Match(message,
            @"try again in (\d+(?:\.\d+)?)s", RegexOptions.IgnoreCase);
        if (sMatch.Success &&
            double.TryParse(sMatch.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var sec))
            return sec;

        return null;
    }

    // =========================================================================
    // Context builders (ported from GroqService)
    // =========================================================================

    private static string BuildUserPrompt(
        DocumentationType docType, string? additionalContext,
        string sourceData, int sourceCount)
    {
        var extra = string.IsNullOrWhiteSpace(additionalContext)
            ? string.Empty
            : $"Additional context: {additionalContext.Trim()}\n";

        return GetFocusInstructions(docType) + "\n" +
               extra +
               "SOURCE DATA:\n" + sourceData + "\n" +
               "Output JSON:\n" + GetJsonSchema(docType);
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
                sb.AppendLine(Truncate(t.Description, 2000));
            }

            if (t.AcceptanceCriteria.Count > 0)
            {
                sb.AppendLine("Acceptance Criteria:");
                foreach (var ac in t.AcceptanceCriteria.Take(10))
                    sb.AppendLine($"  - {Truncate(ac, 300)}");
            }

            if (t.SubTasks.Count > 0)
            {
                sb.AppendLine("Sub-tasks:");
                foreach (var st in t.SubTasks.Take(10))
                    sb.AppendLine($"  - {st.Key}: {st.Summary}");
            }

            if (t.Comments.Count > 0)
            {
                sb.AppendLine("Comments:");
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
        sb.AppendLine($"Contributors: {string.Join(", ", repo.Contributors.Take(20))}");

        if (repo.Technologies.Count > 0)
            sb.AppendLine($"Technologies: {string.Join(", ", repo.Technologies)}");

        if (repo.Branches.Count > 0)
            sb.AppendLine($"Branches: {string.Join(", ", repo.Branches.Take(20))}");

        if (repo.RecentCommits.Count > 0)
        {
            sb.AppendLine("\nRecent Commits:");
            foreach (var c in repo.RecentCommits.Take(20))
                sb.AppendLine($"  {c.Date:yyyy-MM-dd} {c.Author}: {Truncate(c.Message, 200)}");
        }

        if (repo.Files.Count > 0)
        {
            sb.AppendLine("\nSource Files:");
            foreach (var f in repo.Files.Where(x => x.Content != null).Take(20))
            {
                sb.AppendLine($"\n--- {f.Path} ---");
                sb.AppendLine(Truncate(f.Content!, 2000));
            }
        }

        return sb.ToString();
    }

    // =========================================================================
    // Prompt building (ported from GroqService)
    // =========================================================================

    private static string GetFocusInstructions(DocumentationType docType) => docType switch
    {
        DocumentationType.UserGuide =>
            "USER GUIDE: plain English, no jargon, step-by-step for non-technical users.",

        DocumentationType.TechnicalDocumentation =>
            "TECHNICAL DOCS: real class names, config keys, setup commands from SOURCE DATA.",

        DocumentationType.ApiDocumentation =>
            "API REFERENCE: every endpoint with method, path, plain-text request/response.",

        DocumentationType.ArchitectureOverview =>
            "ARCHITECTURE OVERVIEW: components, data flow, tech choices from SOURCE DATA.",

        _ =>
            "FULL DOCUMENTATION: executive summary, tech details, user guide, setup, architecture."
    };

    private static string GetJsonSchema(DocumentationType docType) => docType switch
    {
        DocumentationType.UserGuide =>
            """{"executiveSummary":"1-2 sentences","userGuide":"## Getting Started\n## Key Features\n## How To Use","features":[{"name":"","description":"","usageExample":""}],"recommendations":[""]}""",

        DocumentationType.TechnicalDocumentation =>
            """{"executiveSummary":"purpose/stack","technicalOverview":"## Tech Stack\n## Architecture","setupInstructions":"## Prerequisites\n## Install\n## Run","configurationGuide":"key=type (default) — effect","features":[{"name":"","description":"","usageExample":""}],"dependencies":["pkg vX — reason"],"recommendations":[""]}""",

        DocumentationType.ApiDocumentation =>
            """{"executiveSummary":"base URL/auth","technicalOverview":"## Auth ## Errors","apiEndpoints":[{"method":"GET","path":"/api/x","description":"what it does","requestBody":"params in plain text","responseBody":"response in plain text"}],"recommendations":[""]}""",

        DocumentationType.ArchitectureOverview =>
            """{"executiveSummary":"system/stack","technicalOverview":"## Stack ## Runtime","architectureDescription":"## Components\n## Data Flow\n## Security","dependencies":["lib vX — role"],"recommendations":[""]}""",

        _ /* FullDocumentation */ =>
            """{"executiveSummary":"what/who/stack","technicalOverview":"## Stack\n## Architecture","userGuide":"## Getting Started\n## How To Use","setupInstructions":"## Install\n## Configure\n## Run","configurationGuide":"key=type — purpose","features":[{"name":"","description":"","usageExample":""}],"dependencies":["pkg vX — purpose"],"recommendations":[""]}"""
    };

    private static string GetSystemPrompt(DocumentationType docType)
    {
        var role = docType switch
        {
            DocumentationType.UserGuide              => "Technical writer for non-technical users.",
            DocumentationType.TechnicalDocumentation => "Senior developer writing technical docs.",
            DocumentationType.ApiDocumentation       => "API documentation specialist.",
            DocumentationType.ArchitectureOverview   => "Principal software architect.",
            _                                         => "Technical writer covering all audiences."
        };

        return $"You are a {role} " +
               "Output ONLY a raw JSON object (first char {, last char }), no markdown fences. " +
               "Use ## headings inside string values for structure. " +
               "Write real content from SOURCE DATA — no placeholders.";
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "... [truncated]";

    // Truncate only if the prompt is extremely large (> 80,000 chars ≈ 20,000 tokens).
    // This is a safety net for runaway inputs; Tier 1 context window is 1M tokens.
    private static string TruncateIfNeeded(string prompt) =>
        prompt.Length > 80_000 ? prompt[..80_000] + "\n\n[Source data truncated]" : prompt;
}
