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

        Atlassian.Jira.Issue issue;
        try
        {
            issue = await _jiraClient.Issues.GetIssueAsync(ticketId);
        }
        catch (Exception ex)
        {
            // The Atlassian SDK wraps JIRA API errors in generic exceptions and
            // embeds the raw JSON body in the message, e.g.:
            //   "Response Content: {"errorMessages":["Issue does not exist or you
            //    do not have permission to see it."],"errors":{}}"
            // Parse that JSON so callers always get a plain English sentence.
            var friendly = ExtractJiraApiError(ex.Message);
            _logger.LogWarning("JIRA SDK threw while fetching {TicketId}: {Msg}", ticketId, ex.Message);
            throw new KeyNotFoundException(
                friendly ?? $"Ticket '{ticketId}' was not found or could not be accessed.", ex);
        }

        if (issue == null)
            throw new KeyNotFoundException($"JIRA ticket '{ticketId}' was not found.");

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
        var idList    = ticketIds.ToList();
        var tickets   = new List<Core.Models.JiraTicket>();
        var failedIds = new List<string>();

        foreach (var id in idList)
        {
            try
            {
                tickets.Add(await GetTicketAsync(id));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch ticket {TicketId}: {Message}", id, ex.Message);
                failedIds.Add(id);
            }
        }

        if (tickets.Count > 0 && failedIds.Count > 0)
        {
            // Partial success — proceed with what we have but warn so it shows up in logs
            _logger.LogWarning(
                "Partial JIRA fetch: {SuccessCount} succeeded, {FailCount} failed ({FailedIds}). " +
                "Continuing with the tickets that were fetched.",
                tickets.Count, failedIds.Count, string.Join(", ", failedIds));
        }
        else if (tickets.Count == 0 && failedIds.Count > 0)
        {
            // Every requested ticket failed — surface a clear error instead of sending
            // empty source data to Groq, which would cause hallucinated content.
            throw new InvalidOperationException(
                $"Could not fetch any of the requested JIRA ticket(s): {string.Join(", ", failedIds)}. " +
                "Please verify the ticket IDs exist and that your JIRA credentials have access to them.");
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

    /// <summary>
    /// The Atlassian SDK embeds the raw JIRA REST response in exception messages.
    /// This helper finds the JSON fragment and extracts the first human-readable
    /// entry from the <c>errorMessages</c> array (JIRA's standard error format).
    /// Returns null if the message contains no parseable JSON error.
    /// </summary>
    private static string? ExtractJiraApiError(string exceptionMessage)
    {
        // Locate the start of the JSON object the SDK includes in the message
        var jsonStart = exceptionMessage.IndexOf('{');
        if (jsonStart < 0) return null;

        try
        {
            var json = exceptionMessage[jsonStart..];
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Standard JIRA error shape: { "errorMessages": ["..."], "errors": {} }
            if (root.TryGetProperty("errorMessages", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                var messages = arr.EnumerateArray()
                    .Select(el => el.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                if (messages.Count > 0)
                    return string.Join(" ", messages)
                        .Replace("Issue does not exist", "JIRA Ticket does not exist",
                                 StringComparison.OrdinalIgnoreCase)
                        .Replace("Issue ", "JIRA Ticket ",
                                 StringComparison.OrdinalIgnoreCase);
            }

            // Fallback: some JIRA errors use a top-level "message" field
            if (root.TryGetProperty("message", out var msg) &&
                msg.ValueKind == JsonValueKind.String)
                return msg.GetString();
        }
        catch { /* not parseable JSON — fall through and return null */ }

        return null;
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
