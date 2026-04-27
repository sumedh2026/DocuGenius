using DocuGenious.Core.Interfaces;
using DocuGenious.Core.Models;
using Microsoft.AspNetCore.Mvc;

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

    /// <summary>
    /// Validate the Gemini AI connection.
    /// </summary>
    [HttpGet("validate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ValidateConnection()
    {
        try
        {
            var connected = await _groqService.ValidateConnectionAsync();
            return Ok(new { connected });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gemini AI connection validation failed");
            return Ok(new { connected = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Analyse JIRA tickets using Gemini AI.
    /// </summary>
    [HttpPost("analyse/jira")]
    [ProducesResponseType(typeof(AnalysisResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> AnalyseJira([FromBody] AnalyseJiraRequest request)
    {
        if (request.Tickets == null || request.Tickets.Count == 0)
            return BadRequest(new { message = "At least one ticket is required" });

        try
        {
            var result = await _groqService.AnalyzeJiraTicketsAsync(request.Tickets, request.DocumentationType);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analysing JIRA tickets with Gemini AI");
            return StatusCode(500, new { message = ex.Message });
        }
    }

    /// <summary>
    /// Analyse a Git repository using Gemini AI.
    /// </summary>
    [HttpPost("analyse/git")]
    [ProducesResponseType(typeof(AnalysisResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> AnalyseGit([FromBody] AnalyseGitRequest request)
    {
        if (request.RepositoryInfo == null)
            return BadRequest(new { message = "repositoryInfo is required" });

        try
        {
            var result = await _groqService.AnalyzeGitRepositoryAsync(request.RepositoryInfo, request.DocumentationType);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analysing Git repository with Gemini AI");
            return StatusCode(500, new { message = ex.Message });
        }
    }

    /// <summary>
    /// Analyse combined JIRA + Git context using Gemini AI.
    /// </summary>
    [HttpPost("analyse/combined")]
    [ProducesResponseType(typeof(AnalysisResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> AnalyseCombined([FromBody] AnalyseCombinedRequest request)
    {
        if (request.Tickets == null || request.Tickets.Count == 0)
            return BadRequest(new { message = "At least one ticket is required" });
        if (request.RepositoryInfo == null)
            return BadRequest(new { message = "repositoryInfo is required" });

        try
        {
            var result = await _groqService.AnalyzeCombinedAsync(
                request.Tickets, request.RepositoryInfo, request.DocumentationType);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analysing combined context with Gemini AI");
            return StatusCode(500, new { message = ex.Message });
        }
    }
}

public class AnalyseJiraRequest
{
    public List<JiraTicket> Tickets { get; set; } = [];
    public DocumentationType DocumentationType { get; set; } = DocumentationType.FullDocumentation;
}

public class AnalyseGitRequest
{
    public GitRepositoryInfo? RepositoryInfo { get; set; }
    public DocumentationType DocumentationType { get; set; } = DocumentationType.FullDocumentation;
}

public class AnalyseCombinedRequest
{
    public List<JiraTicket> Tickets { get; set; } = [];
    public GitRepositoryInfo? RepositoryInfo { get; set; }
    public DocumentationType DocumentationType { get; set; } = DocumentationType.FullDocumentation;
}
