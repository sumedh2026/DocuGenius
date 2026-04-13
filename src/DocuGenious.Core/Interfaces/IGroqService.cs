using DocuGenious.Core.Models;

namespace DocuGenious.Core.Interfaces;

public interface IGroqService
{
    Task<AnalysisResult> AnalyzeJiraTicketsAsync(List<JiraTicket> tickets, DocumentationType docType);
    Task<AnalysisResult> AnalyzeGitRepositoryAsync(GitRepositoryInfo repoInfo, DocumentationType docType);
    Task<AnalysisResult> AnalyzeCombinedAsync(List<JiraTicket> tickets, GitRepositoryInfo repoInfo, DocumentationType docType);
    Task<bool> ValidateConnectionAsync();
}
