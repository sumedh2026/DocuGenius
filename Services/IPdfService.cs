using DocuGenious.Models;

namespace DocuGenious.Services;

public interface IPdfService
{
    Task<string> GeneratePdfAsync(AnalysisResult result, string outputFileName);
}
