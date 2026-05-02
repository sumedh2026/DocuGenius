using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocuGenious.Blazor.Models;

namespace DocuGenious.Blazor.Services;

public partial class ValidationService
{
    private readonly HttpClient _http;

    // JIRA ticket ID: PROJECT-123  (uppercase letters/digits/underscore, dash, one or more digits)
    [GeneratedRegex(@"^[A-Z][A-Z0-9_]+-\d+$")]
    private static partial Regex TicketFormatRegex();

    // Git remote URL: https://, http://, git@, git://
    [GeneratedRegex(@"^(https?://|git@|git://).+", RegexOptions.IgnoreCase)]
    private static partial Regex GitUrlRegex();

    // Invalid file-name characters on Windows
    [GeneratedRegex(@"[<>:""/\\|?*]")]
    private static partial Regex InvalidFileNameRegex();

    // Valid branch name (no spaces, no ~^:?*[\, not starting/ending with .)
    [GeneratedRegex(@"^(?!\.)[^\s~^:?*\[\\]+(?<!\.)$")]
    private static partial Regex BranchNameRegex();

    // Statuses that mean "User Guide ready"
    private static readonly string[] UserGuideDoneStatuses =
        ["done", "complete", "completed"];

    // Statuses that indicate a ticket is already closed (warn for non-UserGuide types)
    private static readonly string[] ClosedStatuses =
        ["done", "closed", "resolved", "cancelled", "rejected", "complete", "completed"];

    public ValidationService(HttpClient http) => _http = http;

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Run all client-side and server-side validations.
    /// Calls <paramref name="onRowUpdated"/> with the current row list after each
    /// row changes so the UI can re-render with live feedback.
    /// Returns false if any hard failure was found (warnings are allowed through).
    /// </summary>
    public async Task<(List<ValidationRow> Rows, bool CanProceed)> ValidateAsync(
        string sourceType,
        List<string> ticketIds,
        string? repoUrl,
        string? branch,
        string? outputFileName,
        string docType,
        Action<List<ValidationRow>> onRowUpdated)
    {
        var rows = new List<ValidationRow>();
        bool hasFailure = false;

        // ── 1. Client-side checks (instant) ───────────────────────────────────

        // Duplicate ticket IDs
        var dupes = ticketIds
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (dupes.Any())
        {
            rows.Add(ValidationRow.Warn("Duplicate ticket IDs",
                $"Removed duplicates: {string.Join(", ", dupes)}. Each ticket is checked once."));
            onRowUpdated(rows);
        }

        var uniqueTickets = ticketIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // JIRA ticket format
        if (sourceType is "JiraOnly" or "Both")
        {
            foreach (var id in uniqueTickets)
            {
                if (!TicketFormatRegex().IsMatch(id.ToUpper()))
                {
                    rows.Add(ValidationRow.Fail($"Invalid ticket format: {id}",
                        $"'{id}' is not a valid JIRA ticket ID. Expected format: PROJECT-123 (e.g. SCRUM-42)"));
                    hasFailure = true;
                    onRowUpdated(rows);
                }
            }
        }

        // Git URL format
        if (sourceType is "GitOnly" or "Both" && !string.IsNullOrWhiteSpace(repoUrl))
        {
            if (!GitUrlRegex().IsMatch(repoUrl))
            {
                rows.Add(ValidationRow.Fail("Invalid repository URL",
                    "The URL must start with https://, http://, git@, or git://. " +
                    "Example: https://github.com/org/repo"));
                hasFailure = true;
                onRowUpdated(rows);
            }
        }

        // Branch name format
        if (!string.IsNullOrWhiteSpace(branch) && !BranchNameRegex().IsMatch(branch))
        {
            rows.Add(ValidationRow.Fail("Invalid branch name",
                $"'{branch}' contains characters that are not allowed in a git branch name " +
                "(spaces, ~, ^, :, ?, *, [, \\)."));
            hasFailure = true;
            onRowUpdated(rows);
        }

        // Output file name
        if (!string.IsNullOrWhiteSpace(outputFileName))
        {
            if (InvalidFileNameRegex().IsMatch(outputFileName))
            {
                rows.Add(ValidationRow.Fail("Invalid output file name",
                    @"The file name contains characters that are not allowed on Windows: < > : "" / \ | ? *"));
                hasFailure = true;
                onRowUpdated(rows);
            }
            else if (outputFileName.Length > 200)
            {
                rows.Add(ValidationRow.Warn("Output file name is very long",
                    "The name is over 200 characters and may be truncated by the operating system."));
                onRowUpdated(rows);
            }
        }

        // Stop here if format errors — server calls would be pointless
        if (hasFailure)
            return (rows, false);

        // ── 2. Server-side checks (async, one by one so UI updates live) ──────

        // JIRA tickets existence + status
        if (sourceType is "JiraOnly" or "Both" && uniqueTickets.Any())
        {
            bool isUserGuide = docType.Equals("UserGuide", StringComparison.OrdinalIgnoreCase);

            foreach (var id in uniqueTickets)
            {
                var row = ValidationRow.Checking(id);
                rows.Add(row);
                onRowUpdated(rows);

                try
                {
                    var response = await _http.PostAsJsonAsync(
                        "api/jira/validate-tickets",
                        new { ticketIds = new[] { id } });

                    var rawBody = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        row.Status  = ValidationStatus.Fail;
                        row.Message = TryExtractMessage(rawBody)
                            ?? $"The server returned an error ({(int)response.StatusCode}) while checking this ticket.";
                        hasFailure  = true;
                        onRowUpdated(rows);
                        continue;
                    }

                    List<TicketValidationDto>? items = null;
                    try
                    {
                        items = JsonSerializer.Deserialize<List<TicketValidationDto>>(
                            rawBody,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch
                    {
                        row.Status  = ValidationStatus.Fail;
                        row.Message = "The server sent an unexpected response. Please try again.";
                        hasFailure  = true;
                        onRowUpdated(rows);
                        continue;
                    }

                    var item = items?.FirstOrDefault();

                    if (item is null)
                    {
                        row.Status  = ValidationStatus.Fail;
                        row.Message = "No response received from the server. Please try again.";
                        hasFailure  = true;
                    }
                    else if (!item.Exists)
                    {
                        row.Status  = ValidationStatus.Fail;
                        // item.Message is already set by the API to a friendly string
                        row.Message = item.Message;
                        hasFailure  = true;
                    }
                    else if (isUserGuide)
                    {
                        // User Guide: ALL tickets must be Done or Complete
                        bool isDone = UserGuideDoneStatuses.Any(s =>
                            item.Status?.Contains(s, StringComparison.OrdinalIgnoreCase) == true);

                        if (isDone)
                        {
                            row.Status  = ValidationStatus.Pass;
                            row.Message = $"{item.Summary}  [{item.Status}] — ready for User Guide";
                        }
                        else
                        {
                            row.Status  = ValidationStatus.Fail;
                            row.Message =
                                $"This ticket has status '{item.Status ?? "unknown"}', but User Guide " +
                                "can only be generated for tickets with status 'Done' or 'Complete'. " +
                                "Please finish the work on this ticket first.";
                            hasFailure = true;
                        }
                    }
                    else
                    {
                        // Other doc types: warn if already closed, pass otherwise
                        bool isClosed = ClosedStatuses.Any(s =>
                            item.Status?.Contains(s, StringComparison.OrdinalIgnoreCase) == true);

                        row.Status  = isClosed ? ValidationStatus.Warn : ValidationStatus.Pass;
                        row.Message = isClosed
                            ? $"{item.Summary}  [{item.Status}] — this ticket is already closed"
                            : item.Message;
                    }
                }
                catch (HttpRequestException httpEx)
                {
                    row.Status  = ValidationStatus.Fail;
                    row.Message = $"Could not reach the server to validate this ticket. " +
                                  $"Check your network connection. ({httpEx.Message})";
                    hasFailure  = true;
                }
                catch (Exception ex)
                {
                    row.Status  = ValidationStatus.Fail;
                    row.Message = $"An unexpected error occurred while checking this ticket: {ex.Message}";
                    hasFailure  = true;
                }

                onRowUpdated(rows);
            }
        }

        // Git repo existence + branch check
        if (sourceType is "GitOnly" or "Both" && !string.IsNullOrWhiteSpace(repoUrl))
        {
            var repoLabel = repoUrl.TrimEnd('/').Split('/').Last().Replace(".git", "");
            var row = ValidationRow.Checking(repoLabel);
            rows.Add(row);
            onRowUpdated(rows);

            try
            {
                var query = $"api/git/validate-remote?url={Uri.EscapeDataString(repoUrl)}";
                if (!string.IsNullOrWhiteSpace(branch))
                    query += $"&branch={Uri.EscapeDataString(branch)}";

                var response = await _http.GetAsync(query);
                var rawBody  = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    row.Status  = ValidationStatus.Fail;
                    row.Message = TryExtractMessage(rawBody)
                        ?? $"The server returned an error ({(int)response.StatusCode}) while checking the repository.";
                    hasFailure  = true;
                    onRowUpdated(rows);
                    return (rows, false);
                }

                RepoValidationDto? dto = null;
                try
                {
                    dto = JsonSerializer.Deserialize<RepoValidationDto>(
                        rawBody,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    row.Status  = ValidationStatus.Fail;
                    row.Message = "The server sent an unexpected response while checking the repository. Please try again.";
                    hasFailure  = true;
                    onRowUpdated(rows);
                    return (rows, false);
                }

                if (dto is null || !dto.Accessible)
                {
                    row.Status  = ValidationStatus.Fail;
                    row.Message = dto?.Message ?? "The repository could not be accessed. Check the URL and that you have read permission.";
                    hasFailure  = true;
                }
                else if (!dto.BranchExists)
                {
                    row.Status  = ValidationStatus.Fail;
                    row.Message = dto.Message;
                    hasFailure  = true;
                }
                else
                {
                    row.Status  = ValidationStatus.Pass;
                    row.Message = dto.Message;
                }
            }
            catch (HttpRequestException httpEx)
            {
                row.Status  = ValidationStatus.Fail;
                row.Message = $"Could not reach the server to check the repository. " +
                              $"Check your network connection. ({httpEx.Message})";
                hasFailure  = true;
            }
            catch (Exception ex)
            {
                row.Status  = ValidationStatus.Fail;
                row.Message = $"An unexpected error occurred while checking the repository: {ex.Message}";
                hasFailure  = true;
            }

            onRowUpdated(rows);
        }

        return (rows, !hasFailure);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Tries to extract a human-readable message from a JSON error body.
    /// Checks "message", "detail", "title", and "error" properties in that order.
    /// Returns null if the body is not JSON or contains none of these fields.
    /// </summary>
    private static string? TryExtractMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            foreach (var prop in new[] { "message", "detail", "title", "error" })
            {
                if (root.TryGetProperty(prop, out var el) &&
                    el.ValueKind == JsonValueKind.String)
                {
                    var val = el.GetString();
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
            }
        }
        catch { /* not JSON or unexpected shape — return null */ }
        return null;
    }
}
