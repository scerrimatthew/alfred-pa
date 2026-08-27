using Alfred.Functions.Configuration;
using Alfred.Functions.Functions;
using Alfred.Functions.Models;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Services.Calendar;
using Alfred.Functions.Services.Gmail;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.State;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using static Alfred.Functions.Tests.Support.TestData;

namespace Alfred.Functions.Tests;

public class PersonalEmailMonitorFunctionTests
{
    private readonly IGmailReaderService _gmail = Substitute.For<IGmailReaderService>();
    private readonly ISummarizerService _summarizer = Substitute.For<ISummarizerService>();
    private readonly ICalendarService _calendar = Substitute.For<ICalendarService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IStateService _state = Substitute.For<IStateService>();

    public PersonalEmailMonitorFunctionTests()
    {
        _gmail.GetNewPersonalEmailsAsync().Returns([]);
        _state.GetSuppressionRulesAsync().Returns(new List<SuppressionRuleEntity>());
        _state.GetAttentionRulesAsync().Returns(new List<AttentionRuleEntity>());
        _state.GetUserFactsAsync().Returns(new List<UserFactEntity>());
        _state.GetPersonalEmailsByThreadAsync(Arg.Any<string>()).Returns(new List<ProcessedEmailEntity>());
        _state.GetBackfillStateAsync().Returns((BackfillStateEntity?)null);
    }

    private PersonalEmailMonitorFunction CreateFunction(Action<AlfredOptions>? mutate = null) =>
        new(_gmail, _summarizer, _calendar, _notifications, _state,
            Options(o =>
            {
                o.PersonalTelegramChatId = "777";
                mutate?.Invoke(o);
            }),
            NullLogger<PersonalEmailMonitorFunction>.Instance);

    private static TimerInfo Timer => new();

    [Fact]
    public async Task WithoutPersonalChatId_TheMonitorIsDisabled()
    {
        var function = new PersonalEmailMonitorFunction(
            _gmail, _summarizer, _calendar, _notifications, _state,
            Options(), NullLogger<PersonalEmailMonitorFunction>.Instance);

        await function.Run(Timer);

        await _gmail.DidNotReceive().GetNewPersonalEmailsAsync();
        await _state.DidNotReceive().GetBackfillStateAsync();
    }

    [Fact]
    public async Task AttentionWorthyUnreadEmail_NotifiesWithActionButtons()
    {
        var email = Email(messageId: "m1", threadId: "t1", subject: "GO bill");
        _gmail.GetNewPersonalEmailsAsync().Returns([email]);
        _summarizer.TriagePersonalEmailAsync(email, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage(requiresAttention: true, category: "invoice", telegramMessage: "Your GO bill is in."));

        string? message = null;
        IReadOnlyList<NotificationButton>? buttons = null;
        _notifications.When(n => n.SendPersonalAlertAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<NotificationButton>?>()))
            .Do(ci =>
            {
                message = ci.ArgAt<string>(0);
                buttons = ci.ArgAt<IReadOnlyList<NotificationButton>?>(1);
            });

        await CreateFunction().Run(Timer);

        Assert.NotNull(message);
        Assert.StartsWith("Your GO bill is in.", message);
        Assert.Contains(GmailLinks.ForThread("t1"), message);

        Assert.NotNull(buttons);
        Assert.Equal(3, buttons.Count);
        Assert.Equal("mu:m1", buttons[0].CallbackData);
        Assert.Equal("sup:m1", buttons[1].CallbackData);
        Assert.Equal("sn1:m1", buttons[2].CallbackData);

        // Attention-worthy mail stays unread in Gmail so it remains visible in the
        // inbox until dealt with — labeled only, never marked read
        await _gmail.Received(1).LabelWithoutMarkingReadAsync("m1", "Invoice");
        await _gmail.DidNotReceiveWithAnyArgs().MarkAsReadAndLabelAsync(default!, default!);
    }

    [Fact]
    public async Task EmptyTriageMessage_FallsBackToSubjectSenderSummary()
    {
        var email = Email(subject: "Vet appointment", senderName: "City Vet");
        _gmail.GetNewPersonalEmailsAsync().Returns([email]);
        _summarizer.TriagePersonalEmailAsync(email, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage(requiresAttention: true, summary: "Appointment confirmed for Friday.", telegramMessage: ""));

        await CreateFunction().Run(Timer);

        await _notifications.Received(1).SendPersonalAlertAsync(
            Arg.Is<string>(m => m.Contains("Vet appointment") && m.Contains("City Vet") && m.Contains("Appointment confirmed for Friday.")),
            Arg.Any<IReadOnlyList<NotificationButton>?>());
    }

    [Fact]
    public async Task QuietEmail_IsFiledWithoutNotification()
    {
        var email = Email(messageId: "m1", subject: "Newsletter", senderEmail: "news@shop.com");
        _gmail.GetNewPersonalEmailsAsync().Returns([email]);
        _summarizer.TriagePersonalEmailAsync(email, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage(requiresAttention: false, category: "notification", summary: "Just a newsletter."));

        await CreateFunction().Run(Timer);

        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _state.Received(1).MarkPersonalEmailProcessedAsync(
            "m1", "Newsletter", email.SenderName, "Just a newsletter.", "notification", false, email.ThreadId, "news@shop.com", false, null);
        await _gmail.Received(1).MarkAsReadAndLabelAsync("m1", "Notification");
        await _gmail.DidNotReceiveWithAnyArgs().LabelWithoutMarkingReadAsync(default!, default!);
        await _state.Received(1).RecordSenderSeenAsync("news@shop.com", email.SenderName, true, null, false);
    }

    [Fact]
    public async Task NotifyAllPersonalEmails_NotifiesEvenQuietOnes()
    {
        var email = Email(subject: "Newsletter");
        _gmail.GetNewPersonalEmailsAsync().Returns([email]);
        _summarizer.TriagePersonalEmailAsync(email, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage(requiresAttention: false, telegramMessage: "A quiet one."));

        await CreateFunction(o => o.NotifyAllPersonalEmails = true).Run(Timer);

        await _notifications.Received(1).SendPersonalAlertAsync(
            Arg.Is<string>(m => m.StartsWith("A quiet one.")), Arg.Any<IReadOnlyList<NotificationButton>?>());

        // Keeping the unread state is keyed to RequiresAttention, not to whether a
        // notification went out — a notify-all courtesy alert still files the email as read
        await _gmail.ReceivedWithAnyArgs(1).MarkAsReadAndLabelAsync(default!, default!);
        await _gmail.DidNotReceiveWithAnyArgs().LabelWithoutMarkingReadAsync(default!, default!);
    }

    [Fact]
    public async Task SuppressedEmail_NeverNotifiesNorCreatesCalendarEvents()
    {
        var email = Email(messageId: "m1", senderEmail: "bolt@bolt.eu");
        var events = new List<CalendarEventInfo>
        {
            new() { Title = "Deadline: X", Description = "", Date = DateTime.Today.AddDays(3), Action = CalendarEventAction.Create }
        };
        _gmail.GetNewPersonalEmailsAsync().Returns([email]);
        _summarizer.TriagePersonalEmailAsync(email, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage(requiresAttention: true, suppressed: true, matchedRule: "r1", needsReply: true, calendarEvents: events));

        await CreateFunction().Run(Timer);

        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _calendar.DidNotReceiveWithAnyArgs().ProcessPersonalEventsAsync(default!, default!);
        // Still recorded (suppressed, and never flagged needs-reply) and labeled
        await _state.Received(1).MarkPersonalEmailProcessedAsync(
            "m1", email.Subject, email.SenderName, Arg.Any<string>(), Arg.Any<string?>(),
            true, email.ThreadId, email.SenderEmail, false, null);
        // Suppressed wins over RequiresAttention (true here, e.g. via a fraud flag or
        // attention rule set upstream): a suppressed email is still marked read
        await _gmail.ReceivedWithAnyArgs(1).MarkAsReadAndLabelAsync(default!, default!);
        await _gmail.DidNotReceiveWithAnyArgs().LabelWithoutMarkingReadAsync(default!, default!);
        await _state.Received(1).RecordSenderSeenAsync(email.SenderEmail, email.SenderName, true, null, false);
    }

    [Fact]
    public async Task FraudulentEmail_WarnsEvenWhenAlreadyRead_AndNeverCreatesReminders()
    {
        var email = Email(messageId: "m1", threadId: "t1", wasUnread: false);
        var events = new List<CalendarEventInfo>
        {
            new() { Title = "Deadline: Pay fake invoice", Description = "", Date = DateTime.Today.AddDays(2), Action = CalendarEventAction.Create }
        };
        _gmail.GetNewPersonalEmailsAsync().Returns([email]);
        _summarizer.TriagePersonalEmailAsync(email, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage(
                requiresAttention: true,
                telegramMessage: "An invoice from ACME.",
                fraudWarning: "Claims to be ACME but was sent from acme-billing.net.",
                calendarEvents: events));

        string? message = null;
        _notifications.When(n => n.SendPersonalAlertAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<NotificationButton>?>()))
            .Do(ci => message = ci.ArgAt<string>(0));

        await CreateFunction().Run(Timer);

        Assert.NotNull(message);
        Assert.StartsWith("⚠️ <b>Careful:</b> Claims to be ACME but was sent from acme-billing.net.", message);
        Assert.Contains("An invoice from ACME.", message);
        await _calendar.DidNotReceiveWithAnyArgs().ProcessPersonalEventsAsync(default!, default!);
    }

    [Fact]
    public async Task AlreadyReadNonFraudEmail_IsProcessedSilently()
    {
        var email = Email(wasUnread: false);
        _gmail.GetNewPersonalEmailsAsync().Returns([email]);
        _summarizer.TriagePersonalEmailAsync(email, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage(requiresAttention: true, telegramMessage: "Important but already read."));

        await CreateFunction().Run(Timer);

        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _state.ReceivedWithAnyArgs(1).MarkPersonalEmailProcessedAsync(default!, default!, default!, default!);
        // The keep-unread branch keys off RequiresAttention alone — an attention-worthy
        // email Matthew already read himself is labeled without touching its read state
        await _gmail.ReceivedWithAnyArgs(1).LabelWithoutMarkingReadAsync(default!, default!);
        await _gmail.DidNotReceiveWithAnyArgs().MarkAsReadAndLabelAsync(default!, default!);
    }

    [Fact]
    public async Task ReplyInKnownThread_PassesEarlierSummariesToTriage()
    {
        var email = Email(messageId: "m2", threadId: "t1");
        var earlier = new List<ProcessedEmailEntity> { ProcessedEmail(messageId: "m1", threadId: "t1") };
        _gmail.GetNewPersonalEmailsAsync().Returns([email]);
        _state.GetPersonalEmailsByThreadAsync("t1").Returns(earlier);
        _summarizer.TriagePersonalEmailAsync(email, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage());

        await CreateFunction().Run(Timer);

        await _summarizer.Received(1).TriagePersonalEmailAsync(
            email, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), earlier);
    }

    [Fact]
    public async Task FirstMessageOfThread_SkipsTheThreadContextLookup()
    {
        var email = Email(messageId: "m1", threadId: "m1");
        _gmail.GetNewPersonalEmailsAsync().Returns([email]);
        _summarizer.TriagePersonalEmailAsync(email, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage());

        await CreateFunction().Run(Timer);

        await _state.DidNotReceiveWithAnyArgs().GetPersonalEmailsByThreadAsync(default!);
    }

    [Fact]
    public async Task NeedsReplyFlag_IsPersistedForUnsuppressedEmails()
    {
        var email = Email(messageId: "m1");
        _gmail.GetNewPersonalEmailsAsync().Returns([email]);
        _summarizer.TriagePersonalEmailAsync(email, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage(requiresAttention: true, telegramMessage: "Sarah wrote", needsReply: true));

        await CreateFunction().Run(Timer);

        await _state.Received(1).MarkPersonalEmailProcessedAsync(
            "m1", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            false, Arg.Any<string?>(), Arg.Any<string?>(), true, null);
    }

    [Fact]
    public async Task SenderStatsFailure_DoesNotFailTheEmail()
    {
        var email = Email(messageId: "m1", senderEmail: "x@y.com");
        _gmail.GetNewPersonalEmailsAsync().Returns([email]);
        _summarizer.TriagePersonalEmailAsync(email, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage());
        _state.RecordSenderSeenAsync(default!, default!, default, default, default)
            .ThrowsAsyncForAnyArgs(new TimeoutException("tables slow"));

        await CreateFunction().Run(Timer);

        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalErrorAsync(default!);
        await _state.ReceivedWithAnyArgs(1).MarkPersonalEmailProcessedAsync(default!, default!, default!, default!);
    }

    [Fact]
    public async Task TriageFailure_ReportsErrorAndContinues()
    {
        var bad = Email(messageId: "bad", subject: "Broken");
        var good = Email(messageId: "good");
        _gmail.GetNewPersonalEmailsAsync().Returns([bad, good]);
        _summarizer.TriagePersonalEmailAsync(bad, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .ThrowsAsync(new InvalidOperationException("boom"));
        _summarizer.TriagePersonalEmailAsync(good, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage());

        await CreateFunction().Run(Timer);

        await _notifications.Received(1).SendPersonalErrorAsync(Arg.Is<string>(m => m.Contains("Broken") && m.Contains("boom")));
        await _state.Received(1).MarkPersonalEmailProcessedAsync(
            "good", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<DateTimeOffset?>());
    }

    [Fact]
    public async Task SavedFacts_AreLoadedOncePerRunAndPassedToEveryTriage()
    {
        var first = Email(messageId: "m1");
        var second = Email(messageId: "m2");
        var facts = new List<UserFactEntity> { new() { RowKey = "f1", Fact = "Matthew's apartment is A5 in Block A." } };
        _gmail.GetNewPersonalEmailsAsync().Returns([first, second]);
        _state.GetUserFactsAsync().Returns(facts);
        _summarizer.TriagePersonalEmailAsync(Arg.Any<SchoolEmail>(), Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage());

        await CreateFunction().Run(Timer);

        // The very list from state reaches each triage call — one fetch for the whole batch
        await _summarizer.Received(1).TriagePersonalEmailAsync(
            first, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), facts, Arg.Any<List<ProcessedEmailEntity>>());
        await _summarizer.Received(1).TriagePersonalEmailAsync(
            second, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), facts, Arg.Any<List<ProcessedEmailEntity>>());
        await _state.Received(1).GetUserFactsAsync();
    }

    // ---- Newsletter-mined news leads ----

    [Fact]
    public async Task NewsLeadsFromANewsletter_AreSavedAsCandidatesWithTheSenderAsSource()
    {
        var email = Email(messageId: "m1", senderName: "TLDR AI");
        var leads = new List<NewsLead>
        {
            new() { Headline = "DORA 2026 lands", Url = "https://dora.dev/2026", Note = "Review times doubled" },
            new() { Headline = "Funding round", Url = null, Note = null }
        };
        _gmail.GetNewPersonalEmailsAsync().Returns([email]);
        _summarizer.TriagePersonalEmailAsync(email, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage(newsLeads: leads));

        List<NewsCandidateEntity>? saved = null;
        _state.When(s => s.SaveNewsCandidatesAsync(Arg.Any<List<NewsCandidateEntity>>()))
            .Do(ci => saved = ci.Arg<List<NewsCandidateEntity>>());

        await CreateFunction().Run(Timer);

        Assert.NotNull(saved);
        Assert.Equal(2, saved.Count);
        Assert.Equal("DORA 2026 lands", saved[0].Headline);
        Assert.Equal("https://dora.dev/2026", saved[0].Url);
        Assert.Equal("Review times doubled", saved[0].Note);
        Assert.Equal("TLDR AI", saved[0].Source);
        Assert.Null(saved[1].Url);
        Assert.Equal("TLDR AI", saved[1].Source);
    }

    [Fact]
    public async Task NoNewsLeads_TheOrdinaryCase_NeverTouchesTheCandidateStore()
    {
        var email = Email(messageId: "m1");
        _gmail.GetNewPersonalEmailsAsync().Returns([email]);
        _summarizer.TriagePersonalEmailAsync(email, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage());

        await CreateFunction().Run(Timer);

        await _state.DidNotReceiveWithAnyArgs().SaveNewsCandidatesAsync(default!);
    }

    [Fact]
    public async Task NewsLeadSaveFailure_IsBestEffort_TheEmailStillCompletes()
    {
        var email = Email(messageId: "m1", senderEmail: "news@tldr.tech");
        _gmail.GetNewPersonalEmailsAsync().Returns([email]);
        _summarizer.TriagePersonalEmailAsync(email, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage(newsLeads: [new NewsLead { Headline = "H" }]));
        _state.SaveNewsCandidatesAsync(Arg.Any<List<NewsCandidateEntity>>())
            .ThrowsAsync(new TimeoutException("tables slow"));

        await CreateFunction().Run(Timer);

        // No error alert, and the rest of the pipeline (sender tally) still ran
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalErrorAsync(default!);
        await _state.ReceivedWithAnyArgs(1).MarkPersonalEmailProcessedAsync(default!, default!, default!, default!);
        await _state.Received(1).RecordSenderSeenAsync("news@tldr.tech", email.SenderName, true, null, false);
    }

    // ---- Backfill batches ----

    [Fact]
    public async Task Backfill_ProcessesQuietly_BackdatesAndLabelsWithoutMarkingRead()
    {
        var received = new DateTimeOffset(2026, 5, 2, 9, 0, 0, TimeSpan.Zero);
        var old = Email(messageId: "old1", subject: "Old invoice", senderEmail: "a@b.com", receivedDate: received);
        var backfill = new BackfillStateEntity { OldestDate = DateTimeOffset.UtcNow.AddDays(-60), ProcessedCount = 5 };

        _state.GetBackfillStateAsync().Returns(backfill);
        _gmail.GetBackfillBatchAsync(backfill.OldestDate, 20).Returns([old]);
        _summarizer.TriagePersonalEmailAsync(old, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage(requiresAttention: true, category: "invoice", telegramMessage: "would have alerted", needsReply: true));

        await CreateFunction().Run(Timer);

        // Quiet: no per-email alert, only the single completion message
        await _notifications.Received(1).SendPersonalAlertAsync(
            Arg.Is<string>(m => m.Contains("Backfill finished") && m.Contains("<b>6</b>")));
        await _notifications.DidNotReceive().SendPersonalAlertAsync(Arg.Any<string>(), Arg.Is<IReadOnlyList<NotificationButton>?>(b => b != null));

        // Backdated, never needs-reply, labeled without touching the read flag
        await _state.Received(1).MarkPersonalEmailProcessedAsync(
            "old1", "Old invoice", old.SenderName, Arg.Any<string>(), "invoice",
            false, old.ThreadId, "a@b.com", false, received);
        await _gmail.Received(1).LabelWithoutMarkingReadAsync("old1", "Invoice");
        await _gmail.DidNotReceiveWithAnyArgs().MarkAsReadAndLabelAsync(default!, default!);

        // Count incremented and persisted, then the marker cleared (batch < 20 = done)
        await _state.Received(1).SaveBackfillStateAsync(Arg.Is<BackfillStateEntity>(b => b.ProcessedCount == 6));
        await _state.Received(1).ClearBackfillStateAsync();
    }

    [Fact]
    public async Task Backfill_FullBatch_KeepsTheMarkerAndStaysQuiet()
    {
        var backfill = new BackfillStateEntity { OldestDate = DateTimeOffset.UtcNow.AddDays(-60) };
        var batch = Enumerable.Range(0, 20).Select(i => Email(messageId: $"m{i}")).ToList();

        _state.GetBackfillStateAsync().Returns(backfill);
        _gmail.GetBackfillBatchAsync(backfill.OldestDate, 20).Returns(batch);
        _summarizer.TriagePersonalEmailAsync(Arg.Any<SchoolEmail>(), Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage());

        await CreateFunction().Run(Timer);

        await _state.Received(1).SaveBackfillStateAsync(Arg.Is<BackfillStateEntity>(b => b.ProcessedCount == 20));
        await _state.DidNotReceive().ClearBackfillStateAsync();
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
    }

    [Fact]
    public async Task Backfill_EmptyBatch_CompletesWithoutSavingProgress()
    {
        var backfill = new BackfillStateEntity { OldestDate = DateTimeOffset.UtcNow.AddDays(-30), ProcessedCount = 42 };
        _state.GetBackfillStateAsync().Returns(backfill);
        _gmail.GetBackfillBatchAsync(backfill.OldestDate, 20).Returns([]);

        await CreateFunction().Run(Timer);

        await _state.DidNotReceiveWithAnyArgs().SaveBackfillStateAsync(default!);
        await _state.Received(1).ClearBackfillStateAsync();
        await _notifications.Received(1).SendPersonalAlertAsync(Arg.Is<string>(m => m.Contains("<b>42</b>")));
    }

    [Fact]
    public async Task Backfill_SuppressedHistoricalEmail_SkipsCalendarEvents()
    {
        var old = Email(messageId: "old1");
        var backfill = new BackfillStateEntity { OldestDate = DateTimeOffset.UtcNow.AddDays(-60) };
        var events = new List<CalendarEventInfo>
        {
            new() { Title = "Deadline: X", Description = "", Date = DateTime.Today.AddDays(1), Action = CalendarEventAction.Create }
        };

        _state.GetBackfillStateAsync().Returns(backfill);
        _gmail.GetBackfillBatchAsync(backfill.OldestDate, 20).Returns([old]);
        _summarizer.TriagePersonalEmailAsync(old, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage(suppressed: true, calendarEvents: events));

        await CreateFunction().Run(Timer);

        await _calendar.DidNotReceiveWithAnyArgs().ProcessPersonalEventsAsync(default!, default!);
    }

    [Fact]
    public async Task Backfill_DeliberatelyDoesNotHarvestNewsLeads()
    {
        // Historical newsletters must not pollute tonight's candidate list
        var old = Email(messageId: "old1", senderName: "TLDR AI");
        var backfill = new BackfillStateEntity { OldestDate = DateTimeOffset.UtcNow.AddDays(-60) };

        _state.GetBackfillStateAsync().Returns(backfill);
        _gmail.GetBackfillBatchAsync(backfill.OldestDate, 20).Returns([old]);
        _summarizer.TriagePersonalEmailAsync(old, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage(newsLeads: [new NewsLead { Headline = "Old lead", Url = "https://old.example" }]));

        await CreateFunction().Run(Timer);

        await _state.DidNotReceiveWithAnyArgs().SaveNewsCandidatesAsync(default!);
        // The email itself was still processed normally
        await _state.ReceivedWithAnyArgs(1).MarkPersonalEmailProcessedAsync(default!, default!, default!, default!);
    }

    [Fact]
    public async Task Backfill_AlsoPassesSavedFactsToTriage()
    {
        // Historical mail is judged with the same facts ("that notice is for Block B, not his")
        var old = Email(messageId: "old1");
        var backfill = new BackfillStateEntity { OldestDate = DateTimeOffset.UtcNow.AddDays(-60) };
        var facts = new List<UserFactEntity> { new() { RowKey = "f1", Fact = "Matthew's apartment is A5 in Block A." } };

        _state.GetBackfillStateAsync().Returns(backfill);
        _state.GetUserFactsAsync().Returns(facts);
        _gmail.GetBackfillBatchAsync(backfill.OldestDate, 20).Returns([old]);
        _summarizer.TriagePersonalEmailAsync(old, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), Arg.Any<List<UserFactEntity>>(), Arg.Any<List<ProcessedEmailEntity>>())
            .Returns(Triage());

        await CreateFunction().Run(Timer);

        await _summarizer.Received(1).TriagePersonalEmailAsync(
            old, Arg.Any<List<SuppressionRuleEntity>>(), Arg.Any<List<AttentionRuleEntity>>(), facts, Arg.Any<List<ProcessedEmailEntity>>());
    }

    [Fact]
    public async Task Backfill_GmailFailure_IsSwallowedAndRetriedNextRun()
    {
        var backfill = new BackfillStateEntity { OldestDate = DateTimeOffset.UtcNow.AddDays(-60) };
        _state.GetBackfillStateAsync().Returns(backfill);
        _gmail.GetBackfillBatchAsync(Arg.Any<DateTimeOffset>(), Arg.Any<int>())
            .ThrowsAsync(new HttpRequestException("gmail down"));

        await CreateFunction().Run(Timer);

        await _state.DidNotReceive().ClearBackfillStateAsync();
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalErrorAsync(default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
    }
}
