namespace DocuGenious.Core.Models;

public class JiraTicket
{
    public string Key { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string Assignee { get; set; } = string.Empty;
    public string Reporter { get; set; } = string.Empty;
    public string IssueType { get; set; } = string.Empty;
    public List<string> Labels { get; set; } = [];
    public List<string> Components { get; set; } = [];
    public List<JiraComment> Comments { get; set; } = [];
    public List<JiraTicket> SubTasks { get; set; } = [];
    public DateTime? CreatedDate { get; set; }
    public DateTime? UpdatedDate { get; set; }
    public string ProjectKey { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public List<string> AcceptanceCriteria { get; set; } = [];
}

public class JiraComment
{
    public string Author { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime? CreatedDate { get; set; }
}
