using DocuGenious.Core.Interfaces;
using DocuGenious.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace DocuGenious.Api.Controllers;

[ApiController]
[Route("api/jira")]
public class JiraController : ControllerBase
{
    private readonly IJiraService _jiraService;
    private readonly ILogger<JiraController> _logger;

    public JiraController(IJiraService jiraService, ILogger<JiraController> logger)
    {
        _jiraService = jiraService;
        _logger = logger;
    }

    /// <summary>
    /// Get a single JIRA ticket by ID.
    /// </summary>
    [HttpGet("ticket/{ticketId}")]
    [ProducesResponseType(typeof(JiraTicket), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetTicket(string ticketId)
    {
        try
        {
            var ticket = await _jiraService.GetTicketAsync(ticketId);
            return Ok(ticket);
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning(ex, "Ticket {TicketId} not found", ticketId);
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching ticket {TicketId}", ticketId);
            return StatusCode(500, new { message = ex.Message });
        }
    }

    /// <summary>
    /// Get multiple JIRA tickets by IDs.
    /// </summary>
    [HttpPost("tickets")]
    [ProducesResponseType(typeof(List<JiraTicket>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetTickets([FromBody] GetTicketsRequest request)
    {
        try
        {
            var tickets = await _jiraService.GetTicketsAsync(request.TicketIds);
            return Ok(tickets);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching tickets");
            return StatusCode(500, new { message = ex.Message });
        }
    }

    /// <summary>
    /// Validate one or more JIRA ticket IDs — checks existence, status and summary.
    /// </summary>
    [HttpPost("validate-tickets")]
    [ProducesResponseType(typeof(List<TicketValidationResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ValidateTickets([FromBody] GetTicketsRequest request)
    {
        var results = new List<TicketValidationResult>();

        foreach (var ticketId in request.TicketIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var item = new TicketValidationResult { TicketId = ticketId.ToUpper() };
            try
            {
                var ticket = await _jiraService.GetTicketAsync(ticketId);
                item.Exists  = true;
                item.Summary = ticket.Summary;
                item.Status  = ticket.Status;
                item.Message = string.IsNullOrWhiteSpace(ticket.Status)
                    ? ticket.Summary
                    : $"{ticket.Summary}  [{ticket.Status}]";
            }
            catch (KeyNotFoundException ex)
            {
                // JiraService already translates SDK JSON errors to plain English here
                item.Exists  = false;
                item.Message = ex.Message;
            }
            catch (Exception ex)
            {
                // Last-resort fallback: strip any raw JSON the SDK may still embed
                item.Exists  = false;
                item.Message = JiraControllerHelpers.ExtractReadableMessage(ex.Message)
                    ?? $"Could not check ticket '{ticketId}'. Please try again.";
                _logger.LogWarning(ex, "Unexpected error validating ticket {TicketId}", ticketId);
            }
            results.Add(item);
        }

        return Ok(results);
    }

    /// <summary>
    /// Validate the JIRA connection.
    /// </summary>
    [HttpGet("validate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ValidateConnection()
    {
        try
        {
            var connected = await _jiraService.ValidateConnectionAsync();
            return Ok(new { connected });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JIRA connection validation failed");
            return Ok(new { connected = false, error = ex.Message });
        }
    }
}

public class GetTicketsRequest
{
    public List<string> TicketIds { get; set; } = [];
}

file static class JiraControllerHelpers
{
    /// <summary>
    /// Tries to extract a human-readable sentence from an exception message that
    /// may contain a raw JIRA JSON body (e.g. the Atlassian SDK error format).
    /// Returns null when no JSON or no recognisable field is found.
    /// </summary>
    internal static string? ExtractReadableMessage(string exceptionMessage)
    {
        var jsonStart = exceptionMessage.IndexOf('{');
        if (jsonStart < 0) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(exceptionMessage[jsonStart..]);
            var root = doc.RootElement;

            if (root.TryGetProperty("errorMessages", out var arr) &&
                arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var msgs = arr.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
                if (msgs.Count > 0) return string.Join(" ", msgs);
            }
            foreach (var prop in new[] { "message", "detail", "error" })
            {
                if (root.TryGetProperty(prop, out var el) &&
                    el.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var v = el.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }
        }
        catch { /* not JSON */ }
        return null;
    }
}
