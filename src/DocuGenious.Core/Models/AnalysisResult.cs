namespace DocuGenious.Core.Models;

public class AnalysisResult
{
    public string ExecutiveSummary { get; set; } = string.Empty;
    public string TechnicalOverview { get; set; } = string.Empty;
    public string ArchitectureDescription { get; set; } = string.Empty;
    public string UserGuide { get; set; } = string.Empty;
    public List<Feature> Features { get; set; } = [];
    public List<ApiEndpoint> ApiEndpoints { get; set; } = [];
    public List<string> Dependencies { get; set; } = [];
    public List<string> Recommendations { get; set; } = [];
    public List<string> KnownIssues { get; set; } = [];
    public string SetupInstructions { get; set; } = string.Empty;
    public string ConfigurationGuide { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public DocumentationType DocumentationType { get; set; }
    public string SourceInfo { get; set; } = string.Empty;
}

public class Feature
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string UsageExample { get; set; } = string.Empty;
}

public class ApiEndpoint
{
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RequestBody { get; set; } = string.Empty;
    public string ResponseBody { get; set; } = string.Empty;
}
public enum DocumentationType
{
    TechnicalDocumentation,
    UserGuide,
    ApiDocumentation,
    ArchitectureOverview,
    FullDocumentation 
}
