using Alfred.Functions.Models;

namespace Alfred.Functions.Services.Calendar;

public interface ICalendarService
{
    Task ProcessEventsAsync(List<CalendarEventInfo> events, string emailId);
    Task<List<Google.Apis.Calendar.v3.Data.Event>> GetUpcomingEventsAsync(int schoolDaysAhead);
}
