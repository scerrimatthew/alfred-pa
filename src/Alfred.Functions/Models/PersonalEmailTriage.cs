namespace Alfred.Functions.Models;

public class PersonalEmailTriage
{
    public required bool RequiresAttention { get; set; }
    public string Category { get; set; } = "other";
    public required string Summary { get; set; }
    public string TelegramMessage { get; set; } = string.Empty;
    public List<CalendarEventInfo> CalendarEvents { get; set; } = [];
    public bool Suppressed { get; set; }
    public string? MatchedRule { get; set; }
    public string? MatchedAttentionRule { get; set; }
    // One-sentence warning when a payment request looks like impersonation/fraud; null when clean
    public string? FraudWarning { get; set; }
    // True when a real person wrote to Matthew and expects a response from him
    public bool NeedsReply { get; set; }
    // Story leads extracted when the email is an AI-industry newsletter; empty otherwise
    public List<NewsLead> NewsLeads { get; set; } = [];
}

// One story mentioned in an AI newsletter, harvested during triage as candidate
// material for the evening AI-news digest
public class NewsLead
{
    public string Headline { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? Note { get; set; }
}
