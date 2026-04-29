
using DocuGenious.Core.Models;

namespace DocuGenious.Api.Models;

public class AnalyseJiraRequest
{
	public List<JiraTicket> Tickets { get; set; } = [];
	public DocumentationType DocumentationType { get; set; } =
		DocumentationType.FullDocumentation;
}

public class AnalyseGitRequest
{
	public GitRepositoryInfo? RepositoryInfo { get; set; }
	public DocumentationType DocumentationType { get; set; } =
		DocumentationType.FullDocumentation;
}

public class AnalyseCombinedRequest
{
	public List<JiraTicket> Tickets { get; set; } = [];
	public GitRepositoryInfo? RepositoryInfo { get; set; }
	public DocumentationType DocumentationType { get; set; } =
		DocumentationType.FullDocumentation;
}
