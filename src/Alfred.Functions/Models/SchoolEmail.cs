namespace Alfred.Functions.Models;

public class SchoolEmail
{
    public required string MessageId { get; set; }
    public required string Subject { get; set; }
    public required string SenderName { get; set; }
    public required string SenderEmail { get; set; }
    public required DateTimeOffset ReceivedDate { get; set; }
    public required string Body { get; set; }
    public List<PdfAttachment> PdfAttachments { get; set; } = [];
}

public class PdfAttachment
{
    public required string FileName { get; set; }
    public required string ExtractedText { get; set; }
}
