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
}
