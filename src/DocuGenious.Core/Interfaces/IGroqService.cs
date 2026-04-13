using DocuGenious.Core.Models;

namespace DocuGenious.Core.Interfaces;

public interface IGroqService
{
    Task<AnalysisResult> AnalyzeJiraTicketsAsync(List<JiraTicket> tickets, DocumentationType docType, string? additionalContext = null);
    Task<AnalysisResult> AnalyzeGitRepositoryAsync(GitRepositoryInfo repoInfo, DocumentationType docType, string? additionalContext = null);
    Task<AnalysisResult> AnalyzeCombinedAsync(List<JiraTicket> tickets, GitRepositoryInfo repoInfo, DocumentationType docType, string? additionalContext = null);
    Task<bool> ValidateConnectionAsync();
}
