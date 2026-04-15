using System.Net.Http.Json;
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

    public ValidationService(HttpClient http) => _http = http;

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Run all client-side and server-side validations.
    /// Calls <paramref name="onRowUpdated"/> after each row changes so the UI can re-render.
    /// Returns false if any hard failure was found (warnings are allowed through).
    /// </summary>
    public async Task<(List<ValidationRow> Rows, bool CanProceed)> ValidateAsync(
        string sourceType,
        List<string> ticketIds,
        string? repoUrl,
        string? branch,
        string? outputFileName,
        Action onRowUpdated)
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
                $"Removed duplicates: {string.Join(", ", dupes)}"));
            onRowUpdated();
        }

        var uniqueTickets = ticketIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // JIRA ticket format
        if (sourceType is "JiraOnly" or "Both")
        {
            foreach (var id in uniqueTickets)
            {
                if (!TicketFormatRegex().IsMatch(id.ToUpper()))
                {
                    rows.Add(ValidationRow.Fail($"Format: {id}",
                        $"'{id}' is not a valid JIRA ticket ID. Expected format: PROJECT-123"));
                    hasFailure = true;
                    onRowUpdated();
                }
            }
        }

        // Git URL format
        if (sourceType is "GitOnly" or "Both" && !string.IsNullOrWhiteSpace(repoUrl))
        {
            if (!GitUrlRegex().IsMatch(repoUrl))
            {
                rows.Add(ValidationRow.Fail("Git URL format",
                    "URL must start with https://, http://, git@, or git://"));
                hasFailure = true;
                onRowUpdated();
            }
        }

        // Branch name format
        if (!string.IsNullOrWhiteSpace(branch) && !BranchNameRegex().IsMatch(branch))
        {
            rows.Add(ValidationRow.Fail("Branch name format",
                $"'{branch}' contains invalid characters for a git branch name."));
            hasFailure = true;
            onRowUpdated();
        }

        // Output file name
        if (!string.IsNullOrWhiteSpace(outputFileName))
        {
            if (InvalidFileNameRegex().IsMatch(outputFileName))
            {
                rows.Add(ValidationRow.Fail("Output file name",
                    @"File name contains invalid characters: < > : "" / \ | ? *"));
                hasFailure = true;
                onRowUpdated();
            }
            else if (outputFileName.Length > 200)
            {
                rows.Add(ValidationRow.Warn("Output file name",
                    "File name is very long (> 200 chars). It will be truncated if needed."));
                onRowUpdated();
            }
        }

        // Stop here if format errors — server calls would be pointless
        if (hasFailure)
            return (rows, false);

        // ── 2. Server-side checks (async, one by one so UI updates live) ──────

        // JIRA tickets existence
        if (sourceType is "JiraOnly" or "Both" && uniqueTickets.Any())
        {
            foreach (var id in uniqueTickets)
            {
                var row = ValidationRow.Checking(id);
                rows.Add(row);
                onRowUpdated();

                try
                {
                    var response = await _http.PostAsJsonAsync(
                        "api/jira/validate-tickets",
                        new { ticketIds = new[] { id } });

                    // Read raw body first so we can show it on any error
                    var rawBody = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        row.Status  = ValidationStatus.Fail;
                        row.Message = $"API error {(int)response.StatusCode}: {rawBody}";
                        hasFailure  = true;
                        onRowUpdated();
                        continue;
                    }

                    List<TicketValidationDto>? items = null;
                    try
                    {
                        items = System.Text.Json.JsonSerializer.Deserialize<List<TicketValidationDto>>(
                            rawBody,
                            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch (Exception jsonEx)
                    {
                        row.Status  = ValidationStatus.Fail;
                        row.Message = $"Unexpected response format: {jsonEx.Message}. Body: {rawBody[..Math.Min(200, rawBody.Length)]}";
                        hasFailure  = true;
                        onRowUpdated();
                        continue;
                    }

                    var item = items?.FirstOrDefault();

                    if (item is null)
                    {
                        row.Status  = ValidationStatus.Fail;
                        row.Message = "No response from server.";
                        hasFailure  = true;
                    }
                    else if (!item.Exists)
                    {
                        row.Status  = ValidationStatus.Fail;
                        row.Message = item.Message;
                        hasFailure  = true;
                    }
                    else
                    {
                        var closedStatuses = new[] { "done", "closed", "resolved", "cancelled", "rejected" };
                        bool isClosed = closedStatuses.Any(s =>
                            item.Status.Contains(s, StringComparison.OrdinalIgnoreCase));

                        row.Status  = isClosed ? ValidationStatus.Warn : ValidationStatus.Pass;
                        row.Message = isClosed
                            ? $"{item.Summary}  [{item.Status}] — ticket is already closed"
                            : item.Message;
                    }
                }
                catch (HttpRequestException httpEx)
                {
                    row.Status  = ValidationStatus.Fail;
                    row.Message = $"Network error reaching API: {httpEx.Message}";
                    hasFailure  = true;
                }
                catch (Exception ex)
                {
                    row.Status  = ValidationStatus.Fail;
                    row.Message = $"Unexpected error: {ex.Message}";
                    hasFailure  = true;
                }

                onRowUpdated();
            }
        }

        // Git repo existence + branch check
        if (sourceType is "GitOnly" or "Both" && !string.IsNullOrWhiteSpace(repoUrl))
        {
            var repoLabel = repoUrl.TrimEnd('/').Split('/').Last().Replace(".git", "");
            var row = ValidationRow.Checking(repoLabel);
            rows.Add(row);
            onRowUpdated();

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
                    row.Message = $"API error {(int)response.StatusCode}: {rawBody}";
                    hasFailure  = true;
                    onRowUpdated();
                    return (rows, false);
                }

                RepoValidationDto? dto = null;
                try
                {
                    dto = System.Text.Json.JsonSerializer.Deserialize<RepoValidationDto>(
                        rawBody,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (Exception jsonEx)
                {
                    row.Status  = ValidationStatus.Fail;
                    row.Message = $"Unexpected response format: {jsonEx.Message}";
                    hasFailure  = true;
                    onRowUpdated();
                    return (rows, false);
                }

                if (dto is null || !dto.Accessible)
                {
                    row.Status  = ValidationStatus.Fail;
                    row.Message = dto?.Message ?? "Repository is not accessible.";
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
                row.Message = $"Network error reaching API: {httpEx.Message}";
                hasFailure  = true;
            }
            catch (Exception ex)
            {
                row.Status  = ValidationStatus.Fail;
                row.Message = $"Unexpected error: {ex.Message}";
                hasFailure  = true;
            }

            onRowUpdated();
        }

        return (rows, !hasFailure);
    }
}
