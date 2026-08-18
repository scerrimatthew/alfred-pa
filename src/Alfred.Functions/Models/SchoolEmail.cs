namespace Alfred.Functions.Models;

public class SchoolEmail
{
    public required string MessageId { get; set; }
    public string ThreadId { get; set; } = string.Empty;
    public required string Subject { get; set; }
    public required string SenderName { get; set; }
    public required string SenderEmail { get; set; }
    public required DateTimeOffset ReceivedDate { get; set; }
    public required string Body { get; set; }
    public List<LinkedDocument> Documents { get; set; } = [];
    // False when the email was already read before Alfred saw it (user got there first) —
    // such emails are processed silently: state, labels, and digest, but no alert
    public bool WasUnread { get; set; } = true;
}

public class LinkedDocument
{
    public required string Title { get; set; }
    public required string Url { get; set; }
    public required LinkedDocumentSource Source { get; set; }
    public string? ExtractedText { get; set; }
}

public enum LinkedDocumentSource
{
    EmailAttachment,
    BodyLink
}
