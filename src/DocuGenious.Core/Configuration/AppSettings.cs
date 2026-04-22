namespace DocuGenious.Core.Configuration;

public class AppSettings
{
    public JiraSettings Jira { get; set; } = new();
    public GitSettings Git { get; set; } = new();
    public GroqSettings Groq { get; set; } = new();
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
    /// llama-3.1-8b-instant  — fast (30 K TPM free tier, ~1000 tok/s).  Recommended default.
    /// llama-3.3-70b-versatile — highest quality but only 6 K TPM free tier (~200 tok/s, often throttled).
    /// </summary>
    public string Model { get; set; } = "llama-3.1-8b-instant";
    public int MaxTokens { get; set; } = 4000;
    public string BaseUrl { get; set; } = "https://api.groq.com/openai/v1";
    /// <summary>
    /// Absolute ceiling (seconds) for a single Groq streaming call.
    /// With streaming the network timeout resets per chunk, so this is only
    /// reached if Groq stops sending data entirely. Default 300 s (5 min).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;
}

public class OutputSettings
{
    public string PdfDirectory { get; set; } = "./output";
}
