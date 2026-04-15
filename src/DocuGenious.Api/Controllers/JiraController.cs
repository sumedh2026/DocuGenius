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
            catch (KeyNotFoundException)
            {
                item.Exists  = false;
                item.Message = $"Ticket '{ticketId}' not found in JIRA.";
            }
            catch (Exception ex)
            {
                item.Exists  = false;
                item.Message = $"Error checking '{ticketId}': {ex.Message}";
                _logger.LogWarning(ex, "Error validating ticket {TicketId}", ticketId);
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
