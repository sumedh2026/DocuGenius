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
    public string Model { get; set; } = "llama-3.3-70b-versatile";
    public int MaxTokens { get; set; } = 6000;
    public string BaseUrl { get; set; } = "https://api.groq.com/openai/v1";
    /// <summary>
    /// Seconds before an individual Groq API call is cancelled.
    /// Default 120 s. Increase for very large documents or slow networks.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;
}

public class OutputSettings
{
    public string PdfDirectory { get; set; } = "./output";
}
