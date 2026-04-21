using System.Security.Cryptography;
using System.Text;
using Alfred.Functions.Configuration;
using Alfred.Functions.Models;
using Alfred.Functions.Services.State;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alfred.Functions.Services.Calendar;

public class GoogleCalendarService : ICalendarService
{
    private readonly AlfredOptions _alfredOptions;
    private readonly GoogleOptions _googleOptions;
    private readonly IStateService _stateService;
    private readonly ILogger<GoogleCalendarService> _logger;

    public GoogleCalendarService(
        IOptions<AlfredOptions> alfredOptions,
        IOptions<GoogleOptions> googleOptions,
        IStateService stateService,
        ILogger<GoogleCalendarService> logger)
    {
        _alfredOptions = alfredOptions.Value;
        _googleOptions = googleOptions.Value;
        _stateService = stateService;
        _logger = logger;
    }

    public async Task ProcessEventsAsync(List<CalendarEventInfo> events, string emailId)
    {
        if (events.Count == 0) return;

        var calendarService = CreateCalendarService();

        foreach (var eventInfo in events)
        {
            var subjectHash = ComputeHash($"{eventInfo.Title}_{eventInfo.Date:yyyy-MM-dd}");
            eventInfo.SubjectHash = subjectHash;

            switch (eventInfo.Action)
            {
                case CalendarEventAction.Create:
                    await CreateEventAsync(calendarService, eventInfo, emailId);
                    break;
                case CalendarEventAction.Update:
                    await UpdateEventAsync(calendarService, eventInfo, emailId);
                    break;
                case CalendarEventAction.Delete:
                    await DeleteEventAsync(calendarService, eventInfo);
                    break;
            }
        }
    }

    public async Task<List<Event>> GetUpcomingEventsAsync(int schoolDaysAhead)
    {
        var calendarService = CreateCalendarService();
        var now = DateTime.Now;
        var endDate = GetSchoolDaysFromNow(now, schoolDaysAhead);

        var request = calendarService.Events.List(_alfredOptions.SharedCalendarId);
        request.TimeMinDateTimeOffset = new DateTimeOffset(now);
        request.TimeMaxDateTimeOffset = new DateTimeOffset(endDate.AddDays(1));
        request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;
        request.SingleEvents = true;
        request.MaxResults = 50;

        var response = await request.ExecuteAsync();
        return response.Items?.ToList() ?? [];
    }

    private async Task CreateEventAsync(CalendarService calendarService, CalendarEventInfo eventInfo, string emailId)
    {
        var existing = await _stateService.GetCalendarEventMappingAsync(eventInfo.SubjectHash!);
        if (existing is not null)
        {
            _logger.LogInformation("Calendar event already exists for {Title}, skipping create", eventInfo.Title);
            return;
        }

        var calendarEvent = BuildCalendarEvent(eventInfo);
        var created = await calendarService.Events
            .Insert(calendarEvent, _alfredOptions.SharedCalendarId)
            .ExecuteAsync();

        await _stateService.SaveCalendarEventMappingAsync(
            eventInfo.SubjectHash!, created.Id, emailId, eventInfo.Title,
            new DateTimeOffset(eventInfo.Date));

        _logger.LogInformation("Created calendar event: {Title} on {Date}", eventInfo.Title, eventInfo.Date);
    }

    private async Task UpdateEventAsync(CalendarService calendarService, CalendarEventInfo eventInfo, string emailId)
    {
        var existing = await _stateService.GetCalendarEventMappingAsync(eventInfo.SubjectHash!);
        if (existing is null)
        {
            _logger.LogInformation("No existing event found for update, creating instead: {Title}", eventInfo.Title);
            await CreateEventAsync(calendarService, eventInfo, emailId);
            return;
        }

        var calendarEvent = BuildCalendarEvent(eventInfo);
        await calendarService.Events
            .Update(calendarEvent, _alfredOptions.SharedCalendarId, existing.GoogleEventId)
            .ExecuteAsync();

        await _stateService.SaveCalendarEventMappingAsync(
            eventInfo.SubjectHash!, existing.GoogleEventId, emailId, eventInfo.Title,
            new DateTimeOffset(eventInfo.Date));

        _logger.LogInformation("Updated calendar event: {Title}", eventInfo.Title);
    }

    private async Task DeleteEventAsync(CalendarService calendarService, CalendarEventInfo eventInfo)
    {
        var existing = await _stateService.GetCalendarEventMappingAsync(eventInfo.SubjectHash!);
        if (existing is null)
        {
            _logger.LogInformation("No existing event found to delete: {Title}", eventInfo.Title);
            return;
        }

        await calendarService.Events
            .Delete(_alfredOptions.SharedCalendarId, existing.GoogleEventId)
            .ExecuteAsync();

        await _stateService.DeleteCalendarEventMappingAsync(eventInfo.SubjectHash!);

        _logger.LogInformation("Deleted calendar event: {Title}", eventInfo.Title);
    }

    private static Event BuildCalendarEvent(CalendarEventInfo eventInfo)
    {
        var calendarEvent = new Event
        {
            Summary = eventInfo.Title,
            Description = eventInfo.Description,
            Reminders = new Event.RemindersData
            {
                UseDefault = false,
                Overrides =
                [
                    new EventReminder { Method = "popup", Minutes = 24 * 60 } // 1 day before
                ]
            }
        };

        if (eventInfo.IsAllDay)
        {
            calendarEvent.Start = new EventDateTime { Date = eventInfo.Date.ToString("yyyy-MM-dd") };
            calendarEvent.End = new EventDateTime { Date = eventInfo.Date.AddDays(1).ToString("yyyy-MM-dd") };
        }
        else
        {
            var startDt = eventInfo.Date.Add(eventInfo.StartTime!.Value);
            var endDt = eventInfo.Date.Add(eventInfo.EndTime ?? eventInfo.StartTime.Value.Add(TimeSpan.FromHours(1)));

            calendarEvent.Start = new EventDateTime
            {
                DateTimeDateTimeOffset = new DateTimeOffset(startDt, TimeSpan.FromHours(2)), // CET/CEST
                TimeZone = "Europe/Malta"
            };
            calendarEvent.End = new EventDateTime
            {
                DateTimeDateTimeOffset = new DateTimeOffset(endDt, TimeSpan.FromHours(2)),
                TimeZone = "Europe/Malta"
            };
        }

        return calendarEvent;
    }

    private static DateTime GetSchoolDaysFromNow(DateTime start, int schoolDays)
    {
        var current = start.Date;
        var count = 0;
        while (count < schoolDays)
        {
            current = current.AddDays(1);
            if (current.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                count++;
        }
        return current;
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private CalendarService CreateCalendarService()
    {
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = _googleOptions.ClientId,
                ClientSecret = _googleOptions.ClientSecret
            },
            Scopes = [CalendarService.Scope.Calendar]
        });

        var credential = new UserCredential(flow, "user", new TokenResponse
        {
            RefreshToken = _googleOptions.RefreshToken
        });

        return new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Alfred"
        });
    }
}
