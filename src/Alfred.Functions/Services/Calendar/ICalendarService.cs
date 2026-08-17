using Alfred.Functions.Models;

namespace Alfred.Functions.Services.Calendar;

public interface ICalendarService
{
    Task ProcessEventsAsync(List<CalendarEventInfo> events, string emailId);
    Task ProcessPersonalEventsAsync(List<CalendarEventInfo> events, string emailId);
    Task<List<Google.Apis.Calendar.v3.Data.Event>> GetUpcomingEventsAsync(int schoolDaysAhead);
    Task<List<Google.Apis.Calendar.v3.Data.Event>> GetUpcomingPersonalEventsAsync(int daysAhead);
    Task<string> UpdatePersonalEventAsync(string eventId, string? title, DateTime? date, TimeSpan? startTime, TimeSpan? endTime, string? description);
    Task<string> DeletePersonalEventAsync(string eventId);
}
