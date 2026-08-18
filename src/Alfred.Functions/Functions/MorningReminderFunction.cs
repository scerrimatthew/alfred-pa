using System.Text;
using Alfred.Functions.Configuration;
using Alfred.Functions.Services.Calendar;
using Alfred.Functions.Services.Notifications;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alfred.Functions.Functions;

// Escalates Alfred-created personal actions (payment deadlines, appointments) that are
// due today or tomorrow, so a calendar entry can't slip by unnoticed. Deterministic
// formatting — no Claude call. Quiet on days with nothing due.
public class MorningReminderFunction
{
    private readonly ICalendarService _calendarService;
    private readonly INotificationService _notificationService;
    private readonly AlfredOptions _options;
    private readonly ILogger<MorningReminderFunction> _logger;

    public MorningReminderFunction(
        ICalendarService calendarService,
        INotificationService notificationService,
        IOptions<AlfredOptions> options,
        ILogger<MorningReminderFunction> logger)
    {
        _calendarService = calendarService;
        _notificationService = notificationService;
        _options = options.Value;
        _logger = logger;
    }

    // 5 AM UTC = 7 AM Malta in summer (CEST), 6 AM in winter (CET)
    [Function("MorningReminder")]
    public async Task Run([TimerTrigger("0 0 5 * * *")] TimerInfo timerInfo)
    {
        if (string.IsNullOrWhiteSpace(_options.PersonalTelegramChatId))
            return;

        try
        {
            var maltaTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Malta");
            var todayMalta = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, maltaTz).Date;

            // Window covers today and tomorrow; filter precisely by Malta start date below
            var events = await _calendarService.GetUpcomingPersonalEventsAsync(2);

            var today = new List<string>();
            var tomorrow = new List<string>();

            foreach (var ev in events)
            {
                DateTime startDate;
                string line;
                if (ev.Start?.Date is not null)
                {
                    // All-day (deadlines are usually all-day)
                    startDate = DateTime.Parse(ev.Start.Date);
                    line = $"• {ev.Summary}";
                }
                else if (ev.Start?.DateTimeDateTimeOffset is not null)
                {
                    var startMalta = TimeZoneInfo.ConvertTime(ev.Start.DateTimeDateTimeOffset.Value, maltaTz);
                    startDate = startMalta.Date;
                    line = $"• {ev.Summary} — {startMalta:HH:mm}";
                }
                else
                {
                    continue;
                }

                if (startDate == todayMalta)
                    today.Add(line);
                else if (startDate == todayMalta.AddDays(1))
                    tomorrow.Add(line);
            }

            if (today.Count == 0 && tomorrow.Count == 0)
            {
                _logger.LogInformation("MorningReminder: nothing due today or tomorrow");
                return;
            }

            var message = new StringBuilder("🌅 Morning check — on your plate:\n");
            if (today.Count > 0)
                message.Append("\n<b>Today</b>\n").AppendJoin('\n', today).Append('\n');
            if (tomorrow.Count > 0)
                message.Append("\n<b>Tomorrow</b>\n").AppendJoin('\n', tomorrow).Append('\n');

            await _notificationService.SendPersonalAlertAsync(message.ToString().TrimEnd());

            _logger.LogInformation("MorningReminder sent ({Today} today, {Tomorrow} tomorrow)",
                today.Count, tomorrow.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MorningReminder failed");
            await _notificationService.SendPersonalErrorAsync($"MorningReminder failed: {ex.Message}");
        }
    }
}
