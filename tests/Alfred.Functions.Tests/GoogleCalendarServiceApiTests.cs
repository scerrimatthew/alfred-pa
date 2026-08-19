using System.Text.Json;
using Alfred.Functions.Configuration;
using Alfred.Functions.Models;
using Alfred.Functions.Services.Calendar;
using Alfred.Functions.Services.State;
using Alfred.Functions.Tests.Support;
using Google.Apis.Calendar.v3;
using Google.Apis.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static Alfred.Functions.Tests.Support.TestData;

namespace Alfred.Functions.Tests;

// Drives GoogleCalendarService over a fake HTTP layer: the real Google SDK builds the
// requests and parses canned Calendar JSON. Pins the dedup pipeline (state mapping ->
// ±1-day similarity scan -> insert) and the alfred=true ownership guard.
public class GoogleCalendarServiceApiTests
{
    private const string SchoolCalendarId = "school-cal@group.calendar.google.com";

    private readonly FakeHttpHandler _http = new();
    private readonly IStateService _state = Substitute.For<IStateService>();
    private readonly GoogleCalendarService _service;

    public GoogleCalendarServiceApiTests()
    {
        _state.GetCalendarEventMappingAsync(Arg.Any<string>()).Returns((CalendarEventEntity?)null);

        _service = new GoogleCalendarService(
            Options(o => o.SharedCalendarId = SchoolCalendarId),
            Microsoft.Extensions.Options.Options.Create(new GoogleOptions()),
            _state,
            NullLogger<GoogleCalendarService>.Instance)
        {
            CalendarServiceOverride = new CalendarService(new BaseClientService.Initializer
            {
                HttpClientFactory = new FakeGoogleHttpClientFactory(_http),
                ApplicationName = "AlfredTests",
                GZipEnabled = false // keep recorded request bodies readable
            })
        };
    }

    private static CalendarEventInfo EventInfo(
        string title = "Outing: Zoo Year 1",
        string date = "2100-05-10",
        CalendarEventAction action = CalendarEventAction.Create,
        TimeSpan? startTime = null) =>
        new()
        {
            Title = title,
            Description = "Bring a hat",
            Date = DateTime.Parse(date),
            StartTime = startTime,
            Action = action
        };

    private static string EventsListJson(params string[] summaries) =>
        JsonSerializer.Serialize(new { items = summaries.Select(s => new { summary = s }).ToArray() });

    private RecordedRequest InsertRequest() =>
        _http.Requests.Single(r => r.Method == HttpMethod.Post && r.Path.Contains("/events", StringComparison.Ordinal));

    // ---- Create + dedup pipeline ----

    [Fact]
    public async Task NewEvent_ScansTheSurroundingDaysThenInsertsAndRecordsTheMapping()
    {
        _http.Route("POST /calendars/", """{"id":"gev1"}""");
        _http.Route("GET /calendars/", EventsListJson()); // empty scan window

        await _service.ProcessEventsAsync([EventInfo()], "email-1");

        // Dedup scan covers the target date ±1 day (catches off-by-one date errors).
        // The SDK may serialize the instants in any offset — compare wall dates.
        var scan = _http.Requests.Single(r => r.Method == HttpMethod.Get);
        var scanParams = System.Web.HttpUtility.ParseQueryString(scan.Query);
        Assert.Equal(new DateTime(2100, 5, 9),
            TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(scanParams["timeMin"]!), TimeZoneInfo.Local).Date);
        Assert.Equal(new DateTime(2100, 5, 12),
            TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(scanParams["timeMax"]!), TimeZoneInfo.Local).Date);
        Assert.Contains(Uri.EscapeDataString(SchoolCalendarId), scan.Path);

        var inserted = JsonDocument.Parse(InsertRequest().Body!).RootElement;
        Assert.Equal("Outing: Zoo Year 1", inserted.GetProperty("summary").GetString());
        Assert.Equal("Bring a hat", inserted.GetProperty("description").GetString());
        Assert.Equal("2100-05-10", inserted.GetProperty("start").GetProperty("date").GetString());
        Assert.Equal("2100-05-11", inserted.GetProperty("end").GetProperty("date").GetString());
        Assert.False(inserted.TryGetProperty("extendedProperties", out _), "school events carry no alfred tag");

        var reminder = inserted.GetProperty("reminders").GetProperty("overrides")[0];
        Assert.Equal("popup", reminder.GetProperty("method").GetString());
        Assert.Equal(360, reminder.GetProperty("minutes").GetInt32());

        var expectedHash = GoogleCalendarService.ComputeHash("Outing: Zoo Year 1_2100-05-10");
        await _state.Received(1).SaveCalendarEventMappingAsync(
            expectedHash, "gev1", "email-1", "Outing: Zoo Year 1",
            new DateTimeOffset(new DateTime(2100, 5, 10)));
    }

    [Fact]
    public async Task SimilarEventAlreadyOnTheCalendar_SkipsTheInsert()
    {
        // "Deadline: Sports Day" vs existing "Sports Day" — same event, different prefix
        _http.Route("GET /calendars/", EventsListJson("Sports Day"));

        await _service.ProcessEventsAsync([EventInfo(title: "Deadline: Sports Day")], "email-1");

        Assert.DoesNotContain(_http.Requests, r => r.Method == HttpMethod.Post);
        await _state.DidNotReceiveWithAnyArgs().SaveCalendarEventMappingAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task UnrelatedEventInTheSameWindow_DoesNotBlockTheInsert()
    {
        // The regression the two-thirds similarity rule fixed: two different bills, same week
        _http.Route("POST /calendars/", """{"id":"gev1"}""");
        _http.Route("GET /calendars/", EventsListJson("Deadline: Pay Melita invoice €30.00"));

        await _service.ProcessEventsAsync(
            [EventInfo(title: "Deadline: Pay GO invoice €45.20")], "email-1");

        Assert.Single(_http.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task EventAlreadyInState_SkipsEvenTheScan()
    {
        _state.GetCalendarEventMappingAsync(Arg.Any<string>())
            .Returns(new CalendarEventEntity { GoogleEventId = "existing" });

        await _service.ProcessEventsAsync([EventInfo()], "email-1");

        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task PersonalEvents_AreTaggedAlfredAndGoToThePersonalCalendar()
    {
        _http.Route("POST /calendars/primary/events", """{"id":"gev1"}""");
        _http.Route("GET /calendars/primary/events", EventsListJson());

        await _service.ProcessPersonalEventsAsync([EventInfo(title: "Deadline: Pay GO invoice")], "email-1");

        var inserted = JsonDocument.Parse(InsertRequest().Body!).RootElement;
        Assert.Equal("true", inserted
            .GetProperty("extendedProperties").GetProperty("private").GetProperty("alfred").GetString());
    }

    [Fact]
    public async Task TimedEvent_IsInsertedInMaltaTime()
    {
        _http.Route("POST /calendars/", """{"id":"gev1"}""");
        _http.Route("GET /calendars/", EventsListJson());

        await _service.ProcessEventsAsync(
            [EventInfo(title: "Meeting: Online Safety", date: "2100-07-10", startTime: new TimeSpan(18, 30, 0))], "email-1");

        var inserted = JsonDocument.Parse(InsertRequest().Body!).RootElement;
        Assert.Equal("Europe/Malta", inserted.GetProperty("start").GetProperty("timeZone").GetString());
        Assert.StartsWith("2100-07-10T18:30:00", inserted.GetProperty("start").GetProperty("dateTime").GetString());
        Assert.StartsWith("2100-07-10T19:30:00", inserted.GetProperty("end").GetProperty("dateTime").GetString());
    }

    // ---- Update / delete via the state mapping ----

    [Fact]
    public async Task UpdateAction_WithAKnownMapping_UpdatesTheExistingGoogleEvent()
    {
        _state.GetCalendarEventMappingAsync(Arg.Any<string>())
            .Returns(new CalendarEventEntity { GoogleEventId = "gev9" });
        _http.Route("PUT /events/gev9", """{"id":"gev9"}""");

        await _service.ProcessEventsAsync(
            [EventInfo(title: "Outing: Zoo Year 1", action: CalendarEventAction.Update)], "email-2");

        var put = _http.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.EndsWith("/events/gev9", put.Path);
        Assert.Equal("Outing: Zoo Year 1", JsonDocument.Parse(put.Body!).RootElement.GetProperty("summary").GetString());
        await _state.Received(1).SaveCalendarEventMappingAsync(
            Arg.Any<string>(), "gev9", "email-2", "Outing: Zoo Year 1", Arg.Any<DateTimeOffset>());
    }

    [Fact]
    public async Task UpdateAction_WithoutAMapping_FallsBackToCreating()
    {
        _http.Route("POST /calendars/", """{"id":"gev1"}""");
        _http.Route("GET /calendars/", EventsListJson());

        await _service.ProcessEventsAsync([EventInfo(action: CalendarEventAction.Update)], "email-1");

        Assert.Single(_http.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task DeleteAction_RemovesTheEventAndTheMapping()
    {
        _state.GetCalendarEventMappingAsync(Arg.Any<string>())
            .Returns(new CalendarEventEntity { GoogleEventId = "gev9" });
        _http.RouteResponder("DELETE /events/gev9", _ => new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));

        await _service.ProcessEventsAsync([EventInfo(action: CalendarEventAction.Delete)], "email-1");

        var delete = _http.Requests.Single(r => r.Method == HttpMethod.Delete);
        Assert.EndsWith("/events/gev9", delete.Path);
        await _state.Received(1).DeleteCalendarEventMappingAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task DeleteAction_WithoutAMapping_DoesNothing()
    {
        await _service.ProcessEventsAsync([EventInfo(action: CalendarEventAction.Delete)], "email-1");

        Assert.Empty(_http.Requests);
        await _state.DidNotReceiveWithAnyArgs().DeleteCalendarEventMappingAsync(default!);
    }

    // ---- Upcoming-event queries ----

    [Fact]
    public async Task GetUpcomingEvents_QueriesTheSharedCalendarOrderedByStart()
    {
        _http.Route("GET /calendars/", EventsListJson("Sports Day", "Field Day"));

        var events = await _service.GetUpcomingEventsAsync(3);

        Assert.Equal(2, events.Count);
        Assert.Equal("Sports Day", events[0].Summary);

        var query = System.Web.HttpUtility.ParseQueryString(_http.Requests.Single().Query);
        Assert.Equal("starttime", query["orderBy"], ignoreCase: true);
        Assert.Equal("True", query["singleEvents"], ignoreCase: true);
        Assert.Equal("50", query["maxResults"]);
        Assert.NotNull(query["timeMin"]);
        Assert.NotNull(query["timeMax"]);
    }

    [Fact]
    public async Task GetUpcomingPersonalEvents_FiltersToAlfredCreatedOnes()
    {
        _http.Route("GET /calendars/primary/events", EventsListJson("Deadline: Pay GO invoice"));

        var events = await _service.GetUpcomingPersonalEventsAsync(7);

        Assert.Single(events);
        var query = System.Web.HttpUtility.ParseQueryString(_http.Requests.Single().Query);
        Assert.Equal("alfred=true", query["privateExtendedProperty"]);
    }

    // ---- Chat-created personal events ----

    [Fact]
    public async Task CreatePersonalEvent_CoercesABadEndTimeAndRecordsAChatMapping()
    {
        _http.Route("POST /calendars/primary/events", """{"id":"gev-chat"}""");

        var id = await _service.CreatePersonalEventAsync(
            "Pay Aeris invoice", new DateTime(2100, 7, 10), new TimeSpan(10, 0, 0),
            endTime: new TimeSpan(9, 0, 0), // before the start — Google would reject it
            description: "€120");

        Assert.Equal("gev-chat", id);
        var inserted = JsonDocument.Parse(InsertRequest().Body!).RootElement;
        Assert.StartsWith("2100-07-10T10:00:00", inserted.GetProperty("start").GetProperty("dateTime").GetString());
        Assert.StartsWith("2100-07-10T11:00:00", inserted.GetProperty("end").GetProperty("dateTime").GetString());

        await _state.Received(1).SaveCalendarEventMappingAsync(
            GoogleCalendarService.ComputeHash("Pay Aeris invoice_2100-07-10"),
            "gev-chat", "chat", "Pay Aeris invoice", Arg.Any<DateTimeOffset>());
    }

    // ---- The alfred=true ownership guard ----

    private void RouteAlfredEvent(string eventId, bool alfredTagged, string summary = "Deadline: Pay GO invoice")
    {
        var eventJson = alfredTagged
            ? JsonSerializer.Serialize(new
            {
                id = eventId,
                summary,
                extendedProperties = new { @private = new { alfred = "true" } },
                start = new { date = "2100-05-10" },
                end = new { date = "2100-05-11" }
            })
            : JsonSerializer.Serialize(new
            {
                id = eventId,
                summary,
                start = new { date = "2100-05-10" },
                end = new { date = "2100-05-11" }
            });
        _http.Route($"GET /events/{eventId}", eventJson);
    }

    [Fact]
    public async Task UpdatePersonalEvent_RefusesToTouchEventsAlfredDidNotCreate()
    {
        RouteAlfredEvent("ev1", alfredTagged: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UpdatePersonalEventAsync("ev1", "New title", null, null, null, null));

        Assert.DoesNotContain(_http.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task DeletePersonalEvent_RefusesToTouchEventsAlfredDidNotCreate()
    {
        RouteAlfredEvent("ev1", alfredTagged: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeletePersonalEventAsync("ev1"));

        Assert.DoesNotContain(_http.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task UpdatePersonalEvent_MovingAnAllDayEventKeepsItAllDay()
    {
        RouteAlfredEvent("ev1", alfredTagged: true, summary: "GO bill");
        _http.Route("PUT /events/ev1", """{"id":"ev1"}""");

        var title = await _service.UpdatePersonalEventAsync(
            "ev1", null, new DateTime(2100, 5, 12), null, null, null);

        Assert.Equal("GO bill", title);
        var put = JsonDocument.Parse(_http.Requests.Single(r => r.Method == HttpMethod.Put).Body!).RootElement;
        Assert.Equal("2100-05-12", put.GetProperty("start").GetProperty("date").GetString());
        Assert.Equal("2100-05-13", put.GetProperty("end").GetProperty("date").GetString());
    }

    [Fact]
    public async Task UpdatePersonalEvent_AddingAStartTime_ConvertsToATimedMaltaEvent()
    {
        RouteAlfredEvent("ev1", alfredTagged: true, summary: "Dentist");
        _http.Route("PUT /events/ev1", """{"id":"ev1"}""");

        await _service.UpdatePersonalEventAsync(
            "ev1", null, new DateTime(2100, 7, 12), new TimeSpan(9, 0, 0), null, null);

        var put = JsonDocument.Parse(_http.Requests.Single(r => r.Method == HttpMethod.Put).Body!).RootElement;
        Assert.StartsWith("2100-07-12T09:00:00", put.GetProperty("start").GetProperty("dateTime").GetString());
        Assert.StartsWith("2100-07-12T10:00:00", put.GetProperty("end").GetProperty("dateTime").GetString());
        Assert.Equal("Europe/Malta", put.GetProperty("start").GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task DeletePersonalEvent_DeletesAlfredEventsAndReturnsTheTitle()
    {
        RouteAlfredEvent("ev1", alfredTagged: true, summary: "GO bill reminder");
        _http.RouteResponder("DELETE /events/ev1", _ => new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));

        var title = await _service.DeletePersonalEventAsync("ev1");

        Assert.Equal("GO bill reminder", title);
        Assert.Contains(_http.Requests, r => r.Method == HttpMethod.Delete && r.Path.EndsWith("/events/ev1", StringComparison.Ordinal));
    }
}
