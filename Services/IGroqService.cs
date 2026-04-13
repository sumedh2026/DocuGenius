using DocuGenious.Models;

namespace DocuGenious.Services;

public interface IGroqService
{
    Task<AnalysisResult> AnalyzeJiraTicketsAsync(List<JiraTicket> tickets, DocumentationType docType);
    Task<AnalysisResult> AnalyzeGitRepositoryAsync(GitRepositoryInfo repoInfo, DocumentationType docType);
    Task<AnalysisResult> AnalyzeCombinedAsync(List<JiraTicket> tickets, GitRepositoryInfo repoInfo, DocumentationType docType);
    Task<bool> ValidateConnectionAsync();
}
