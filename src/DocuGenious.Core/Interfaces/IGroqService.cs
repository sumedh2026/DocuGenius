using DocuGenious.Core.Models;

namespace DocuGenious.Core.Interfaces;

public interface IGroqService
{
	/// <summary>
	/// Unified analysis entry point.
	/// jiraContext and gitContext must already be normalized strings.
	/// </summary>
	Task<AnalysisResult> AnalyzeAsync(string jiraContext,string gitContext,DocumentationType docType,string? additionalContext = null);
}
