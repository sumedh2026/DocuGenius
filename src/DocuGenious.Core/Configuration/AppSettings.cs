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
    public string CloneDirectory { get; set; } = "./git-repos";
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
}

public class OutputSettings
{
    public string PdfDirectory { get; set; } = "./output";
}
