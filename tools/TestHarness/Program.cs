using Alfred.Functions.Configuration;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Services.Calendar;
using Alfred.Functions.Services.Gmail;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.Pdf;
using Alfred.Functions.Services.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestHarness;

// ── Load settings from local.settings.json ──
var settingsPath = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "Alfred.Functions", "local.settings.json"));

if (!File.Exists(settingsPath))
{
    Console.WriteLine($"local.settings.json not found at: {settingsPath}");
    return;
}

var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
var values = json.RootElement.GetProperty("Values");

string GetSetting(string key) => values.TryGetProperty(key, out var val) ? val.GetString() ?? "" : "";

// Set environment variables (used by services that read from env)
Environment.SetEnvironmentVariable("Anthropic__ApiKey", GetSetting("Anthropic:ApiKey"));
Environment.SetEnvironmentVariable("Telegram__BotToken", GetSetting("Telegram:BotToken"));
Environment.SetEnvironmentVariable("Alfred__TelegramChatId", GetSetting("Alfred:TelegramChatId"));

var alfredOptions = Options.Create(new AlfredOptions
{
    SchoolEmailSender = GetSetting("Alfred:SchoolEmailSender"),
    SharedCalendarId = GetSetting("Alfred:SharedCalendarId"),
    TelegramChatId = GetSetting("Alfred:TelegramChatId"),
    LookbackHours = args.Length > 0 && int.TryParse(args[0], out var lhArg) ? lhArg
        : int.TryParse(GetSetting("Alfred:LookbackHours"), out var lh) ? lh : 25,
    SchoolDaysAhead = int.TryParse(GetSetting("Alfred:SchoolDaysAhead"), out var sd) ? sd : 5
});

var googleOptions = Options.Create(new GoogleOptions
{
    ClientId = GetSetting("Google:ClientId"),
    ClientSecret = GetSetting("Google:ClientSecret"),
    RefreshToken = GetSetting("Google:RefreshToken")
});

// ── Set up services ──
var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

var stateService = new InMemoryStateService();
var pdfExtractor = new PdfExtractorService(loggerFactory.CreateLogger<PdfExtractorService>());
var gmailReader = new GmailReaderService(alfredOptions, googleOptions, stateService, pdfExtractor,
    loggerFactory.CreateLogger<GmailReaderService>());
var summarizer = new ClaudeSummarizerService(loggerFactory.CreateLogger<ClaudeSummarizerService>());
var calendarService = new GoogleCalendarService(alfredOptions, googleOptions, stateService,
    loggerFactory.CreateLogger<GoogleCalendarService>());
var telegram = new TelegramNotificationService(loggerFactory.CreateLogger<TelegramNotificationService>());

// ── Menu ──
while (true)
{
    Console.WriteLine();
    Console.WriteLine("=== Alfred Test Harness ===");
    Console.WriteLine();
    Console.WriteLine("1. Test full EmailMonitor pipeline (fetch → summarize → calendar → Telegram)");
    Console.WriteLine("2. Test Telegram only (send a test message)");
    Console.WriteLine("3. Test Gmail only (fetch and list school emails)");
    Console.WriteLine("4. Test Evening Digest (calendar events → Claude digest → Telegram)");
    Console.WriteLine("5. Test Chat Q&A (ask Alfred a question)");
    Console.WriteLine("6. Exit");
    Console.WriteLine();
    Console.Write("Choose: ");

    var choice = Console.ReadLine()?.Trim();

    switch (choice)
    {
        case "1":
            await TestFullPipeline();
            break;
        case "2":
            await TestTelegram();
            break;
        case "3":
            await TestGmail();
            break;
        case "4":
            await TestEveningDigest();
            break;
        case "5":
            await TestChatQA();
            break;
        case "6":
            Console.WriteLine("Bye!");
            return;
        default:
            Console.WriteLine("Bye!");
            return;
    }
}

async Task TestFullPipeline()
{
    Console.WriteLine();
    Console.WriteLine("── Fetching new school emails... ──");

    var emails = await gmailReader.GetNewEmailsAsync();

    if (emails.Count == 0)
    {
        Console.WriteLine("No new school emails found.");
        return;
    }

    Console.WriteLine($"Found {emails.Count} new email(s).");

    foreach (var email in emails)
    {
        Console.WriteLine();
        Console.WriteLine($"── Processing: {email.Subject} ──");
        Console.WriteLine($"   From: {email.SenderName}");
        Console.WriteLine($"   Date: {email.ReceivedDate}");
        Console.WriteLine($"   Docs: {email.Documents.Count}");

        Console.WriteLine();
        Console.WriteLine("── Summarizing with Claude... ──");

        var digest = await summarizer.SummarizeEmailAsync(email);

        Console.WriteLine();
        Console.WriteLine("── Telegram message preview: ──");
        Console.WriteLine(digest.TelegramMessage);

        Console.WriteLine();
        Console.WriteLine($"── Calendar events to create: {digest.CalendarEvents.Count} ──");
        foreach (var evt in digest.CalendarEvents)
        {
            Console.WriteLine($"   [{evt.Action}] {evt.Title} — {evt.Date:ddd d MMM yyyy}" +
                (evt.IsAllDay ? " (all day)" : $" {evt.StartTime}-{evt.EndTime}"));
        }

        Console.Write("\nSend to Telegram and create calendar events? (y/n): ");
        if (Console.ReadLine()?.Trim().ToLower() == "y")
        {
            Console.WriteLine("── Creating calendar events... ──");
            await calendarService.ProcessEventsAsync(digest.CalendarEvents, email.MessageId);

            Console.WriteLine("── Sending Telegram notification... ──");
            await telegram.SendAlertAsync(digest.TelegramMessage);

            await stateService.MarkEmailProcessedAsync(
                email.MessageId, email.Subject, email.SenderName, digest.TelegramMessage, digest.Homework);

            Console.WriteLine("Done!");
        }
        else
        {
            Console.WriteLine("Skipped.");
        }
    }
}

async Task TestTelegram()
{
    Console.WriteLine();
    Console.WriteLine("── Sending test message to Telegram... ──");
    await telegram.SendAlertAsync("🏫 *Alfred — Test Message*\n\nAlfred is working\\! This is a test notification\\.");
    Console.WriteLine("Sent! Check your Telegram group.");
}

async Task TestEveningDigest()
{
    Console.WriteLine();
    var lookback = alfredOptions.Value.LookbackHours;

    // Fetch recent emails from in-memory state (will be empty unless option 1 was run first)
    var since = DateTimeOffset.UtcNow.AddHours(-lookback);
    var recentEmails = await stateService.GetEmailsSinceAsync(since);
    Console.WriteLine($"── Found {recentEmails.Count} processed email(s) in state (last {lookback}h) ──");
    foreach (var e in recentEmails)
        Console.WriteLine($"   - [{e.SenderName}] {e.Subject}");

    // Fetch real upcoming calendar events
    var schoolDaysAhead = alfredOptions.Value.SchoolDaysAhead;
    Console.WriteLine();
    Console.WriteLine($"── Fetching upcoming calendar events ({schoolDaysAhead} school days)... ──");
    var upcomingEvents = await calendarService.GetUpcomingEventsAsync(schoolDaysAhead);
    Console.WriteLine($"   Found {upcomingEvents.Count} event(s):");
    foreach (var ev in upcomingEvents)
    {
        var date = ev.Start.DateTimeDateTimeOffset?.ToString("ddd d MMM") ?? ev.Start.Date ?? "TBD";
        Console.WriteLine($"   - {date}: {ev.Summary}");
    }

    if (recentEmails.Count == 0 && upcomingEvents.Count == 0)
    {
        Console.WriteLine();
        Console.WriteLine("No emails or events to include. Run option 1 first to process emails,");
        Console.WriteLine("or check that the shared calendar has upcoming events.");
        Console.Write("Generate digest anyway? (y/n): ");
        if (Console.ReadLine()?.Trim().ToLower() != "y")
            return;
    }

    // Build digest with Claude
    Console.WriteLine();
    Console.WriteLine("── Building evening digest with Claude... ──");
    var digestMessage = await summarizer.BuildEveningDigestAsync(recentEmails, upcomingEvents);

    Console.WriteLine();
    Console.WriteLine("── Digest preview: ──");
    Console.WriteLine(digestMessage);

    Console.WriteLine();
    Console.Write("Send to Telegram? (y/n): ");
    if (Console.ReadLine()?.Trim().ToLower() == "y")
    {
        await telegram.SendAlertAsync(digestMessage);
        Console.WriteLine("Sent!");
    }
    else
    {
        Console.WriteLine("Skipped.");
    }
}

async Task TestChatQA()
{
    Console.WriteLine();

    // Fetch context: recent emails from state + upcoming calendar events
    var lookbackDays = 30;
    var since = DateTimeOffset.UtcNow.AddDays(-lookbackDays);
    var recentEmails = await stateService.GetEmailsSinceAsync(since);
    Console.WriteLine($"── Context: {recentEmails.Count} email(s) in state (last {lookbackDays} days) ──");

    var schoolDaysAhead = alfredOptions.Value.SchoolDaysAhead;
    var upcomingEvents = await calendarService.GetUpcomingEventsAsync(schoolDaysAhead);
    Console.WriteLine($"── Context: {upcomingEvents.Count} upcoming calendar event(s) ──");

    if (recentEmails.Count == 0 && upcomingEvents.Count == 0)
    {
        Console.WriteLine();
        Console.WriteLine("No context available. Run option 1 first to process emails,");
        Console.WriteLine("or check that the shared calendar has upcoming events.");
    }

    Console.WriteLine();
    Console.Write("Ask Alfred a question: ");
    var question = Console.ReadLine()?.Trim();

    if (string.IsNullOrWhiteSpace(question))
    {
        Console.WriteLine("No question entered.");
        return;
    }

    Console.WriteLine();
    Console.WriteLine("── Asking Claude... ──");
    var answer = await summarizer.AnswerQuestionAsync(question, recentEmails, upcomingEvents);

    Console.WriteLine();
    Console.WriteLine("── Answer: ──");
    Console.WriteLine(answer);

    Console.WriteLine();
    Console.Write("Send answer to Telegram? (y/n): ");
    if (Console.ReadLine()?.Trim().ToLower() == "y")
    {
        await telegram.SendAlertAsync(answer);
        Console.WriteLine("Sent!");
    }
    else
    {
        Console.WriteLine("Skipped.");
    }
}

async Task TestGmail()
{
    Console.WriteLine();
    Console.WriteLine("── Fetching school emails... ──");

    var emails = await gmailReader.GetNewEmailsAsync();

    Console.WriteLine($"Found {emails.Count} email(s):");
    foreach (var email in emails)
    {
        Console.WriteLine();
        Console.WriteLine($"  Subject: {email.Subject}");
        Console.WriteLine($"  From:    {email.SenderName}");
        Console.WriteLine($"  Date:    {email.ReceivedDate}");
        Console.WriteLine($"  Body:    {email.Body[..Math.Min(200, email.Body.Length)]}...");
        Console.WriteLine($"  Docs:    {email.Documents.Count}");
        foreach (var doc in email.Documents)
        {
            var textLen = doc.ExtractedText?.Length ?? 0;
            Console.WriteLine($"           - [{doc.Source}] {doc.Title} ({textLen} chars)");
        }
    }
}
