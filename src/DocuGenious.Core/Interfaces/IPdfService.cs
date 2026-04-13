using DocuGenious.Core.Models;

namespace DocuGenious.Core.Interfaces;

public interface IPdfService
{
    Task<string> GeneratePdfAsync(AnalysisResult result, string outputFileName);
}
