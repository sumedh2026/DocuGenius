using System.Collections.Concurrent;

namespace DocuGenious.Api.Services;

/// <summary>
/// Singleton that tracks the progress text of in-flight document generation jobs.
/// The controller writes status strings here; the Blazor client polls
/// GET /api/documentation/status/{jobId} to read them.
/// Entries expire after 30 minutes to prevent unbounded memory growth.
/// </summary>
public sealed class JobStatusService
{
    private sealed record Entry(string Status, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _jobs = new();

    /// <summary>Updates (or creates) the status message for a job.</summary>
    public void Update(string jobId, string status)
    {
        _jobs[jobId] = new Entry(status, DateTime.UtcNow.AddMinutes(30));
        PurgeExpired();
    }

    /// <summary>Returns the current status, or null if the job is unknown / expired.</summary>
    public string? Get(string jobId) =>
        _jobs.TryGetValue(jobId, out var e) && e.ExpiresAt > DateTime.UtcNow
            ? e.Status
            : null;

    /// <summary>Removes a completed job immediately (call after success or error).</summary>
    public void Remove(string jobId) => _jobs.TryRemove(jobId, out _);

    private void PurgeExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _jobs.Keys)
            if (_jobs.TryGetValue(key, out var e) && e.ExpiresAt <= now)
                _jobs.TryRemove(key, out _);
    }
}
