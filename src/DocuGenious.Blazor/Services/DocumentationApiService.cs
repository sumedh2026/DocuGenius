using System.Net.Http.Json;
using System.Text.Json;
using DocuGenious.Blazor.Models;

namespace DocuGenious.Blazor.Services;

public class DocumentationApiService
{
	private readonly HttpClient _http;

	public DocumentationApiService(HttpClient http)
	{
		_http = http;
	}

	public string ApiBaseUrl => _http.BaseAddress?.ToString() ?? "unknown";

	// ─── JIRA ────────────────────────────────────────────────────────────────────

	public async Task<ConnectionResult> ValidateJiraAsync()
	{
		try
		{
			var result = await _http.GetFromJsonAsync<ConnectionResult>("api/jira/validate");
			return result ?? new ConnectionResult { Connected = false, Message = "No response" };
		}
		catch (Exception ex)
		{
			return new ConnectionResult { Connected = false, Message = ex.Message };
		}
	}

	// ─── Groq ─────────────────────────────────────────────────────────────────────

	public async Task<ConnectionResult> ValidateGroqAsync()
	{
		try
		{
			var result = await _http.GetFromJsonAsync<ConnectionResult>("api/groq/validate");
			return result ?? new ConnectionResult { Connected = false, Message = "No response" };
		}
		catch (Exception ex)
		{
			return new ConnectionResult { Connected = false, Message = ex.Message };
		}
	}

	// ─── Documentation ────────────────────────────────────────────────────────────

	public async Task<GenerateResult> GenerateDocumentAsync(GenerateRequest request)
	{
		try
		{
			var response = await _http.PostAsJsonAsync("api/documentation/generate", request);

			if (response.IsSuccessStatusCode)
			{
				var result = await response.Content.ReadFromJsonAsync<GenerateResult>();
				if (result is null)
					return new GenerateResult { Success = false, Error = "Empty response from server" };
				result.Success = true;   // guarantee Success is set even if API omits it
				return result;
			}

			// Read raw body once — the stream can only be consumed once
			var errorBody = await response.Content.ReadAsStringAsync();

			// Try to surface a friendly message from the JSON payload
			// (API returns { "message": "..." } for most errors)
			var friendly = TryExtractMessage(errorBody);
			return new GenerateResult
			{
				Success = false,
				Error   = friendly ?? $"Something went wrong (HTTP {(int)response.StatusCode}). Please try again."
			};
		}
		catch (Exception ex)
		{
			return new GenerateResult { Success = false, Error = ex.Message };
		}
	}

    // ─── Job status polling ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current status string for a running job, or null when the job
    /// is not found (not started yet, or already finished and cleaned up).
    /// </summary>
    public async Task<string?> GetJobStatusAsync(string jobId)
    {
        try
        {
            var response = await _http.GetAsync($"api/documentation/status/{Uri.EscapeDataString(jobId)}");
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return null;

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<JobStatusResponse>();
                return body?.Status;
            }
        }
        catch { /* polling errors are non-fatal */ }
        return null;
    }

    private sealed record JobStatusResponse(string? Status);

    /// <summary>
    /// Tries to pull a human-readable message out of a JSON error body.
    /// Checks "message", "detail", "title", and "error" properties in order.
    /// Returns null when the body is not JSON or none of those properties exist.
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
        catch { /* not JSON — fall through */ }
        return null;
    }

    // ─── PDF download ─────────────────────────────────────────────────────────────

	public async Task<byte[]?> DownloadPdfAsync(string fileName)
	{
		try
		{
			return await _http.GetByteArrayAsync($"api/documentation/download/{Uri.EscapeDataString(fileName)}");
		}
		catch
		{
			return null;
		}
	}

}
