using DocuGenious.Api.Services;
using DocuGenious.Core.Interfaces;
using DocuGenious.Core.Models;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace DocuGenious.Api.Controllers;

[ApiController]
[Route("api/documentation")]
public class DocumentationController : ControllerBase
{
	private readonly IJiraService _jiraService;
	private readonly IGitService _gitService;
	private readonly IGroqService _groqService;
	private readonly IPdfService _pdfService;
	private readonly JobStatusService _jobStatus;
	private readonly ILogger<DocumentationController> _logger;

	public DocumentationController(
		IJiraService jiraService,
		IGitService gitService,
		IGroqService groqService,
		IPdfService pdfService,
		JobStatusService jobStatus,
		ILogger<DocumentationController> logger)
	{
		_jiraService = jiraService;
		_gitService = gitService;
		_groqService = groqService;
		_pdfService = pdfService;
		_jobStatus = jobStatus;
		_logger = logger;
	}

	[HttpPost("generate")]
	public async Task<IActionResult> GenerateDocumentation([FromBody] DocumentationRequest request)
	{
		if (request == null)
			return BadRequest(new { message = "Request body is required." });

		var jobId = request.JobId;
		void Status(string msg)
		{
			_logger.LogInformation(msg);
			if (!string.IsNullOrWhiteSpace(jobId))
				_jobStatus.Update(jobId, msg);
		}

		try
		{
			List<JiraTicket>? tickets = null;
			GitRepositoryInfo? repoInfo = null;

			if (request.SourceType is SourceType.JiraOnly or SourceType.Both)
			{
				Status("📋 Fetching JIRA tickets…");
				tickets = await _jiraService.GetTicketsAsync(request.JiraTicketIds);
			}

			if (request.SourceType is SourceType.GitOnly or SourceType.Both)
			{
				Status("🔍 Analysing Git repository…");
				repoInfo = !string.IsNullOrWhiteSpace(request.GitRepositoryUrl)
					? await _gitService.CloneAndAnalyzeAsync(
						request.GitRepositoryUrl, request.GitBranch, request)
					: await _gitService.AnalyzeLocalRepositoryAsync(
						request.GitLocalPath!, request.GitBranch, request);
			}

			var jiraContext = tickets != null ? BuildJiraContext(tickets) : "";
			var gitContext = repoInfo != null ? BuildGitContext(repoInfo) : "";

			Status("🤖 Analysing with Groq AI…");

			var result = await _groqService.AnalyzeAsync(
				jiraContext,
				gitContext,
				request.DocumentationType,
				request.AdditionalContext);

			Status("📄 Generating PDF…");
			var filePath = await _pdfService.GeneratePdfAsync(result, "Documentation");

			_jobStatus.Remove(jobId);

			return Ok(new
			{
				success = true,
				fileName = Path.GetFileName(filePath),
				filePath
			});
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Generation failed");
			_jobStatus.Remove(jobId);
			return StatusCode(500, new { message = ex.Message });
		}
	}

	private static string BuildJiraContext(IEnumerable<JiraTicket> tickets)
	{
		var sb = new StringBuilder();
		foreach (var t in tickets)
		{
			sb.AppendLine($"[{t.Key}] {t.Summary}");
			sb.AppendLine(t.Description);
			sb.AppendLine();
		}
		return sb.ToString();
	}

	private static string BuildGitContext(GitRepositoryInfo repo)
	{
		var sb = new StringBuilder();

		// ── Repository metadata ──
		if (!string.IsNullOrWhiteSpace(repo.RepositoryUrl))
			sb.AppendLine($"Repository URL: {repo.RepositoryUrl}");

		if (!string.IsNullOrWhiteSpace(repo.RepositoryPath))
			sb.AppendLine($"Repository Path: {repo.RepositoryPath}");

		if (!string.IsNullOrWhiteSpace(repo.CurrentBranch))
			sb.AppendLine($"Current Branch: {repo.CurrentBranch}");

		if (repo.Branches.Count > 0)
			sb.AppendLine($"Branches: {string.Join(", ", repo.Branches)}");

		if (repo.Contributors.Count > 0)
			sb.AppendLine($"Contributors: {string.Join(", ", repo.Contributors)}");

		if (!string.IsNullOrWhiteSpace(repo.TotalCommits))
			sb.AppendLine($"Total Commits: {repo.TotalCommits}");

		if (repo.Technologies.Count > 0)
			sb.AppendLine($"Technologies: {string.Join(", ", repo.Technologies)}");

		if (!string.IsNullOrWhiteSpace(repo.Description))
		{
			sb.AppendLine();
			sb.AppendLine("Repository Description:");
			sb.AppendLine(repo.Description);
		}

		sb.AppendLine();
		sb.AppendLine(new string('-', 50));

		// ── Recent commits (high signal for documentation) ──
		if (repo.RecentCommits.Count > 0)
		{
			sb.AppendLine("Recent Commits:");
			foreach (var commit in repo.RecentCommits.Take(10))
			{
				sb.AppendLine($"- {commit.Message} (by {commit.Author}, {commit.Date:yyyy-MM-dd})");
			}

			sb.AppendLine();
			sb.AppendLine(new string('-', 50));
		}

		// ── Source files (core input for Groq) ──
		if (repo.Files.Count == 0)
		{
			sb.AppendLine("No source files found.");
			return sb.ToString();
		}

		foreach (var file in repo.Files)
		{
			sb.AppendLine($"File: {file.Path}");

			if (!string.IsNullOrWhiteSpace(file.Content))
				sb.AppendLine(file.Content);
			else
				sb.AppendLine("[File content not available]");

			sb.AppendLine(new string('-', 50));
		}

		return sb.ToString();
	}
}