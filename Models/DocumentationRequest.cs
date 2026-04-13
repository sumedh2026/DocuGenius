namespace DocuGenious.Models;

public class DocumentationRequest
{
    public SourceType SourceType { get; set; }
    public string? JiraTicketId { get; set; }
    public List<string> JiraTicketIds { get; set; } = [];
    public string? GitRepositoryUrl { get; set; }
    public string? GitLocalPath { get; set; }
    public string? GitBranch { get; set; }
    public DocumentationType DocumentationType { get; set; }
    public string OutputFileName { get; set; } = string.Empty;
    public bool IncludeCodeSnippets { get; set; } = true;
    public bool IncludeArchitectureDiagram { get; set; } = false;
    public int MaxFilesToAnalyze { get; set; } = 50;
    public List<string> FileExtensionsToInclude { get; set; } = [".cs", ".ts", ".js", ".py", ".java", ".go", ".rs", ".md"];
}

public enum SourceType
{
    JiraOnly,
    GitOnly,
    Both
}
