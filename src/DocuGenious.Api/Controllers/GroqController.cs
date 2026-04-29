using DocuGenious.Core.Interfaces;
using DocuGenious.Core.Models;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace DocuGenious.Api.Controllers;

[ApiController]
[Route("api/groq")]
public class GroqController : ControllerBase
{
	private readonly IGroqService _groqService;
	private readonly ILogger<GroqController> _logger;

	public GroqController(IGroqService groqService, ILogger<GroqController> logger)
	{
		_groqService = groqService;
		_logger = logger;
	}

	// ✅ Validation endpoint removed (no longer supported)
	// Groq client validates implicitly on first call

	// ───────────────────────────────────────────────────────────────
	// Analyse JIRA
	// ───────────────────────────────────────────────────────────────
	[HttpPost("analyse/jira")]
	public async Task<IActionResult> AnalyseJira([FromBody] AnalyseJiraRequest request)
	{
		if (request.Tickets == null || request.Tickets.Count == 0)
			return BadRequest(new { message = "At least one ticket is required" });

		try
		{
			var jiraContext = BuildJiraContext(request.Tickets);

			var result = await _groqService.AnalyzeAsync(
				jiraContext,
				gitContext: string.Empty,
				request.DocumentationType);

			return Ok(result);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error analysing JIRA tickets with Groq");
			return StatusCode(500, new { message = ex.Message });
		}
	}

	// ───────────────────────────────────────────────────────────────
	// Analyse Git
	// ───────────────────────────────────────────────────────────────
	[HttpPost("analyse/git")]
	public async Task<IActionResult> AnalyseGit([FromBody] AnalyseGitRequest request)
	{
		if (request.RepositoryInfo == null)
			return BadRequest(new { message = "repositoryInfo is required" });

		try
		{
			var gitContext = BuildGitContext(request.RepositoryInfo);

			var result = await _groqService.AnalyzeAsync(
				jiraContext: string.Empty,
				gitContext,
				request.DocumentationType);

			return Ok(result);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error analysing Git repository with Groq");
			return StatusCode(500, new { message = ex.Message });
		}
	}

	// ───────────────────────────────────────────────────────────────
	// Analyse Combined
	// ───────────────────────────────────────────────────────────────
	[HttpPost("analyse/combined")]
	public async Task<IActionResult> AnalyseCombined([FromBody] AnalyseCombinedRequest request)
	{
		if (request.Tickets.Count == 0)
			return BadRequest(new { message = "At least one ticket is required" });

		if (request.RepositoryInfo == null)
			return BadRequest(new { message = "repositoryInfo is required" });

		try
		{
			var jiraContext = BuildJiraContext(request.Tickets);
			var gitContext = BuildGitContext(request.RepositoryInfo);

			var result = await _groqService.AnalyzeAsync(
				jiraContext,
				gitContext,
				request.DocumentationType);

			return Ok(result);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error analysing combined context with Groq");
			return StatusCode(500, new { message = ex.Message });
		}
	}

	// ───────────────────────────────────────────────────────────────
	// Context builders
	// ───────────────────────────────────────────────────────────────

	private static string BuildJiraContext(IEnumerable<JiraTicket> tickets)
	{
		var sb = new StringBuilder();

		foreach (var t in tickets)
		{
			sb.AppendLine($"Ticket: {t.Key}");
			sb.AppendLine($"Summary: {t.Summary}");
			sb.AppendLine($"Status: {t.Status}");
			sb.AppendLine("Description:");
			sb.AppendLine(t.Description);
			sb.AppendLine(new string('-', 40));
		}

		return sb.ToString();
	}

	private static string BuildGitContext(GitRepositoryInfo repo)
	{
		var sb = new StringBuilder();

		sb.AppendLine($"Repository URL: {repo.RepositoryUrl}");
		sb.AppendLine($"Current Branch: {repo.CurrentBranch}");

		if (repo.Technologies.Count > 0)
			sb.AppendLine($"Technologies: {string.Join(", ", repo.Technologies)}");

		sb.AppendLine(new string('-', 40));

		foreach (var file in repo.Files)
		{
			sb.AppendLine($"File: {file.Path}");

			if (!string.IsNullOrWhiteSpace(file.Content))
				sb.AppendLine(file.Content);

			sb.AppendLine(new string('-', 40));
		}

		return sb.ToString();
	}
}