using DocuGenious.Api.Services;
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
        _gitService  = gitService;
        _groqService = groqService;
        _pdfService  = pdfService;
        _jobStatus   = jobStatus;
        _logger      = logger;
    }

    /// <summary>
    /// All-in-one endpoint: fetches JIRA/Git data, analyses with Gemini AI, generates PDF, returns file path.
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

        var jobId = request.JobId;
        void Status(string msg)
        {
            _logger.LogInformation("{Msg}", msg);
            if (!string.IsNullOrWhiteSpace(jobId))
                _jobStatus.Update(jobId, msg);
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
                var count = request.JiraTicketIds.Count;
                Status($"📋 Fetching {count} JIRA ticket{(count == 1 ? "" : "s")}…");
                tickets = await _jiraService.GetTicketsAsync(request.JiraTicketIds);

                // Guard: if every ticket failed the service throws, but defend here too so
                // we never send empty source data to Gemini (which causes hallucinated content).
                if (tickets.Count == 0)
                    return BadRequest(new
                    {
                        message = $"Could not fetch any of the requested JIRA ticket(s): " +
                                  $"{string.Join(", ", request.JiraTicketIds)}. " +
                                  "Please verify the ticket IDs exist and that your JIRA credentials have access."
                    });

                Status($"✅ Fetched {tickets.Count} JIRA ticket{(tickets.Count == 1 ? "" : "s")} successfully");
            }
            // User Guide requires every ticket to be Done or Complete
            if (request.DocumentationType == DocumentationType.UserGuide && tickets != null && tickets.Count > 0)
            {
                var doneStatuses = new[] { "done", "complete", "completed" };
                var notDoneTickets = tickets
                    .Where(t => !doneStatuses.Any(s =>
                        t.Status?.Contains(s, StringComparison.OrdinalIgnoreCase) == true))
                    .ToList();

                if (notDoneTickets.Count > 0)
                {
                    var details = string.Join(", ", notDoneTickets.Select(t =>
                        $"{t.Key} (status: {(string.IsNullOrWhiteSpace(t.Status) ? "unknown" : t.Status)})"));

                    return BadRequest(new
                    {
                        message =
                            "User Guide can only be generated for completed tickets. " +
                            $"The following ticket(s) are not done yet: {details}. " +
                            "Please make sure all tickets have status 'Done' or 'Complete' before generating a User Guide."
                    });
                }
            }
			// Step 2: Fetch Git repository
			if (request.SourceType is SourceType.GitOnly or SourceType.Both)
            {
                if (!string.IsNullOrWhiteSpace(request.GitRepositoryUrl))
                {
                    Status("🔍 Cloning and analysing remote Git repository…");
                    repoInfo = await _gitService.CloneAndAnalyzeAsync(request.GitRepositoryUrl, request.GitBranch, request);
                }
                else
                {
                    Status($"🔍 Analysing local repository…");
                    repoInfo = await _gitService.AnalyzeLocalRepositoryAsync(request.GitLocalPath!, request.GitBranch, request);
                }
                Status("✅ Repository analysed successfully");
            }

            // Step 3: Analyse with Gemini AI
            Status($"🤖 Analysing with Gemini AI — generating {request.DocumentationType} (this may take a moment)…");
            AnalysisResult analysisResult = request.SourceType switch
            {
                SourceType.JiraOnly => await _groqService.AnalyzeJiraTicketsAsync(tickets!, request.DocumentationType, request.AdditionalContext),
                SourceType.GitOnly  => await _groqService.AnalyzeGitRepositoryAsync(repoInfo!, request.DocumentationType, request.AdditionalContext),
                _                   => await _groqService.AnalyzeCombinedAsync(tickets!, repoInfo!, request.DocumentationType, request.AdditionalContext)
            };
            Status("✅ AI analysis complete");

            // Step 4: Generate PDF
            var outputFileName = string.IsNullOrWhiteSpace(request.OutputFileName)
                ? BuildDefaultFileName(request)
                : request.OutputFileName;

            Status($"📄 Building PDF document…");
            var filePath = await _pdfService.GeneratePdfAsync(analysisResult, outputFileName);

            Status("✅ Document ready!");
            if (!string.IsNullOrWhiteSpace(jobId))
                _jobStatus.Remove(jobId);   // clean up immediately on success

            return Ok(new
            {
                success  = true,
                filePath,
                fileName = Path.GetFileName(filePath)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating documentation");
            if (!string.IsNullOrWhiteSpace(jobId))
                _jobStatus.Remove(jobId);   // clean up on error too
            return StatusCode(500, new { message = ex.Message });
        }
    }

    /// <summary>
    /// Returns the current progress status for a running generation job.
    /// The Blazor client polls this endpoint every ~1.5 s while generating.
    /// Returns 204 No Content when the job is unknown or has already completed.
    /// </summary>
    [HttpGet("status/{jobId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult GetStatus(string jobId)
    {
        var status = _jobStatus.Get(jobId);
        return status is null
            ? NoContent()
            : Ok(new { status });
    }

    /// <summary>Downloads a previously generated PDF by file name.</summary>
    [HttpGet("download/{fileName}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DownloadPdf(string fileName)
    {
        // Sanitise to prevent path traversal
        var safeName = Path.GetFileName(fileName);
        var outputDir = Path.GetFullPath("./output");
        var fullPath  = Path.Combine(outputDir, safeName);

        if (!System.IO.File.Exists(fullPath))
            return NotFound(new { message = $"File '{safeName}' not found." });

        var bytes = System.IO.File.ReadAllBytes(fullPath);
        return File(bytes, "application/pdf", safeName);
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
