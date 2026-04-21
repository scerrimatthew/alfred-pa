namespace Alfred.Functions.Models;

public class CalendarEventInfo
{
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required DateTime Date { get; set; }
    public TimeSpan? StartTime { get; set; }
    public TimeSpan? EndTime { get; set; }
    public bool IsAllDay => StartTime is null;
    public required CalendarEventAction Action { get; set; }
    public string? SubjectHash { get; set; }
}

public enum CalendarEventAction
{
    Create,
    Update,
    Delete
}
