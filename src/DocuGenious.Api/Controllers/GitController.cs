using DocuGenious.Core.Interfaces;
using DocuGenious.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace DocuGenious.Api.Controllers;

[ApiController]
[Route("api/git")]
public class GitController : ControllerBase
{
    private readonly IGitService _gitService;
    private readonly ILogger<GitController> _logger;

    public GitController(IGitService gitService, ILogger<GitController> logger)
    {
        _gitService = gitService;
        _logger = logger;
    }

    /// <summary>
    /// Analyse a local Git repository.
    /// </summary>
    [HttpPost("analyse/local")]
    [ProducesResponseType(typeof(GitRepositoryInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> AnalyseLocal([FromBody] AnalyseLocalRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.LocalPath))
            return BadRequest(new { message = "localPath is required" });

        try
        {
            var info = await _gitService.AnalyzeLocalRepositoryAsync(request.LocalPath, request.Branch);
            return Ok(info);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid local repository path: {Path}", request.LocalPath);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analysing local repository");
            return StatusCode(500, new { message = ex.Message });
        }
    }

    /// <summary>
    /// Clone and analyse a remote Git repository.
    /// </summary>
    [HttpPost("analyse/remote")]
    [ProducesResponseType(typeof(GitRepositoryInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> AnalyseRemote([FromBody] AnalyseRemoteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RepositoryUrl))
            return BadRequest(new { message = "repositoryUrl is required" });

        try
        {
            var info = await _gitService.CloneAndAnalyzeAsync(request.RepositoryUrl, request.Branch);
            return Ok(info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analysing remote repository");
            return StatusCode(500, new { message = ex.Message });
        }
    }
}

public class AnalyseLocalRequest
{
    public string LocalPath { get; set; } = string.Empty;
    public string? Branch { get; set; }
}

public class AnalyseRemoteRequest
{
    public string RepositoryUrl { get; set; } = string.Empty;
    public string? Branch { get; set; }
}
