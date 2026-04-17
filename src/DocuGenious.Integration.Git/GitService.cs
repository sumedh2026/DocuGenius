using DocuGenious.Core.Configuration;
using DocuGenious.Core.Interfaces;
using DocuGenious.Core.Models;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace DocuGenious.Integration.Git;

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

    public async Task<RepoValidationResult> ValidateRemoteRepositoryAsync(
        string repositoryUrl, string? branch = null)
    {
        var result = new RepoValidationResult { RepositoryUrl = repositoryUrl };

        // Use GitHub REST API for github.com URLs — more reliable than LibGit2Sharp for validation
        if (repositoryUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_settings.PersonalAccessToken))
        {
            return await ValidateGitHubRepositoryAsync(repositoryUrl, branch);
        }

        try
        {
            _logger.LogInformation("Validating remote repository via LibGit2Sharp: {Url}", repositoryUrl);

            IEnumerable<Reference> refs = await Task.Run(() =>
                Repository.ListRemoteReferences(repositoryUrl,
                    (url, user, cred) => new UsernamePasswordCredentials
                    {
                        Username = string.IsNullOrWhiteSpace(_settings.Username) ? "git" : _settings.Username,
                        Password = _settings.PersonalAccessToken ?? string.Empty
                    }));

            var refList = refs.ToList();
            result.Accessible = true;

            // Detect default branch from HEAD symref or fall back to common names
            var headRef = refList.FirstOrDefault(r => r.CanonicalName == "HEAD");
            var headTarget = (headRef as SymbolicReference)?.Target?.CanonicalName;
            result.DefaultBranch = headTarget?.Replace("refs/heads/", "")
                ?? refList.FirstOrDefault(r => r.CanonicalName is "refs/heads/main" or "refs/heads/master")
                          ?.CanonicalName.Replace("refs/heads/", "")
                ?? "main";

            if (!string.IsNullOrWhiteSpace(branch))
            {
                result.BranchExists = refList.Any(r =>
                    r.CanonicalName == $"refs/heads/{branch}" ||
                    r.CanonicalName.EndsWith($"/{branch}"));

                if (!result.BranchExists)
                {
                    var available = refList
                        .Where(r => r.CanonicalName.StartsWith("refs/heads/"))
                        .Select(r => r.CanonicalName.Replace("refs/heads/", ""))
                        .Take(5);
                    result.Message = $"Branch '{branch}' not found. Available: {string.Join(", ", available)}";
                }
                else
                {
                    result.Message = $"Repository accessible · branch '{branch}' found";
                }
            }
            else
            {
                result.BranchExists = true;
                result.Message = $"Repository accessible · default branch: {result.DefaultBranch}";
            }
        }
        catch (LibGit2SharpException ex) when (
            ex.Message.Contains("401", StringComparison.Ordinal) ||
            ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase))
        {
            result.Accessible = false;
            result.Message = "Authentication failed (401) — check your Personal Access Token.";
        }
        catch (LibGit2SharpException ex) when (
            ex.Message.Contains("403", StringComparison.Ordinal))
        {
            result.Accessible = false;
            result.Message = "Access denied (403) — your PAT may lack 'repo' scope or the repository is private.";
        }
        catch (LibGit2SharpException ex) when (
            ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("404", StringComparison.Ordinal) ||
            ex.Message.Contains("Repository not found", StringComparison.OrdinalIgnoreCase))
        {
            result.Accessible = false;
            result.Message = "Repository not found — check the URL is correct.";
        }
        catch (Exception ex)
        {
            result.Accessible = false;
            result.Message = $"Cannot reach repository: {ex.Message}";
            _logger.LogWarning(ex, "Remote repository validation failed for {Url}", repositoryUrl);
        }

        return result;
    }

    // ── GitHub-specific validation using REST API ─────────────────────────────
    // Avoids LibGit2Sharp credential issues — uses Bearer token directly via HttpClient

    private async Task<RepoValidationResult> ValidateGitHubRepositoryAsync(
        string repositoryUrl, string? branch)
    {
        var result = new RepoValidationResult { RepositoryUrl = repositoryUrl };

        try
        {
            // Extract owner/repo from URL  (handles .git suffix and trailing slashes)
            // e.g. https://github.com/owner/repo.git  →  owner/repo
            var uri     = new Uri(repositoryUrl.TrimEnd('/').Replace(".git", ""));
            var parts   = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length < 2)
            {
                result.Accessible = false;
                result.Message    = "Could not parse GitHub owner/repo from URL.";
                return result;
            }
            var owner = parts[0];
            var repo  = parts[1];

            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent",       "DocuGenius-Validator");
            http.DefaultRequestHeaders.Add("Authorization",    $"Bearer {_settings.PersonalAccessToken}");
            http.DefaultRequestHeaders.Add("Accept",           "application/vnd.github+json");
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

            // Check repo exists
            var repoResponse = await http.GetAsync($"https://api.github.com/repos/{owner}/{repo}");

            if (repoResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                result.Accessible = false;
                result.Message    = $"Repository '{owner}/{repo}' not found on GitHub — check the URL.";
                return result;
            }

            if (repoResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                result.Accessible = false;
                result.Message    = "GitHub authentication failed (401) — your PAT may be expired or invalid.";
                return result;
            }

            if (repoResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                result.Accessible = false;
                result.Message    = "GitHub access denied (403) — your PAT needs 'repo' scope for private repositories.";
                return result;
            }

            if (!repoResponse.IsSuccessStatusCode)
            {
                result.Accessible = false;
                result.Message    = $"GitHub API returned {(int)repoResponse.StatusCode}.";
                return result;
            }

            // Parse default branch from repo info
            var repoJson = await repoResponse.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(repoJson);
            result.DefaultBranch = doc.RootElement
                .TryGetProperty("default_branch", out var db) ? db.GetString() ?? "main" : "main";
            result.Accessible = true;

            // Check branch exists (if specified)
            if (!string.IsNullOrWhiteSpace(branch))
            {
                var branchResponse = await http.GetAsync(
                    $"https://api.github.com/repos/{owner}/{repo}/branches/{Uri.EscapeDataString(branch)}");

                if (branchResponse.IsSuccessStatusCode)
                {
                    result.BranchExists = true;
                    result.Message      = $"Repository accessible · branch '{branch}' found";
                }
                else
                {
                    // List available branches for a helpful message
                    var listResponse = await http.GetAsync(
                        $"https://api.github.com/repos/{owner}/{repo}/branches?per_page=10");
                    var available    = string.Empty;
                    if (listResponse.IsSuccessStatusCode)
                    {
                        var listJson   = await listResponse.Content.ReadAsStringAsync();
                        using var list = System.Text.Json.JsonDocument.Parse(listJson);
                        available      = string.Join(", ", list.RootElement.EnumerateArray()
                            .Select(b => b.TryGetProperty("name", out var n) ? n.GetString() : null)
                            .Where(n => n != null)
                            .Take(5)!);
                    }

                    result.BranchExists = false;
                    result.Message      = string.IsNullOrWhiteSpace(available)
                        ? $"Branch '{branch}' not found in '{owner}/{repo}'."
                        : $"Branch '{branch}' not found. Available: {available}";
                }
            }
            else
            {
                result.BranchExists = true;
                result.Message      = $"Repository accessible · default branch: {result.DefaultBranch}";
            }
        }
        catch (Exception ex)
        {
            result.Accessible = false;
            result.Message    = $"Error validating GitHub repository: {ex.Message}";
            _logger.LogWarning(ex, "GitHub validation failed for {Url}", repositoryUrl);
        }

        return result;
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
            var relativePath = System.IO.Path.GetRelativePath(rootPath, file);

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
            var dirName = System.IO.Path.GetFileName(dir);
            if (SkippedDirectories.Contains(dirName)) continue;

            foreach (var f in EnumerateSourceFiles(dir, allowedExtensions))
                yield return f;
        }

        foreach (var file in Directory.EnumerateFiles(path))
        {
            var ext = System.IO.Path.GetExtension(file);
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

    private static Core.Models.DirectoryStructure BuildDirectoryStructure(DirectoryInfo dir, int depth, int maxDepth)
    {
        var node = new Core.Models.DirectoryStructure { Name = dir.Name, IsDirectory = true };

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
                node.Children.Add(new Core.Models.DirectoryStructure { Name = file.Name, IsDirectory = false });
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
