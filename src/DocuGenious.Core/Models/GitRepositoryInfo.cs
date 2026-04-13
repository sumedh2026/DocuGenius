namespace DocuGenious.Core.Models;

public class GitRepositoryInfo
{
    public string RepositoryPath { get; set; } = string.Empty;
    public string RepositoryUrl { get; set; } = string.Empty;
    public string CurrentBranch { get; set; } = string.Empty;
    public List<string> Branches { get; set; } = [];
    public List<GitCommit> RecentCommits { get; set; } = [];
    public List<GitFileInfo> Files { get; set; } = [];
    public List<string> Contributors { get; set; } = [];
    public string TotalCommits { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Technologies { get; set; } = [];
    public DirectoryStructure? Structure { get; set; }
}

public class GitCommit
{
    public string Sha { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public List<string> ChangedFiles { get; set; } = [];
}

public class GitFileInfo
{
    public string Path { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long Size { get; set; }
    public string? Content { get; set; }
}

public class DirectoryStructure
{
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public List<DirectoryStructure> Children { get; set; } = [];
}
