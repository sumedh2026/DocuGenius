using DocuGenious.Core.Models;

namespace DocuGenious.Core.Interfaces;

public interface IGitService
{
    Task<GitRepositoryInfo> AnalyzeLocalRepositoryAsync(string localPath, string? branch = null, DocumentationRequest? request = null);
    Task<GitRepositoryInfo> CloneAndAnalyzeAsync(string repositoryUrl, string? branch = null, DocumentationRequest? request = null);
    Task<bool> ValidateLocalRepositoryAsync(string localPath);
}
