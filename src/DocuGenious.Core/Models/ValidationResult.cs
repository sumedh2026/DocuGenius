namespace DocuGenious.Core.Models;

public class ValidationItem
{
    public string Name    { get; set; } = string.Empty;
    public bool   IsValid { get; set; }
    public bool   IsWarning { get; set; }      // valid but worth noting
    public string Message { get; set; } = string.Empty;
}

public class TicketValidationResult
{
    public string TicketId  { get; set; } = string.Empty;
    public bool   Exists    { get; set; }
    public string Summary   { get; set; } = string.Empty;   // populated when found
    public string Status    { get; set; } = string.Empty;
    public string Message   { get; set; } = string.Empty;
}

public class RepoValidationResult
{
    public string RepositoryUrl  { get; set; } = string.Empty;
    public bool   Accessible     { get; set; }
    public string DefaultBranch  { get; set; } = string.Empty;
    public bool   BranchExists   { get; set; } = true;      // true when no branch was specified
    public string Message        { get; set; } = string.Empty;
}
