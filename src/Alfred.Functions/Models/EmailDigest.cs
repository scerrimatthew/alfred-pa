namespace Alfred.Functions.Models;

public class EmailDigest
{
    public required string TelegramMessage { get; set; }
    public List<CalendarEventInfo> CalendarEvents { get; set; } = [];
}
