using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Atlassian.Jira;
using DocuGenious.Core.Configuration;
using DocuGenious.Core.Interfaces;
using DocuGenious.Core.Models;
using Microsoft.Extensions.Logging;

namespace DocuGenious.Integration.Jira;

public class JiraService : IJiraService
{
    private readonly Atlassian.Jira.Jira _jiraClient;
    private readonly HttpClient _httpClient;
    private readonly ILogger<JiraService> _logger;

    public JiraService(AppSettings settings, ILogger<JiraService> logger)
    {
        _logger = logger;

        if (string.IsNullOrWhiteSpace(settings.Jira.BaseUrl) ||
            string.IsNullOrWhiteSpace(settings.Jira.Username) ||
            string.IsNullOrWhiteSpace(settings.Jira.ApiToken))
        {
            throw new InvalidOperationException(
                "JIRA configuration is incomplete. Please set BaseUrl, Username, and ApiToken in appsettings.json.");
        }

        // Atlassian.SDK: authenticate using username + API token (Basic Auth)
        _jiraClient = Atlassian.Jira.Jira.CreateRestClient(
            url: settings.Jira.BaseUrl,
            username: settings.Jira.Username,
            password: settings.Jira.ApiToken
        );
        _jiraClient.Issues.MaxIssuesPerRequest = 50;

        // HttpClient for Jira REST API v3 endpoints (SDK uses deprecated v2 search)
        var basicAuth = Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{settings.Jira.Username}:{settings.Jira.ApiToken}"));

        _httpClient = new HttpClient { BaseAddress = new Uri(settings.Jira.BaseUrl.TrimEnd('/') + "/") };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", basicAuth);
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<bool> ValidateConnectionAsync()
    {
        try
        {
            var projects = await _jiraClient.Projects.GetProjectsAsync();
            _logger.LogInformation("JIRA connection validated. Found {Count} projects.", projects.Count());
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate JIRA connection.");
            return false;
        }
    }

    public async Task<Core.Models.JiraTicket> GetTicketAsync(string ticketId)
    {
        _logger.LogInformation("Fetching JIRA ticket: {TicketId}", ticketId);

        var issue = await _jiraClient.Issues.GetIssueAsync(ticketId);

        if (issue == null)
            throw new KeyNotFoundException($"JIRA ticket '{ticketId}' not found.");

        var comments = await issue.GetCommentsAsync();

        var ticket = new Core.Models.JiraTicket
        {
            Key = issue.Key.Value,
            Summary = issue.Summary ?? string.Empty,
            Description = issue.Description ?? string.Empty,
            Status = issue.Status?.Name ?? string.Empty,
            Priority = issue.Priority?.Name ?? string.Empty,
            Assignee = issue.Assignee ?? string.Empty,
            Reporter = issue.Reporter ?? string.Empty,
            IssueType = issue.Type?.Name ?? string.Empty,
            CreatedDate = issue.Created,
            UpdatedDate = issue.Updated,
            ProjectKey = issue.Project ?? string.Empty,
            Comments = comments.Select(c => new Core.Models.JiraComment
            {
                Author = c.Author ?? string.Empty,
                Body = c.Body ?? string.Empty,
                CreatedDate = c.CreatedDate
            }).ToList()
        };

        if (issue.Labels != null)
            ticket.Labels.AddRange(issue.Labels);

        foreach (var component in issue.Components)
            ticket.Components.Add(component.Name);

        ticket.AcceptanceCriteria = ExtractAcceptanceCriteria(ticket.Description);

        // Use REST API v3 directly — SDK's JQL search uses the removed v2 endpoint
        ticket.SubTasks = await FetchSubTasksAsync(ticketId);

        return ticket;
    }

    public async Task<List<Core.Models.JiraTicket>> GetTicketsAsync(IEnumerable<string> ticketIds)
    {
        var tickets = new List<Core.Models.JiraTicket>();

        foreach (var id in ticketIds)
        {
            try
            {
                tickets.Add(await GetTicketAsync(id));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch ticket {TicketId}", id);
            }
        }

        return tickets;
    }

    // ─── Private helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Calls /rest/api/3/search/jql directly because Atlassian.SDK still targets
    /// the removed /rest/api/2/search endpoint (HTTP 410 on Jira Cloud).
    /// </summary>
    private async Task<List<Core.Models.JiraTicket>> FetchSubTasksAsync(string parentKey)
    {
        var subTasks = new List<Core.Models.JiraTicket>();

        try
        {
            var jql = Uri.EscapeDataString($"parent = {parentKey}");
            var fieldList = Uri.EscapeDataString("summary,status,issuetype");
            var url = $"rest/api/3/search/jql?jql={jql}&maxResults=20&fields={fieldList}";

            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Sub-task fetch returned {Status} for {Key}", response.StatusCode, parentKey);
                return subTasks;
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("issues", out var issues))
                return subTasks;

            foreach (var issue in issues.EnumerateArray())
            {
                var key = issue.GetProperty("key").GetString() ?? string.Empty;
                var fields = issue.GetProperty("fields");

                subTasks.Add(new Core.Models.JiraTicket
                {
                    Key = key,
                    Summary = fields.TryGetProperty("summary", out var s) ? s.GetString() ?? string.Empty : string.Empty,
                    Status = fields.TryGetProperty("status", out var st)
                        && st.TryGetProperty("name", out var sn) ? sn.GetString() ?? string.Empty : string.Empty,
                    IssueType = fields.TryGetProperty("issuetype", out var it)
                        && it.TryGetProperty("name", out var itn) ? itn.GetString() ?? string.Empty : string.Empty
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch sub-tasks for {TicketId}", parentKey);
        }

        return subTasks;
    }

    private static List<string> ExtractAcceptanceCriteria(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return [];

        var criteria = new List<string>();
        var lines = description.Split('\n');
        bool inAcceptanceCriteria = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.Contains("Acceptance Criteria", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("AC:", StringComparison.OrdinalIgnoreCase))
            {
                inAcceptanceCriteria = true;
                continue;
            }

            if (inAcceptanceCriteria && trimmed.StartsWith("h") && trimmed.Contains(". "))
                break;

            if (inAcceptanceCriteria && (trimmed.StartsWith("*") || trimmed.StartsWith("-") || trimmed.StartsWith("#")))
                criteria.Add(trimmed.TrimStart('*', '-', '#', ' '));
        }

        return criteria;
    }
}
