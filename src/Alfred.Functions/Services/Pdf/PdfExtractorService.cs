using System.Text;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace Alfred.Functions.Services.Pdf;

public class PdfExtractorService : IPdfExtractorService
{
    private readonly ILogger<PdfExtractorService> _logger;

    public PdfExtractorService(ILogger<PdfExtractorService> logger)
    {
        _logger = logger;
    }

    public string ExtractText(byte[] pdfBytes, string fileName)
    {
        try
        {
            var text = new StringBuilder();

            using var document = PdfDocument.Open(pdfBytes);
            foreach (var page in document.GetPages())
            {
                text.AppendLine(page.Text);
            }

            var result = text.ToString().Trim();
            _logger.LogInformation("Extracted {Length} characters from {FileName}", result.Length, fileName);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract text from PDF: {FileName}", fileName);
            return string.Empty;
        }
    }
}
