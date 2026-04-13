using DocuGenious.Configuration;
using DocuGenious.Models;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace DocuGenious.Services;

public class GitService : IGitService
{
    private readonly GitSettings _settings;
    private readonly ILogger<GitService> _logger;

    // File extensions that likely contain meaningful source code
    private static readonly HashSet<string> DefaultExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".java", ".go", ".rs",
        ".cpp", ".c", ".h", ".rb", ".php", ".swift", ".kt", ".scala",
        ".md", ".yaml", ".yml", ".json", ".toml", ".xml", ".sql"
    };

    // Directories to skip during analysis
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "dist", "build", ".vs",
        "__pycache__", ".cache", "vendor", "packages", ".idea", "coverage"
    };

    public GitService(AppSettings settings, ILogger<GitService> logger)
    {
        _settings = settings.Git;
        _logger = logger;
    }

    public Task<bool> ValidateLocalRepositoryAsync(string localPath)
    {
        return Task.FromResult(Repository.IsValid(localPath));
    }

    public async Task<GitRepositoryInfo> CloneAndAnalyzeAsync(
        string repositoryUrl, string? branch = null, DocumentationRequest? request = null)
    {
        var cloneDir = Path.GetFullPath(_settings.CloneDirectory);
        Directory.CreateDirectory(cloneDir);

        var repoName = ExtractRepoName(repositoryUrl);
        var localPath = Path.Combine(cloneDir, repoName);

        if (Directory.Exists(localPath))
        {
            _logger.LogInformation("Repository already cloned at {Path}. Using existing copy.", localPath);
        }
        else
        {
            _logger.LogInformation("Cloning repository {Url} to {Path}...", repositoryUrl, localPath);

            var cloneOptions = new CloneOptions
            {
                BranchName = branch,
                IsBare = false
            };

            // Add credentials if PAT is configured (supports GitHub, GitLab, Azure DevOps)
            if (!string.IsNullOrWhiteSpace(_settings.PersonalAccessToken))
            {
                cloneOptions.FetchOptions.CredentialsProvider = (url, user, cred) =>
                    new UsernamePasswordCredentials
                    {
                        // For GitHub/GitLab: username can be anything, password = PAT
                        // For Azure DevOps: username = actual username, password = PAT
                        Username = string.IsNullOrWhiteSpace(_settings.Username) ? "git" : _settings.Username,
                        Password = _settings.PersonalAccessToken
                    };
            }

            await Task.Run(() => Repository.Clone(repositoryUrl, localPath, cloneOptions));
        }

        return await AnalyzeLocalRepositoryAsync(localPath, branch, request);
    }

    public Task<GitRepositoryInfo> AnalyzeLocalRepositoryAsync(
        string localPath, string? branch = null, DocumentationRequest? request = null)
    {
        if (!Repository.IsValid(localPath))
            throw new InvalidOperationException($"'{localPath}' is not a valid Git repository.");

        _logger.LogInformation("Analyzing repository at {Path}", localPath);

        using var repo = new Repository(localPath);

        // Checkout specific branch if requested
        if (!string.IsNullOrWhiteSpace(branch))
        {
            var targetBranch = repo.Branches[branch] ?? repo.Branches[$"origin/{branch}"];
            if (targetBranch != null)
                Commands.Checkout(repo, targetBranch);
        }

        var info = new GitRepositoryInfo
        {
            RepositoryPath = localPath,
            RepositoryUrl = repo.Network.Remotes.FirstOrDefault()?.Url ?? string.Empty,
            CurrentBranch = repo.Head.FriendlyName,
            Branches = repo.Branches.Select(b => b.FriendlyName).ToList()
        };

        // Collect recent commits (last 30)
        var commits = repo.Commits.Take(30).ToList();
        info.RecentCommits = commits.Select(c => new GitCommit
        {
            Sha = c.Sha[..8],
            Message = c.MessageShort,
            Author = $"{c.Author.Name} <{c.Author.Email}>",
            Date = c.Author.When.DateTime,
            ChangedFiles = GetChangedFiles(repo, c)
        }).ToList();

        // Collect unique contributors
        info.Contributors = repo.Commits
            .Select(c => c.Author.Name)
            .Distinct()
            .Take(20)
            .ToList();

        info.TotalCommits = repo.Commits.Count().ToString();

        // Scan files for analysis
        var allowedExtensions = request?.FileExtensionsToInclude?.Count > 0
            ? new HashSet<string>(request.FileExtensionsToInclude, StringComparer.OrdinalIgnoreCase)
            : DefaultExtensions;

        int maxFiles = request?.MaxFilesToAnalyze ?? 50;
        info.Files = ScanFiles(localPath, allowedExtensions, maxFiles);

        // Detect technologies from file extensions
        info.Technologies = DetectTechnologies(info.Files);

        // Build directory structure (top 2 levels)
        info.Structure = BuildDirectoryStructure(new DirectoryInfo(localPath), depth: 0, maxDepth: 2);

        return Task.FromResult(info);
    }

    private static List<string> GetChangedFiles(Repository repo, Commit commit)
    {
        if (!commit.Parents.Any())
            return [];

        var parent = commit.Parents.First();
        var diff = repo.Diff.Compare<TreeChanges>(parent.Tree, commit.Tree);
        return diff.Select(d => d.Path).Take(10).ToList();
    }

    private static List<GitFileInfo> ScanFiles(string rootPath, HashSet<string> allowedExtensions, int maxFiles)
    {
        var result = new List<GitFileInfo>();

        foreach (var file in EnumerateSourceFiles(rootPath, allowedExtensions))
        {
            if (result.Count >= maxFiles)
                break;

            var fi = new FileInfo(file);
            var relativePath = Path.GetRelativePath(rootPath, file);

            var fileInfo = new GitFileInfo
            {
                Path = relativePath,
                Extension = fi.Extension,
                Size = fi.Length
            };

            // Read content only for reasonably-sized files (< 100KB)
            if (fi.Length < 100 * 1024 && IsTextFile(fi.Extension))
            {
                try
                {
                    fileInfo.Content = File.ReadAllText(file);
                }
                catch
                {
                    // Skip unreadable files
                }
            }

            result.Add(fileInfo);
        }

        return result;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string path, HashSet<string> allowedExtensions)
    {
        foreach (var dir in Directory.EnumerateDirectories(path))
        {
            var dirName = Path.GetFileName(dir);
            if (SkippedDirectories.Contains(dirName)) continue;

            foreach (var f in EnumerateSourceFiles(dir, allowedExtensions))
                yield return f;
        }

        foreach (var file in Directory.EnumerateFiles(path))
        {
            var ext = Path.GetExtension(file);
            if (allowedExtensions.Contains(ext))
                yield return file;
        }
    }

    private static List<string> DetectTechnologies(List<GitFileInfo> files)
    {
        var techs = new HashSet<string>();
        var extensionCounts = files
            .GroupBy(f => f.Extension.ToLowerInvariant())
            .OrderByDescending(g => g.Count());

        foreach (var group in extensionCounts)
        {
            switch (group.Key)
            {
                case ".cs": techs.Add("C# / .NET"); break;
                case ".ts" or ".tsx": techs.Add("TypeScript"); break;
                case ".js" or ".jsx": techs.Add("JavaScript"); break;
                case ".py": techs.Add("Python"); break;
                case ".java": techs.Add("Java"); break;
                case ".go": techs.Add("Go"); break;
                case ".rs": techs.Add("Rust"); break;
                case ".rb": techs.Add("Ruby"); break;
                case ".php": techs.Add("PHP"); break;
                case ".swift": techs.Add("Swift"); break;
                case ".kt": techs.Add("Kotlin"); break;
                case ".sql": techs.Add("SQL"); break;
            }
        }

        // Detect frameworks from file names
        if (files.Any(f => f.Path.Contains("package.json", StringComparison.OrdinalIgnoreCase)))
            techs.Add("Node.js");
        if (files.Any(f => f.Path.Contains(".csproj", StringComparison.OrdinalIgnoreCase)))
            techs.Add(".NET Project");
        if (files.Any(f => f.Path.Contains("Dockerfile", StringComparison.OrdinalIgnoreCase)))
            techs.Add("Docker");
        if (files.Any(f => f.Path.Contains("docker-compose", StringComparison.OrdinalIgnoreCase)))
            techs.Add("Docker Compose");
        if (files.Any(f => f.Path.Contains("kubernetes") || f.Path.Contains(".k8s.")))
            techs.Add("Kubernetes");

        return [.. techs];
    }

    private static DirectoryStructure BuildDirectoryStructure(DirectoryInfo dir, int depth, int maxDepth)
    {
        var node = new DirectoryStructure { Name = dir.Name, IsDirectory = true };

        if (depth >= maxDepth) return node;

        try
        {
            foreach (var subDir in dir.GetDirectories())
            {
                if (SkippedDirectories.Contains(subDir.Name)) continue;
                node.Children.Add(BuildDirectoryStructure(subDir, depth + 1, maxDepth));
            }

            foreach (var file in dir.GetFiles().Take(20))
            {
                node.Children.Add(new DirectoryStructure { Name = file.Name, IsDirectory = false });
            }
        }
        catch
        {
            // Skip inaccessible directories
        }

        return node;
    }

    private static bool IsTextFile(string extension) =>
        DefaultExtensions.Contains(extension);

    private static string ExtractRepoName(string url)
    {
        var parts = url.TrimEnd('/').Split('/');
        var name = parts.LastOrDefault() ?? "repo";
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }
}
