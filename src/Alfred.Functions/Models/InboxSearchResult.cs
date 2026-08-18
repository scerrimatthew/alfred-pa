namespace Alfred.Functions.Models;

// Lightweight hit from an on-demand inbox search — headers and Gmail's snippet only,
// so a search never pays for full body downloads or PDF extraction
public class InboxSearchResult
{
    public required string MessageId { get; set; }
    public string ThreadId { get; set; } = string.Empty;
    public required string Subject { get; set; }
    public required string SenderName { get; set; }
    public required string SenderEmail { get; set; }
    public required DateTimeOffset ReceivedDate { get; set; }
    public string Snippet { get; set; } = string.Empty;
}
