namespace DocuGenious.Blazor.Models;

// ── Per-item result shown in the validation panel ─────────────────────────────

public enum ValidationStatus { Pending, Checking, Pass, Warn, Fail }

public class ValidationRow
{
    public string           Label   { get; set; } = string.Empty;
    public ValidationStatus Status  { get; set; } = ValidationStatus.Pending;
    public string           Message { get; set; } = string.Empty;

    // Convenience factory methods
    public static ValidationRow Checking(string label)             => new() { Label = label, Status = ValidationStatus.Checking };
    public static ValidationRow Pass(string label, string msg)     => new() { Label = label, Status = ValidationStatus.Pass,    Message = msg };
    public static ValidationRow Warn(string label, string msg)     => new() { Label = label, Status = ValidationStatus.Warn,    Message = msg };
    public static ValidationRow Fail(string label, string msg)     => new() { Label = label, Status = ValidationStatus.Fail,    Message = msg };
}

// ── API response shapes (mirrors Core models but kept in Blazor layer) ─────────

public class TicketValidationDto
{
    public string TicketId { get; set; } = string.Empty;
    public bool   Exists   { get; set; }
    public string Summary  { get; set; } = string.Empty;
    public string Status   { get; set; } = string.Empty;
    public string Message  { get; set; } = string.Empty;
}

public class RepoValidationDto
{
    public string RepositoryUrl  { get; set; } = string.Empty;
    public bool   Accessible     { get; set; }
    public string DefaultBranch  { get; set; } = string.Empty;
    public bool   BranchExists   { get; set; } = true;
    public string Message        { get; set; } = string.Empty;
}
