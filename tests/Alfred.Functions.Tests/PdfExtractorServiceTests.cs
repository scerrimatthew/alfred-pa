using System.Text;
using Alfred.Functions.Services.Pdf;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Alfred.Functions.Tests;

public class PdfExtractorServiceTests
{
    private readonly PdfExtractorService _extractor = new(NullLogger<PdfExtractorService>.Instance);

    [Fact]
    public void GarbageBytes_ReturnEmptyInsteadOfThrowing()
    {
        // Extraction is best-effort: a corrupt attachment must never sink the whole email
        var result = _extractor.ExtractText(Encoding.UTF8.GetBytes("this is not a pdf"), "broken.pdf");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, _extractor.ExtractText([], "empty.pdf"));
    }
}
