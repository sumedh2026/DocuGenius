using System.Text.Json.Serialization;

namespace DocuGenious.Blazor.Models;

// ─── Request ──────────────────────────────────────────────────────────────────

public class GenerateRequest
{
    [JsonPropertyName("sourceType")]
    public string SourceType { get; set; } = "JiraOnly";

    [JsonPropertyName("jiraTicketIds")]
    public List<string> JiraTicketIds { get; set; } = [];

    [JsonPropertyName("gitRepositoryUrl")]
    public string? GitRepositoryUrl { get; set; }

    [JsonPropertyName("gitBranch")]
    public string? GitBranch { get; set; }

    [JsonPropertyName("documentationType")]
    public string DocumentationType { get; set; } = "FullDocumentation";

    [JsonPropertyName("outputFileName")]
    public string OutputFileName { get; set; } = string.Empty;

    [JsonPropertyName("additionalContext")]
    public string? AdditionalContext { get; set; }

    [JsonPropertyName("jobId")]
    public string? JobId { get; set; }
}

// ─── Responses ───────────────────────────────────────────────────────────────

public class GenerateResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class ConnectionResult
{
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

// ─── UI helpers ──────────────────────────────────────────────────────────────

public record DropdownOption(string Value, string Label);

public static class SourceTypes
{
    public static readonly List<DropdownOption> Options =
    [
        new("JiraOnly",  "JIRA Ticket(s) only"),
        new("GitOnly",   "Git Repository only"),
        new("Both",      "Both JIRA + Git Repository"),
    ];
}

public static class DocumentationTypes
{
    public static readonly List<DropdownOption> Options =
    [
        new("FullDocumentation",      "Full Documentation"),
        new("UserGuide",              "User Guide  (non-technical)"),
        new("TechnicalDocumentation", "Technical Documentation"),
        new("ApiDocumentation",       "API Documentation"),
        new("ArchitectureOverview",   "Architecture Overview"),
    ];
}
