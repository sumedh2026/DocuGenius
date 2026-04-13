using DocuGenious.Models;

namespace DocuGenious.Services;

public interface IGitService
{
    Task<GitRepositoryInfo> AnalyzeLocalRepositoryAsync(string localPath, string? branch = null, DocumentationRequest? request = null);
    Task<GitRepositoryInfo> CloneAndAnalyzeAsync(string repositoryUrl, string? branch = null, DocumentationRequest? request = null);
    Task<bool> ValidateLocalRepositoryAsync(string localPath);
}
