using System.Linq.Expressions;
using Alfred.Functions.Models;
using Alfred.Functions.Services.State;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Alfred.Functions.Tests;

// Drives TableStorageStateService against substituted TableClients backed by
// in-memory lists. Query expressions are compiled and applied to the store, so
// these tests pin the actual filter predicates (partition separation, the explicit
// NeedsReply == true comparison, date windows), not just the plumbing.
public class TableStorageStateServiceTests
{
    private readonly TableStorageStateService _service;

    private readonly List<ProcessedEmailEntity> _processedEmails = [];
    private readonly List<SnoozedEmailEntity> _snoozes = [];
    private readonly List<SenderStatsEntity> _senderStats = [];
    private readonly List<ChatTurnEntity> _chatTurns = [];
    private readonly List<SuppressionRuleEntity> _suppressionRules = [];
    private readonly List<AttentionRuleEntity> _attentionRules = [];
    private readonly List<CalendarEventEntity> _calendarEvents = [];
    private readonly List<BackfillStateEntity> _backfillState = [];

    public TableStorageStateServiceTests()
    {
        // Build each fake table up front: configuring one substitute inside another's
        // Returns() confuses NSubstitute's pending-call tracking
        var processedEmailsClient = CreateTableClient(_processedEmails);
        var snoozesClient = CreateTableClient(_snoozes);
        var senderStatsClient = CreateTableClient(_senderStats);
        var chatTurnsClient = CreateTableClient(_chatTurns);
        var suppressionRulesClient = CreateTableClient(_suppressionRules);
        var attentionRulesClient = CreateTableClient(_attentionRules);
        var calendarEventsClient = CreateTableClient(_calendarEvents);
        var backfillStateClient = CreateTableClient(_backfillState);

        var serviceClient = Substitute.For<TableServiceClient>();
        serviceClient.GetTableClient("ProcessedEmails").Returns(processedEmailsClient);
        serviceClient.GetTableClient("SnoozedEmails").Returns(snoozesClient);
        serviceClient.GetTableClient("SenderStats").Returns(senderStatsClient);
        serviceClient.GetTableClient("ChatHistory").Returns(chatTurnsClient);
        serviceClient.GetTableClient("SuppressionRules").Returns(suppressionRulesClient);
        serviceClient.GetTableClient("AttentionRules").Returns(attentionRulesClient);
        serviceClient.GetTableClient("CalendarEvents").Returns(calendarEventsClient);
        serviceClient.GetTableClient("BackfillState").Returns(backfillStateClient);

        _service = new TableStorageStateService(serviceClient, NullLogger<TableStorageStateService>.Instance);
    }

    private static TableClient CreateTableClient<T>(List<T> store) where T : class, ITableEntity, new()
    {
        var client = Substitute.For<TableClient>();

        client.QueryAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var filter = ci.Arg<Expression<Func<T, bool>>>().Compile();
                var page = Page<T>.FromValues(store.Where(filter).ToList(), null, Substitute.For<Response>());
                return AsyncPageable<T>.FromPages([page]);
            });

        client.GetEntityAsync<T>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var partitionKey = ci.ArgAt<string>(0);
                var rowKey = ci.ArgAt<string>(1);
                var found = store.FirstOrDefault(e => e.PartitionKey == partitionKey && e.RowKey == rowKey);
                return found is not null
                    ? Response.FromValue(found, Substitute.For<Response>())
                    : throw new RequestFailedException(404, "Not Found");
            });

        client.UpsertEntityAsync(Arg.Any<T>(), Arg.Any<TableUpdateMode>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var entity = ci.Arg<T>();
                store.RemoveAll(e => e.PartitionKey == entity.PartitionKey && e.RowKey == entity.RowKey);
                store.Add(entity);
                return Substitute.For<Response>();
            });

        client.DeleteEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ETag>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                store.RemoveAll(e => e.PartitionKey == ci.ArgAt<string>(0) && e.RowKey == ci.ArgAt<string>(1));
                return Substitute.For<Response>();
            });

        return client;
    }

    private static ProcessedEmailEntity Entity(
        string partition, string id, DateTimeOffset processedAt,
        bool needsReply = false, bool suppressed = false, string? threadId = null) =>
        new()
        {
            PartitionKey = partition,
            RowKey = id,
            ProcessedAt = processedAt,
            NeedsReply = needsReply,
            Suppressed = suppressed,
            GmailThreadId = threadId
        };

    // ---- Processed-email tracking ----

    [Fact]
    public async Task SchoolAndPersonalPartitions_NeverBleedIntoEachOther()
    {
        _processedEmails.Add(Entity("emails", "school-1", DateTimeOffset.UtcNow));
        _processedEmails.Add(Entity("personal", "personal-1", DateTimeOffset.UtcNow));

        Assert.True(await _service.IsEmailProcessedAsync("school-1"));
        Assert.False(await _service.IsEmailProcessedAsync("personal-1"));
        Assert.True(await _service.IsPersonalEmailProcessedAsync("personal-1"));
        Assert.False(await _service.IsPersonalEmailProcessedAsync("school-1"));
    }

    [Fact]
    public async Task MarkEmailProcessed_WritesASchoolRowWithAllFields()
    {
        await _service.MarkEmailProcessedAsync("m1", "Subject", "Teacher", "summary", "read a book", "homework", "t1");

        var entity = Assert.Single(_processedEmails);
        Assert.Equal("emails", entity.PartitionKey);
        Assert.Equal("m1", entity.RowKey);
        Assert.Equal("Subject", entity.Subject);
        Assert.Equal("Teacher", entity.SenderName);
        Assert.Equal("summary", entity.Summary);
        Assert.Equal("read a book", entity.Homework);
        Assert.Equal("homework", entity.Category);
        Assert.Equal("t1", entity.GmailThreadId);
        Assert.False(entity.NeedsReply);
        Assert.True((DateTimeOffset.UtcNow - entity.ProcessedAt).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task MarkPersonalEmailProcessed_BackdatesProcessedAtWhenAsked()
    {
        var receivedDate = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

        await _service.MarkPersonalEmailProcessedAsync(
            "m1", "S", "Sender", "sum", "invoice", suppressed: true, threadId: "t1",
            senderEmail: "a@b.com", needsReply: true, processedAt: receivedDate);

        var entity = Assert.Single(_processedEmails);
        Assert.Equal("personal", entity.PartitionKey);
        Assert.Equal(receivedDate, entity.ProcessedAt);
        Assert.True(entity.Suppressed);
        Assert.True(entity.NeedsReply);
        Assert.Equal("a@b.com", entity.SenderEmail);
        Assert.Null(entity.Homework);
    }

    [Fact]
    public async Task GetEmailsSince_AppliesPartitionAndDateWindow()
    {
        var now = DateTimeOffset.UtcNow;
        _processedEmails.Add(Entity("emails", "recent", now.AddHours(-1)));
        _processedEmails.Add(Entity("emails", "ancient", now.AddDays(-10)));
        _processedEmails.Add(Entity("personal", "personal-recent", now.AddHours(-1)));

        var results = await _service.GetEmailsSinceAsync(now.AddHours(-25));

        Assert.Equal("recent", Assert.Single(results).RowKey);
    }

    [Fact]
    public async Task GetPersonalEmailsNeedingReply_FiltersFlagWindowAndPartition_OldestFirst()
    {
        var now = DateTimeOffset.UtcNow;
        _processedEmails.Add(Entity("personal", "newer-flagged", now.AddDays(-1), needsReply: true));
        _processedEmails.Add(Entity("personal", "older-flagged", now.AddDays(-3), needsReply: true));
        _processedEmails.Add(Entity("personal", "not-flagged", now.AddDays(-1)));
        _processedEmails.Add(Entity("personal", "too-old", now.AddDays(-30), needsReply: true));
        _processedEmails.Add(Entity("emails", "school-flagged", now.AddDays(-1), needsReply: true));

        var results = await _service.GetPersonalEmailsNeedingReplyAsync(now.AddDays(-7));

        Assert.Equal(new[] { "older-flagged", "newer-flagged" }, results.Select(r => r.RowKey).ToArray());
    }

    [Fact]
    public async Task ClearNeedsReply_UnsetsTheFlagInPlace()
    {
        _processedEmails.Add(Entity("personal", "m1", DateTimeOffset.UtcNow, needsReply: true));

        await _service.ClearNeedsReplyAsync("m1");

        Assert.False(Assert.Single(_processedEmails).NeedsReply);
    }

    [Fact]
    public async Task ClearNeedsReply_MissingRow_IsANoOp()
    {
        await _service.ClearNeedsReplyAsync("ghost");

        Assert.Empty(_processedEmails);
    }

    [Fact]
    public async Task GetPersonalEmail_ReturnsTheRowOrNull()
    {
        _processedEmails.Add(Entity("personal", "m1", DateTimeOffset.UtcNow));

        Assert.NotNull(await _service.GetPersonalEmailAsync("m1"));
        Assert.Null(await _service.GetPersonalEmailAsync("ghost"));
    }

    [Fact]
    public async Task UpdatePersonalEmailCategory_RewritesTheCategory()
    {
        _processedEmails.Add(Entity("personal", "m1", DateTimeOffset.UtcNow));

        await _service.UpdatePersonalEmailCategoryAsync("m1", "delivery");

        Assert.Equal("delivery", Assert.Single(_processedEmails).Category);
    }

    [Fact]
    public async Task GetPersonalEmailsByThread_ReturnsThreadRowsInChronologicalOrder()
    {
        var now = DateTimeOffset.UtcNow;
        _processedEmails.Add(Entity("personal", "second", now.AddHours(-1), threadId: "t1"));
        _processedEmails.Add(Entity("personal", "first", now.AddHours(-5), threadId: "t1"));
        _processedEmails.Add(Entity("personal", "other-thread", now, threadId: "t2"));

        var results = await _service.GetPersonalEmailsByThreadAsync("t1");

        Assert.Equal(new[] { "first", "second" }, results.Select(r => r.RowKey).ToArray());
    }

    // ---- Snoozes ----

    [Fact]
    public async Task SaveSnooze_StoresTheDenormalizedReminder()
    {
        var dueAt = DateTimeOffset.UtcNow.AddDays(1);

        await _service.SaveSnoozeAsync("m1", "GO bill", "GO", "pay it", "t1", dueAt);

        var snooze = Assert.Single(_snoozes);
        Assert.Equal("personal", snooze.PartitionKey);
        Assert.Equal("m1", snooze.RowKey);
        Assert.Equal("GO bill", snooze.Subject);
        Assert.Equal("GO", snooze.SenderName);
        Assert.Equal("pay it", snooze.Summary);
        Assert.Equal("t1", snooze.ThreadId);
        Assert.Equal(dueAt, snooze.DueAt);
    }

    [Fact]
    public async Task GetDueSnoozes_ReturnsOnlyThoseAlreadyDue()
    {
        var now = DateTimeOffset.UtcNow;
        _snoozes.Add(new SnoozedEmailEntity { PartitionKey = "personal", RowKey = "due", DueAt = now.AddMinutes(-1) });
        _snoozes.Add(new SnoozedEmailEntity { PartitionKey = "personal", RowKey = "future", DueAt = now.AddHours(2) });

        var due = await _service.GetDueSnoozesAsync(now);

        Assert.Equal("due", Assert.Single(due).RowKey);
    }

    [Fact]
    public async Task GetSnoozes_ListsAllSortedByDueTime()
    {
        var now = DateTimeOffset.UtcNow;
        _snoozes.Add(new SnoozedEmailEntity { PartitionKey = "personal", RowKey = "later", DueAt = now.AddDays(2) });
        _snoozes.Add(new SnoozedEmailEntity { PartitionKey = "personal", RowKey = "sooner", DueAt = now.AddHours(1) });

        var all = await _service.GetSnoozesAsync();

        Assert.Equal(new[] { "sooner", "later" }, all.Select(s => s.RowKey).ToArray());
    }

    [Fact]
    public async Task DeleteSnooze_RemovesTheRow()
    {
        _snoozes.Add(new SnoozedEmailEntity { PartitionKey = "personal", RowKey = "m1" });

        await _service.DeleteSnoozeAsync("m1");

        Assert.Empty(_snoozes);
    }

    // ---- Suppression and attention rules ----

    [Fact]
    public async Task SuppressionRules_SaveListDeleteRoundTrip()
    {
        await _service.SaveSuppressionRuleAsync("r1", "Monthly Bolt reports", "reports@bolt.eu", "July report");

        var rule = Assert.Single(await _service.GetSuppressionRulesAsync());
        Assert.Equal("r1", rule.RowKey);
        Assert.Equal("rules", rule.PartitionKey);
        Assert.Equal("Monthly Bolt reports", rule.Pattern);
        Assert.Equal("reports@bolt.eu", rule.ExampleSender);
        Assert.Equal("July report", rule.ExampleSubject);

        await _service.DeleteSuppressionRuleAsync("r1");
        Assert.Empty(await _service.GetSuppressionRulesAsync());
    }

    [Fact]
    public async Task AttentionRules_SaveListDeleteRoundTrip()
    {
        await _service.SaveAttentionRuleAsync("a1", "Anything from HSBC", null, null);

        var rule = Assert.Single(await _service.GetAttentionRulesAsync());
        Assert.Equal("a1", rule.RowKey);
        Assert.Equal("Anything from HSBC", rule.Pattern);

        await _service.DeleteAttentionRuleAsync("a1");
        Assert.Empty(await _service.GetAttentionRulesAsync());
    }

    // ---- Sender stats ----

    [Fact]
    public async Task RecordSenderSeen_NewSender_StartsTheTally()
    {
        await _service.RecordSenderSeenAsync("news@shop.com", "Shop", wasQuiet: true, "<mailto:u@shop.com>", oneClick: true);

        var stats = Assert.Single(_senderStats);
        Assert.Equal(SenderStatsEntity.RowKeyFor("news@shop.com"), stats.RowKey);
        Assert.Equal("news@shop.com", stats.SenderEmail);
        Assert.Equal("Shop", stats.SenderName);
        Assert.Equal(1, stats.TotalCount);
        Assert.Equal(1, stats.QuietCount);
        Assert.Equal("<mailto:u@shop.com>", stats.ListUnsubscribe);
        Assert.True(stats.ListUnsubscribeOneClick);
    }

    [Fact]
    public async Task RecordSenderSeen_ExistingSender_IncrementsAndKeepsTheUnsubscribeHeader()
    {
        await _service.RecordSenderSeenAsync("news@shop.com", "Shop", wasQuiet: true, "<mailto:u@shop.com>", oneClick: false);
        // Second email arrives without a List-Unsubscribe header and needed attention
        await _service.RecordSenderSeenAsync("news@shop.com", "Shop Renamed", wasQuiet: false, null, oneClick: false);

        var stats = Assert.Single(_senderStats);
        Assert.Equal(2, stats.TotalCount);
        Assert.Equal(1, stats.QuietCount);
        Assert.Equal("Shop Renamed", stats.SenderName);
        Assert.Equal("<mailto:u@shop.com>", stats.ListUnsubscribe); // earlier header survives
    }

    [Fact]
    public async Task UnsubscribeCandidates_RequireVolumeSilenceAMechanismAndNoPriorProposal()
    {
        SenderStatsEntity Stat(string email, int total, int quiet, string? unsub = "<mailto:u@x.com>",
            DateTimeOffset? proposedAt = null, bool unsubscribed = false) =>
            new()
            {
                PartitionKey = "personal",
                RowKey = SenderStatsEntity.RowKeyFor(email),
                SenderEmail = email,
                TotalCount = total,
                QuietCount = quiet,
                ListUnsubscribe = unsub,
                ProposedAt = proposedAt,
                Unsubscribed = unsubscribed
            };

        _senderStats.Add(Stat("big@list.com", 9, 9));                                  // candidate
        _senderStats.Add(Stat("small@list.com", 3, 3));                                // candidate, lower volume
        _senderStats.Add(Stat("useful@list.com", 8, 7));                               // once useful -> excluded
        _senderStats.Add(Stat("nomech@list.com", 8, 8, unsub: null));                  // no unsubscribe mechanism
        _senderStats.Add(Stat("proposed@list.com", 8, 8, proposedAt: DateTimeOffset.UtcNow)); // already proposed
        _senderStats.Add(Stat("toofew@list.com", 2, 2));                               // below the minimum
        _senderStats.Add(Stat("gone@list.com", 9, 9, unsubscribed: true));             // already unsubscribed

        var candidates = await _service.GetUnsubscribeCandidatesAsync(minEmails: 3, maxCandidates: 5);

        Assert.Equal(new[] { "big@list.com", "small@list.com" }, candidates.Select(c => c.SenderEmail).ToArray());

        // The cap keeps only the highest-volume senders
        var capped = await _service.GetUnsubscribeCandidatesAsync(minEmails: 3, maxCandidates: 1);
        Assert.Equal("big@list.com", Assert.Single(capped).SenderEmail);
    }

    [Fact]
    public async Task GetSenderStat_ReturnsTheRowOrNull()
    {
        _senderStats.Add(new SenderStatsEntity { PartitionKey = "personal", RowKey = "s1", SenderName = "Shop" });

        Assert.Equal("Shop", (await _service.GetSenderStatAsync("s1"))?.SenderName);
        Assert.Null(await _service.GetSenderStatAsync("ghost"));
    }

    // ---- Chat history ----

    [Fact]
    public async Task SaveChatTurn_UsesInvertedTicksSoNewestSortsFirst()
    {
        await _service.SaveChatTurnAsync(777, "question?", "answer.");

        var turn = Assert.Single(_chatTurns);
        Assert.Equal("777", turn.PartitionKey);
        Assert.Equal("question?", turn.Question);
        Assert.Equal("answer.", turn.Answer);
        Assert.Equal(19, turn.RowKey.Length);
        Assert.All(turn.RowKey, c => Assert.True(char.IsDigit(c)));
        // The row key must decode back to the AskedAt instant (inverted ticks)
        Assert.Equal(turn.AskedAt.UtcTicks, long.MaxValue - long.Parse(turn.RowKey));
    }

    [Fact]
    public async Task SaveChatTurn_PrunesTurnsOlderThanADay()
    {
        _chatTurns.Add(new ChatTurnEntity { PartitionKey = "777", RowKey = "stale", AskedAt = DateTimeOffset.UtcNow.AddDays(-2) });
        _chatTurns.Add(new ChatTurnEntity { PartitionKey = "777", RowKey = "fresh", AskedAt = DateTimeOffset.UtcNow.AddHours(-2) });

        await _service.SaveChatTurnAsync(777, "q", "a");

        Assert.DoesNotContain(_chatTurns, t => t.RowKey == "stale");
        Assert.Contains(_chatTurns, t => t.RowKey == "fresh");
        Assert.Equal(2, _chatTurns.Count); // fresh + the new turn
    }

    [Fact]
    public async Task GetRecentChatTurns_CapsTheCountAndReturnsChronologicalOrder()
    {
        var now = DateTimeOffset.UtcNow;
        // Stored newest-first, mirroring the inverted-ticks row key order Azure returns
        _chatTurns.Add(new ChatTurnEntity { PartitionKey = "777", RowKey = "3", Question = "third", AskedAt = now.AddMinutes(-5) });
        _chatTurns.Add(new ChatTurnEntity { PartitionKey = "777", RowKey = "2", Question = "second", AskedAt = now.AddMinutes(-10) });
        _chatTurns.Add(new ChatTurnEntity { PartitionKey = "777", RowKey = "1", Question = "first", AskedAt = now.AddMinutes(-15) });

        var turns = await _service.GetRecentChatTurnsAsync(777, now.AddHours(-1), maxCount: 2);

        Assert.Equal(new[] { "second", "third" }, turns.Select(t => t.Question).ToArray());
    }

    [Fact]
    public async Task GetRecentChatTurns_IgnoresOtherChatsAndOldTurns()
    {
        var now = DateTimeOffset.UtcNow;
        _chatTurns.Add(new ChatTurnEntity { PartitionKey = "777", RowKey = "1", Question = "mine", AskedAt = now.AddMinutes(-5) });
        _chatTurns.Add(new ChatTurnEntity { PartitionKey = "555", RowKey = "2", Question = "other chat", AskedAt = now.AddMinutes(-5) });
        _chatTurns.Add(new ChatTurnEntity { PartitionKey = "777", RowKey = "3", Question = "expired", AskedAt = now.AddHours(-3) });

        var turns = await _service.GetRecentChatTurnsAsync(777, now.AddHours(-1), maxCount: 10);

        Assert.Equal("mine", Assert.Single(turns).Question);
    }

    [Fact]
    public async Task ClearChatTurns_RemovesOnlyThatChat()
    {
        _chatTurns.Add(new ChatTurnEntity { PartitionKey = "777", RowKey = "1" });
        _chatTurns.Add(new ChatTurnEntity { PartitionKey = "777", RowKey = "2" });
        _chatTurns.Add(new ChatTurnEntity { PartitionKey = "555", RowKey = "3" });

        await _service.ClearChatTurnsAsync(777);

        Assert.Equal("555", Assert.Single(_chatTurns).PartitionKey);
    }

    // ---- Calendar event mappings ----

    [Fact]
    public async Task CalendarEventMapping_SaveGetDeleteRoundTrip()
    {
        var eventDate = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
        await _service.SaveCalendarEventMappingAsync("hash1", "google-ev-1", "m1", "Outing: Zoo", eventDate);

        var mapping = await _service.GetCalendarEventMappingAsync("hash1");
        Assert.NotNull(mapping);
        Assert.Equal("google-ev-1", mapping.GoogleEventId);
        Assert.Equal("m1", mapping.OriginalEmailId);
        Assert.Equal("Outing: Zoo", mapping.Title);
        Assert.Equal(eventDate, mapping.EventDate);

        await _service.DeleteCalendarEventMappingAsync("hash1");
        Assert.Null(await _service.GetCalendarEventMappingAsync("hash1"));
    }

    // ---- Backfill marker ----

    [Fact]
    public async Task BackfillState_SaveGetClearRoundTrip()
    {
        Assert.Null(await _service.GetBackfillStateAsync());

        var state = new BackfillStateEntity
        {
            OldestDate = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            ProcessedCount = 12
        };
        await _service.SaveBackfillStateAsync(state);

        var loaded = await _service.GetBackfillStateAsync();
        Assert.NotNull(loaded);
        Assert.Equal(12, loaded.ProcessedCount);
        Assert.Equal(state.OldestDate, loaded.OldestDate);

        await _service.ClearBackfillStateAsync();
        Assert.Null(await _service.GetBackfillStateAsync());
    }
}
