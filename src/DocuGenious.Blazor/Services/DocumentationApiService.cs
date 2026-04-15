using System.Net.Http.Json;
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

            var errorBody = await response.Content.ReadAsStringAsync();
            return new GenerateResult
            {
                Success = false,
                Error = $"Server error {(int)response.StatusCode}: {errorBody}"
            };
        }
        catch (Exception ex)
        {
            return new GenerateResult { Success = false, Error = ex.Message };
        }
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
