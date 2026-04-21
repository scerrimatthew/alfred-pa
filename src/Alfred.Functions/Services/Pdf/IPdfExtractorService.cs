namespace Alfred.Functions.Services.Pdf;

public interface IPdfExtractorService
{
    string ExtractText(byte[] pdfBytes, string fileName);
}
