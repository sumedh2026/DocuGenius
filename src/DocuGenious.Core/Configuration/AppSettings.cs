namespace DocuGenious.Core.Configuration;

public class AppSettings
{
    public JiraSettings Jira { get; set; } = new();
    public GitSettings Git { get; set; } = new();
    public GroqSettings Groq { get; set; } = new();
    public GeminiSettings Gemini { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
}

public class JiraSettings
{
    public string BaseUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
}

public class GitSettings
{
    public string Username { get; set; } = string.Empty;
    public string PersonalAccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Base directory for cloning remote Git repositories.
    /// Leave empty (recommended) to use the system temp directory automatically —
    /// this works on every environment including Azure App Service, Docker, and Linux.
    /// Each clone is placed in a unique subdirectory and deleted after analysis.
    /// Set an explicit path only if you need to inspect cloned files manually.
    /// </summary>
    public string CloneDirectory { get; set; } = string.Empty;
}

public class GroqSettings
{
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Model to use for document generation.
    /// Recommended options (Groq free tier):
    ///   groq/compound          — 70,000 TPM, 131K context, agentic system. Best for document generation.
    ///   llama-3.1-8b-instant   — 6,000 TPM, 128K context, fast lightweight model.
    ///   llama-3.3-70b-versatile — 6,000 TPM, highest quality but heavily throttled on free tier.
    /// </summary>
    public string Model { get; set; } = "compound-beta";

    /// <summary>Maximum output tokens per request. compound-beta supports up to 8192.</summary>
    public int MaxTokens { get; set; } = 8192;

    /// <summary>
    /// Free-tier tokens-per-minute (TPM) cap for the chosen model.
    /// Used to calculate how many output tokens can be requested without triggering HTTP 413.
    ///   groq/compound        → 70000
    ///   llama-3.1-8b-instant → 6000
    ///   llama-3.3-70b-versatile → 6000
    /// </summary>
    public int TpmLimit { get; set; } = 70000;

    public string BaseUrl { get; set; } = "https://api.groq.com/openai/v1";

    /// <summary>
    /// Absolute ceiling (seconds) for a single Groq streaming call.
    /// With streaming the network timeout resets per chunk, so this is only
    /// reached if Groq stops sending data entirely. Default 300 s (5 min).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;
}

public class GeminiSettings
{
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gemini model to use for document generation.
    /// Available options (confirmed via ListModels for v1beta):
    ///   gemini-2.5-flash      — Latest Flash. Best overall for free tier. Recommended.
    ///   gemini-2.5-pro        — Latest Pro. Higher quality, may have lower free-tier RPM.
    ///   gemini-2.0-flash      — Previous Flash generation (may be restricted for new keys).
    ///   gemini-2.0-flash-lite — Lightweight/fast variant of 2.0.
    /// </summary>
    public string Model { get; set; } = "gemini-2.5-flash";

    /// <summary>Maximum output tokens per request. gemini-2.0-flash supports up to 8192.</summary>
    public int MaxOutputTokens { get; set; } = 8192;

    /// <summary>Absolute ceiling (seconds) for a single Gemini API call. Default 300 s (5 min).</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Gemini REST API base URL.</summary>
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";

    /// <summary>
    /// Controls how much source data is sent to Gemini per request.
    /// Increase limits for richer AI context (more tokens, higher cost, longer response time).
    /// Decrease limits to reduce token usage and cost.
    /// All values are in characters (≈ tokens × 4).
    /// </summary>
    public GeminiTruncationSettings Truncation { get; set; } = new();
}

public class GeminiTruncationSettings
{
    /// <summary>
    /// Maximum characters sent for a single JIRA ticket description.
    /// Default: 2000 chars (~500 tokens). Increase up to ~8000 for very detailed tickets.
    /// </summary>
    public int JiraDescriptionMaxChars { get; set; } = 2000;

    /// <summary>
    /// Maximum characters per acceptance-criteria item.
    /// Default: 300 chars. Rarely needs changing.
    /// </summary>
    public int JiraAcceptanceCriteriaMaxChars { get; set; } = 300;

    /// <summary>
    /// Maximum characters per JIRA comment body.
    /// Default: 300 chars. Increase if comments contain important technical detail.
    /// </summary>
    public int JiraCommentMaxChars { get; set; } = 300;

    /// <summary>
    /// Maximum characters per Git commit message.
    /// Default: 200 chars. Commit messages are rarely longer.
    /// </summary>
    public int GitCommitMessageMaxChars { get; set; } = 200;

    /// <summary>
    /// Maximum characters of source-file content sent per file.
    /// Default: 2000 chars (~500 tokens). The most impactful setting for code analysis.
    /// Increase to 4000–8000 for deeper per-file understanding on Tier 1.
    /// Note: total prompt is still capped by PromptMaxChars below.
    /// </summary>
    public int GitFileContentMaxChars { get; set; } = 2000;

    /// <summary>
    /// Hard ceiling on the entire assembled prompt (system + user + all source data).
    /// Default: 80000 chars (~20000 tokens). Gemini 2.5 Flash Tier 1 supports up to
    /// ~4 000 000 tokens/min, so this can safely be raised to 200000+ for large repos.
    /// </summary>
    public int PromptMaxChars { get; set; } = 80000;
}

public class OutputSettings
{
    public string PdfDirectory { get; set; } = "./output";
}
