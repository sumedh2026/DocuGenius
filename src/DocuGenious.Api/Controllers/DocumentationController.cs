using DocuGenious.Core.Interfaces;
using DocuGenious.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace DocuGenious.Api.Controllers;

[ApiController]
[Route("api/documentation")]
public class DocumentationController : ControllerBase
{
    private readonly IJiraService _jiraService;
    private readonly IGitService _gitService;
    private readonly IGroqService _groqService;
    private readonly IPdfService _pdfService;
    private readonly ILogger<DocumentationController> _logger;

    public DocumentationController(
        IJiraService jiraService,
        IGitService gitService,
        IGroqService groqService,
        IPdfService pdfService,
        ILogger<DocumentationController> logger)
    {
        _jiraService = jiraService;
        _gitService = gitService;
        _groqService = groqService;
        _pdfService = pdfService;
        _logger = logger;
    }

    /// <summary>
    /// All-in-one endpoint: fetches JIRA/Git data, analyses with Groq, generates PDF, returns file path.
    /// </summary>
    [HttpPost("generate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GenerateDocumentation([FromBody] DocumentationRequest request)
    {
        if (request == null)
            return BadRequest(new { message = "Request body is required" });

        // Validate based on source type
        if (request.SourceType is SourceType.JiraOnly or SourceType.Both)
        {
            if (request.JiraTicketIds.Count == 0 && string.IsNullOrWhiteSpace(request.JiraTicketId))
                return BadRequest(new { message = "At least one JIRA ticket ID is required" });
        }

        if (request.SourceType is SourceType.GitOnly or SourceType.Both)
        {
            if (string.IsNullOrWhiteSpace(request.GitRepositoryUrl) && string.IsNullOrWhiteSpace(request.GitLocalPath))
                return BadRequest(new { message = "Either gitRepositoryUrl or gitLocalPath is required" });
        }

        try
        {
            List<JiraTicket>? tickets = null;
            GitRepositoryInfo? repoInfo = null;

            // Normalise single ticket ID into the list
            if (!string.IsNullOrWhiteSpace(request.JiraTicketId) && !request.JiraTicketIds.Contains(request.JiraTicketId))
                request.JiraTicketIds.Insert(0, request.JiraTicketId);

            // Step 1: Fetch JIRA tickets
            if (request.SourceType is SourceType.JiraOnly or SourceType.Both)
            {
                _logger.LogInformation("Fetching {Count} JIRA ticket(s)...", request.JiraTicketIds.Count);
                tickets = await _jiraService.GetTicketsAsync(request.JiraTicketIds);
            }

            // Step 2: Fetch Git repository
            if (request.SourceType is SourceType.GitOnly or SourceType.Both)
            {
                if (!string.IsNullOrWhiteSpace(request.GitRepositoryUrl))
                {
                    _logger.LogInformation("Cloning and analysing remote repository...");
                    repoInfo = await _gitService.CloneAndAnalyzeAsync(request.GitRepositoryUrl, request.GitBranch, request);
                }
                else
                {
                    _logger.LogInformation("Analysing local repository at {Path}...", request.GitLocalPath);
                    repoInfo = await _gitService.AnalyzeLocalRepositoryAsync(request.GitLocalPath!, request.GitBranch, request);
                }
            }

            // Step 3: Analyse with Groq
            _logger.LogInformation("Analysing with Groq ({DocType})...", request.DocumentationType);
            AnalysisResult analysisResult = request.SourceType switch
            {
                SourceType.JiraOnly => await _groqService.AnalyzeJiraTicketsAsync(tickets!, request.DocumentationType),
                SourceType.GitOnly  => await _groqService.AnalyzeGitRepositoryAsync(repoInfo!, request.DocumentationType),
                _                   => await _groqService.AnalyzeCombinedAsync(tickets!, repoInfo!, request.DocumentationType)
            };

            // Step 4: Generate PDF
            var outputFileName = string.IsNullOrWhiteSpace(request.OutputFileName)
                ? BuildDefaultFileName(request)
                : request.OutputFileName;

            _logger.LogInformation("Generating PDF: {FileName}", outputFileName);
            var filePath = await _pdfService.GeneratePdfAsync(analysisResult, outputFileName);

            return Ok(new
            {
                filePath,
                fileName = Path.GetFileName(filePath)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating documentation");
            return StatusCode(500, new { message = ex.Message });
        }
    }

    private static string BuildDefaultFileName(DocumentationRequest request)
    {
        var parts = new List<string>();

        if (request.JiraTicketIds.Count > 0)
            parts.Add(request.JiraTicketIds[0]);

        if (!string.IsNullOrWhiteSpace(request.GitRepositoryUrl))
        {
            var repoName = request.GitRepositoryUrl.TrimEnd('/').Split('/').LastOrDefault() ?? "repo";
            parts.Add(repoName.Replace(".git", ""));
        }

        parts.Add(request.DocumentationType.ToString());
        parts.Add(DateTime.UtcNow.ToString("yyyyMMdd"));

        return string.Join("_", parts);
    }
}
