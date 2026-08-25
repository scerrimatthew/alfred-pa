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
    private readonly List<NewsRuleEntity> _newsRules = [];
    private readonly List<UserFactEntity> _userFacts = [];
    private readonly List<ReportedNewsEntity> _reportedNews = [];
    private readonly List<NewsCandidateEntity> _newsCandidates = [];
    private readonly List<NewsRequestStateEntity> _newsRequests = [];
    private readonly List<ProcessedUpdateEntity> _processedUpdates = [];
    // The EtfHoldings table holds two entity shapes — the watchlist rows in the "etfs"
    // partition and the one-off onboarding-nudge marker in "meta" — so both share one
    // backing store, exactly like the real table where only the partition-scoped query
    // keeps the marker out of the watchlist
    private readonly List<ITableEntity> _etfTable = [];

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
        var newsRulesClient = CreateTableClient(_newsRules);
        var userFactsClient = CreateTableClient(_userFacts);
        var reportedNewsClient = CreateTableClient(_reportedNews);
        var newsCandidatesClient = CreateTableClient(_newsCandidates);
        var newsRequestsClient = CreateTableClient(_newsRequests);
        var processedUpdatesClient = CreateTableClient(_processedUpdates);
        var etfHoldingsClient = Substitute.For<TableClient>();
        ConfigureStore<EtfHoldingEntity>(etfHoldingsClient, _etfTable);
        ConfigureStore<EtfNudgeEntity>(etfHoldingsClient, _etfTable);

        var serviceClient = Substitute.For<TableServiceClient>();
        serviceClient.GetTableClient("ProcessedEmails").Returns(processedEmailsClient);
        serviceClient.GetTableClient("SnoozedEmails").Returns(snoozesClient);
        serviceClient.GetTableClient("SenderStats").Returns(senderStatsClient);
        serviceClient.GetTableClient("ChatHistory").Returns(chatTurnsClient);
        serviceClient.GetTableClient("SuppressionRules").Returns(suppressionRulesClient);
        serviceClient.GetTableClient("AttentionRules").Returns(attentionRulesClient);
        serviceClient.GetTableClient("CalendarEvents").Returns(calendarEventsClient);
        serviceClient.GetTableClient("BackfillState").Returns(backfillStateClient);
        serviceClient.GetTableClient("NewsRules").Returns(newsRulesClient);
        serviceClient.GetTableClient("UserFacts").Returns(userFactsClient);
        serviceClient.GetTableClient("ReportedNews").Returns(reportedNewsClient);
        serviceClient.GetTableClient("NewsCandidates").Returns(newsCandidatesClient);
        serviceClient.GetTableClient("NewsRequests").Returns(newsRequestsClient);
        serviceClient.GetTableClient("ProcessedUpdates").Returns(processedUpdatesClient);
        serviceClient.GetTableClient("EtfHoldings").Returns(etfHoldingsClient);

        _service = new TableStorageStateService(serviceClient, NullLogger<TableStorageStateService>.Instance);
    }

    // Configures one entity shape on a table client backed by a mixed store, so several
    // shapes can share a table the way EtfHoldings does. Row identity is by partition +
    // row key across ALL shapes (the real table has no idea about .NET types), and a query
    // sees every row projected into the queried shape — so a predicate that forgot its
    // partition filter would pick up the other shape's rows, just like in production.
    private static void ConfigureStore<T>(TableClient client, List<ITableEntity> store)
        where T : class, ITableEntity, new()
    {
        client.QueryAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<int?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var filter = ci.Arg<Expression<Func<T, bool>>>().Compile();
                var rows = store
                    .Select(e => e as T ?? new T { PartitionKey = e.PartitionKey, RowKey = e.RowKey })
                    .Where(filter)
                    .ToList();
                var page = Page<T>.FromValues(rows, null, Substitute.For<Response>());
                return AsyncPageable<T>.FromPages([page]);
            });

        client.GetEntityAsync<T>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var found = store.OfType<T>().FirstOrDefault(
                    e => e.PartitionKey == ci.ArgAt<string>(0) && e.RowKey == ci.ArgAt<string>(1));
                return found is not null
                    ? Response.FromValue(found, Substitute.For<Response>())
                    : throw new RequestFailedException(404, "Not Found");
            });

        client.AddEntityAsync(Arg.Any<T>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var entity = ci.Arg<T>();
                if (store.Any(e => e.PartitionKey == entity.PartitionKey && e.RowKey == entity.RowKey))
                    throw new RequestFailedException(409, "Conflict");
                store.Add(entity);
                return Substitute.For<Response>();
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
                // Like the real table: deleting a row that isn't there is a 404, not a no-op
                if (store.RemoveAll(e => e.PartitionKey == ci.ArgAt<string>(0) && e.RowKey == ci.ArgAt<string>(1)) == 0)
                    throw new RequestFailedException(404, "Not Found");
                return Substitute.For<Response>();
            });
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

        client.AddEntityAsync(Arg.Any<T>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var entity = ci.Arg<T>();
                // Add-if-absent, like the real table: an existing row means 409 Conflict
                if (store.Any(e => e.PartitionKey == entity.PartitionKey && e.RowKey == entity.RowKey))
                    throw new RequestFailedException(409, "Conflict");
                store.Add(entity);
                return Substitute.For<Response>();
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
                // Like the real table: deleting a row that isn't there is a 404, not a no-op
                if (store.RemoveAll(e => e.PartitionKey == ci.ArgAt<string>(0) && e.RowKey == ci.ArgAt<string>(1)) == 0)
                    throw new RequestFailedException(404, "Not Found");
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

    // ---- AI news rules and reported stories ----

    [Fact]
    public async Task NewsRules_SaveListDeleteRoundTrip()
    {
        await _service.SaveNewsRuleAsync("n1", "Skip funding rounds");

        var rule = Assert.Single(await _service.GetNewsRulesAsync());
        Assert.Equal("n1", rule.RowKey);
        Assert.Equal("rules", rule.PartitionKey);
        Assert.Equal("Skip funding rounds", rule.Instruction);
        Assert.True((DateTimeOffset.UtcNow - rule.CreatedAt).Duration() < TimeSpan.FromMinutes(1));

        await _service.DeleteNewsRuleAsync("n1");
        Assert.Empty(await _service.GetNewsRulesAsync());
    }

    // ---- User facts ----

    [Fact]
    public async Task UserFacts_SaveListDeleteRoundTrip()
    {
        await _service.SaveUserFactAsync("f1", "Matthew's apartment at Hillcrest is A5 in Block A.");

        var fact = Assert.Single(await _service.GetUserFactsAsync());
        Assert.Equal("f1", fact.RowKey);
        Assert.Equal("facts", fact.PartitionKey);
        Assert.Equal("Matthew's apartment at Hillcrest is A5 in Block A.", fact.Fact);
        Assert.True((DateTimeOffset.UtcNow - fact.CreatedAt).Duration() < TimeSpan.FromMinutes(1));

        await _service.DeleteUserFactAsync("f1");
        Assert.Empty(await _service.GetUserFactsAsync());
    }

    [Fact]
    public async Task SaveUserFact_SameId_OverwritesInsteadOfDuplicating()
    {
        // The upsert semantics let a corrected fact replace the old one under its id
        await _service.SaveUserFactAsync("f1", "The apartment is in Block B.");
        await _service.SaveUserFactAsync("f1", "The apartment is in Block A.");

        var fact = Assert.Single(_userFacts);
        Assert.Equal("The apartment is in Block A.", fact.Fact);
    }

    [Fact]
    public async Task GetUserFacts_IgnoresRowsOutsideTheFactsPartition()
    {
        _userFacts.Add(new UserFactEntity { PartitionKey = "facts", RowKey = "mine", Fact = "Keep me." });
        _userFacts.Add(new UserFactEntity { PartitionKey = "other", RowKey = "stray", Fact = "Not a fact row." });

        var results = await _service.GetUserFactsAsync();

        Assert.Equal("mine", Assert.Single(results).RowKey);
    }

    [Fact]
    public async Task GetReportedNewsSince_AppliesPartitionAndDateWindow()
    {
        var now = DateTimeOffset.UtcNow;
        _reportedNews.Add(new ReportedNewsEntity { PartitionKey = "news", RowKey = "recent", ReportedAt = now.AddDays(-3) });
        _reportedNews.Add(new ReportedNewsEntity { PartitionKey = "news", RowKey = "stale", ReportedAt = now.AddDays(-20) });
        _reportedNews.Add(new ReportedNewsEntity { PartitionKey = "other", RowKey = "wrong-partition", ReportedAt = now.AddDays(-3) });

        var results = await _service.GetReportedNewsSinceAsync(now.AddDays(-14));

        Assert.Equal("recent", Assert.Single(results).RowKey);
    }

    [Fact]
    public async Task SaveReportedNews_KeysRowsByUrlHashSoRepeatsOverwrite()
    {
        await _service.SaveReportedNewsAsync(
        [
            new AiNewsItem { Headline = "First", Url = "https://a.example/story", Category = "competitor" },
            new AiNewsItem { Headline = "Second", Url = "https://b.example/story" }
        ]);
        // Re-reporting the same story must overwrite the row, not duplicate it
        await _service.SaveReportedNewsAsync(
            [new AiNewsItem { Headline = "First, updated", Url = "https://a.example/story" }]);

        Assert.Equal(2, _reportedNews.Count);

        var first = _reportedNews.Single(e => e.Url == "https://a.example/story");
        Assert.Equal(TableStorageStateService.HashUrl("https://a.example/story"), first.RowKey);
        Assert.Equal("news", first.PartitionKey);
        Assert.Equal("First, updated", first.Headline);
        Assert.True((DateTimeOffset.UtcNow - first.ReportedAt).Duration() < TimeSpan.FromMinutes(1));

        var second = _reportedNews.Single(e => e.Url == "https://b.example/story");
        Assert.Equal("Second", second.Headline);
        Assert.Null(second.Category); // optional category stays null
    }

    [Fact]
    public async Task SaveReportedNews_PrunesStoriesOlderThanSixtyDays()
    {
        var now = DateTimeOffset.UtcNow;
        _reportedNews.Add(new ReportedNewsEntity { PartitionKey = "news", RowKey = "ancient", ReportedAt = now.AddDays(-90) });
        _reportedNews.Add(new ReportedNewsEntity { PartitionKey = "news", RowKey = "keeper", ReportedAt = now.AddDays(-10) });

        await _service.SaveReportedNewsAsync([new AiNewsItem { Headline = "H", Url = "https://new.example" }]);

        Assert.DoesNotContain(_reportedNews, e => e.RowKey == "ancient");
        Assert.Contains(_reportedNews, e => e.RowKey == "keeper");
        Assert.Equal(2, _reportedNews.Count); // keeper + the new story
    }

    [Fact]
    public async Task SaveReportedNews_PersistsTheSummaryAndWhyItMatters()
    {
        await _service.SaveReportedNewsAsync(
        [
            new AiNewsItem
            {
                Headline = "DORA lands", Url = "https://d.example", Category = "thesis-evidence",
                Summary = "Review times doubled.", WhyItMatters = "Core evidence for the bet."
            }
        ]);

        var entity = Assert.Single(_reportedNews);
        Assert.Equal("Review times doubled.", entity.Summary);
        Assert.Equal("Core evidence for the bet.", entity.WhyItMatters);
    }

    [Fact]
    public async Task GetReportedNews_ReturnsTheRowOrNull()
    {
        _reportedNews.Add(new ReportedNewsEntity { PartitionKey = "news", RowKey = "abc123", Headline = "DORA lands" });

        Assert.Equal("DORA lands", (await _service.GetReportedNewsAsync("abc123"))?.Headline);
        Assert.Null(await _service.GetReportedNewsAsync("ghost"));
    }

    [Fact]
    public void HashUrl_IsDeterministicSixteenCharLowercaseHex()
    {
        var hash = TableStorageStateService.HashUrl("https://example.com/story");

        Assert.Equal(16, hash.Length);
        Assert.Matches("^[0-9a-f]{16}$", hash);
        Assert.Equal(hash, TableStorageStateService.HashUrl("https://example.com/story"));
        Assert.NotEqual(hash, TableStorageStateService.HashUrl("https://example.com/other"));
    }

    // ---- News candidates (newsletter-mined leads) ----

    [Fact]
    public async Task SaveNewsCandidates_KeysByUrlHash_OrHeadlineHashWhenThereIsNoUrl()
    {
        await _service.SaveNewsCandidatesAsync(
        [
            new NewsCandidateEntity { Headline = "With url", Url = "https://a.example/1", Note = "n", Source = "TLDR AI" },
            new NewsCandidateEntity { Headline = "No url at all", Url = null, Source = "Import AI" },
            new NewsCandidateEntity { Headline = "Blank url", Url = "   ", Source = "Import AI" }
        ]);

        Assert.Equal(3, _newsCandidates.Count);
        Assert.All(_newsCandidates, c => Assert.Equal("news", c.PartitionKey));
        Assert.All(_newsCandidates, c =>
            Assert.True((DateTimeOffset.UtcNow - c.SeenAt).Duration() < TimeSpan.FromMinutes(1),
                "SeenAt must be stamped at save time"));

        Assert.Equal(TableStorageStateService.HashUrl("https://a.example/1"),
            _newsCandidates.Single(c => c.Headline == "With url").RowKey);
        Assert.Equal(TableStorageStateService.HashUrl("No url at all"),
            _newsCandidates.Single(c => c.Headline == "No url at all").RowKey);
        Assert.Equal(TableStorageStateService.HashUrl("Blank url"),
            _newsCandidates.Single(c => c.Headline == "Blank url").RowKey);
    }

    [Fact]
    public async Task SaveNewsCandidates_SameStoryFromTwoNewsletters_LandsOnce()
    {
        await _service.SaveNewsCandidatesAsync(
            [new NewsCandidateEntity { Headline = "First wording", Url = "https://a.example/1", Source = "TLDR AI" }]);
        await _service.SaveNewsCandidatesAsync(
            [new NewsCandidateEntity { Headline = "Second wording", Url = "https://a.example/1", Source = "Import AI" }]);

        var candidate = Assert.Single(_newsCandidates);
        Assert.Equal("Second wording", candidate.Headline);
        Assert.Equal("Import AI", candidate.Source);
    }

    [Fact]
    public async Task SaveNewsCandidates_PrunesLeadsOlderThanAWeek()
    {
        _newsCandidates.Add(new NewsCandidateEntity { PartitionKey = "news", RowKey = "stale", SeenAt = DateTimeOffset.UtcNow.AddDays(-10) });
        _newsCandidates.Add(new NewsCandidateEntity { PartitionKey = "news", RowKey = "fresh", SeenAt = DateTimeOffset.UtcNow.AddDays(-2) });

        await _service.SaveNewsCandidatesAsync([new NewsCandidateEntity { Headline = "New lead" }]);

        Assert.DoesNotContain(_newsCandidates, c => c.RowKey == "stale");
        Assert.Contains(_newsCandidates, c => c.RowKey == "fresh");
        Assert.Equal(2, _newsCandidates.Count); // fresh + the new lead
    }

    [Fact]
    public async Task GetNewsCandidatesSince_AppliesPartitionAndDateWindow()
    {
        var now = DateTimeOffset.UtcNow;
        _newsCandidates.Add(new NewsCandidateEntity { PartitionKey = "news", RowKey = "recent", SeenAt = now.AddHours(-2) });
        _newsCandidates.Add(new NewsCandidateEntity { PartitionKey = "news", RowKey = "old", SeenAt = now.AddDays(-3) });
        _newsCandidates.Add(new NewsCandidateEntity { PartitionKey = "other", RowKey = "wrong-partition", SeenAt = now.AddHours(-2) });

        var results = await _service.GetNewsCandidatesSinceAsync(now.AddHours(-26));

        Assert.Equal("recent", Assert.Single(results).RowKey);
    }

    // ---- /news in-flight marker ----

    [Fact]
    public async Task NewsRequest_SaveGetClearRoundTrip()
    {
        Assert.Null(await _service.GetNewsRequestAsync());

        var requestedAt = DateTimeOffset.UtcNow;
        await _service.SaveNewsRequestAsync(new NewsRequestStateEntity { RequestedAt = requestedAt, Topic = "EU AI Act" });

        var loaded = await _service.GetNewsRequestAsync();
        Assert.NotNull(loaded);
        Assert.Equal(requestedAt, loaded.RequestedAt);
        Assert.Equal("EU AI Act", loaded.Topic);

        await _service.ClearNewsRequestAsync();
        Assert.Null(await _service.GetNewsRequestAsync());
    }

    [Fact]
    public async Task ClearNewsRequest_WithoutAMarker_IsANoOp()
    {
        await _service.ClearNewsRequestAsync();

        Assert.Empty(_newsRequests);
    }

    [Fact]
    public async Task SaveNewsRequest_ReplacesTheSingleMarkerRow()
    {
        await _service.SaveNewsRequestAsync(new NewsRequestStateEntity { RequestedAt = DateTimeOffset.UtcNow.AddMinutes(-20), Topic = "old" });
        await _service.SaveNewsRequestAsync(new NewsRequestStateEntity { RequestedAt = DateTimeOffset.UtcNow, Topic = null });

        var marker = Assert.Single(_newsRequests);
        Assert.Null(marker.Topic);
    }

    // ---- Telegram update dedup claims ----

    [Fact]
    public async Task TryClaimUpdate_FirstClaim_SucceedsAndStampsTheClaim()
    {
        var claimed = await _service.TryClaimUpdateAsync(123456789);

        Assert.True(claimed);
        var claim = Assert.Single(_processedUpdates);
        Assert.Equal("personal", claim.PartitionKey);
        Assert.Equal("123456789", claim.RowKey);
        Assert.True((DateTimeOffset.UtcNow - claim.ClaimedAt).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task TryClaimUpdate_DuplicateDelivery_IsRefused()
    {
        Assert.True(await _service.TryClaimUpdateAsync(42));
        Assert.False(await _service.TryClaimUpdateAsync(42));

        // The original claim survives untouched
        Assert.Single(_processedUpdates);
    }

    [Fact]
    public async Task TryClaimUpdate_DifferentUpdates_ClaimIndependently()
    {
        Assert.True(await _service.TryClaimUpdateAsync(1));
        Assert.True(await _service.TryClaimUpdateAsync(2));

        Assert.Equal(2, _processedUpdates.Count);
    }

    [Fact]
    public async Task TryClaimUpdate_SuccessfulClaim_PrunesDayOldClaims()
    {
        _processedUpdates.Add(new ProcessedUpdateEntity { PartitionKey = "personal", RowKey = "stale", ClaimedAt = DateTimeOffset.UtcNow.AddDays(-2) });
        _processedUpdates.Add(new ProcessedUpdateEntity { PartitionKey = "personal", RowKey = "fresh", ClaimedAt = DateTimeOffset.UtcNow.AddHours(-2) });

        Assert.True(await _service.TryClaimUpdateAsync(99));

        Assert.DoesNotContain(_processedUpdates, c => c.RowKey == "stale");
        Assert.Contains(_processedUpdates, c => c.RowKey == "fresh");
        Assert.Equal(2, _processedUpdates.Count); // fresh + the new claim
    }

    [Fact]
    public async Task TryClaimUpdate_RefusedClaim_DoesNotPrune()
    {
        // A duplicate delivery is the hot path — it must return fast, without a table sweep
        _processedUpdates.Add(new ProcessedUpdateEntity { PartitionKey = "personal", RowKey = "stale", ClaimedAt = DateTimeOffset.UtcNow.AddDays(-2) });
        _processedUpdates.Add(new ProcessedUpdateEntity { PartitionKey = "personal", RowKey = "42", ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });

        Assert.False(await _service.TryClaimUpdateAsync(42));

        Assert.Contains(_processedUpdates, c => c.RowKey == "stale"); // untouched
        Assert.Equal(2, _processedUpdates.Count);
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

    // ---- ETF watchlist ----

    [Theory]
    [InlineData("vwce", "VWCE")]
    [InlineData("  VWCE  ", "VWCE")]
    [InlineData("sxr8.de", "SXR8.DE")]
    [InlineData("brk-b", "BRK-B")]
    [InlineData("etf_1", "ETF_1")]
    [InlineData("VW CE", "VWCE")]        // spaces a row key can't hold are stripped
    [InlineData("VW/CE", "VWCE")]        // ...as are the characters Table Storage rejects
    [InlineData("$$$", "UNKNOWN")]       // nothing usable left
    [InlineData("", "UNKNOWN")]
    [InlineData("   ", "UNKNOWN")]
    public void EtfKey_NormalizesTickersSoTheSameFundLandsOnOneRow(string symbol, string expected)
    {
        Assert.Equal(expected, TableStorageStateService.EtfKey(symbol));
    }

    [Fact]
    public async Task SaveEtfHolding_StoresTheTickerAsTypedUnderItsNormalizedKey()
    {
        await _service.SaveEtfHoldingAsync(" vwce ", " Vanguard FTSE All-World ", " core holding ");

        var saved = Assert.Single(_etfTable.OfType<EtfHoldingEntity>());
        Assert.Equal("etfs", saved.PartitionKey);
        Assert.Equal("VWCE", saved.RowKey);
        Assert.Equal("vwce", saved.Symbol);
        Assert.Equal("Vanguard FTSE All-World", saved.Name);
        Assert.Equal("core holding", saved.Notes);
        Assert.True((DateTimeOffset.UtcNow - saved.CreatedAt).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task SaveEtfHolding_ReAdding_KeepsTheOriginalDateAndTheReportedHistory()
    {
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var reportedAt = new DateTimeOffset(2026, 8, 8, 9, 0, 0, TimeSpan.Zero);
        _etfTable.Add(new EtfHoldingEntity
        {
            RowKey = "VWCE", Symbol = "VWCE", Name = "Vanguard FTSE All-World", Notes = "core holding",
            CreatedAt = createdAt, LastQuote = "€128.42", LastWeekChangePercent = -1.4, LastReportedAt = reportedAt
        });

        // Re-added from chat with only a note this time
        await _service.SaveEtfHoldingAsync("VWCE", null, "monthly DCA");

        var saved = Assert.Single(_etfTable.OfType<EtfHoldingEntity>());
        Assert.Equal(createdAt, saved.CreatedAt);          // watchlist order is preserved
        Assert.Equal("Vanguard FTSE All-World", saved.Name); // not blanked by the omitted argument
        Assert.Equal("monthly DCA", saved.Notes);            // the new note wins
        Assert.Equal("€128.42", saved.LastQuote);            // last week's snapshot survives
        Assert.Equal(-1.4, saved.LastWeekChangePercent);
        Assert.Equal(reportedAt, saved.LastReportedAt);
    }

    [Fact]
    public async Task GetEtfHoldings_ReturnsOnlyTheEtfPartition()
    {
        _etfTable.Add(new EtfHoldingEntity { RowKey = "VWCE", Symbol = "VWCE" });
        _etfTable.Add(new EtfHoldingEntity { PartitionKey = "something-else", RowKey = "IWDA", Symbol = "IWDA" });

        var holdings = await _service.GetEtfHoldingsAsync();

        Assert.Equal("VWCE", Assert.Single(holdings).Symbol);
    }

    [Fact]
    public async Task DeleteEtfHolding_RemovesTheNormalizedRow()
    {
        _etfTable.Add(new EtfHoldingEntity { RowKey = "VWCE", Symbol = "VWCE" });
        _etfTable.Add(new EtfHoldingEntity { RowKey = "IWDA", Symbol = "IWDA" });

        await _service.DeleteEtfHoldingAsync(" vwce ");

        Assert.Equal("IWDA", Assert.Single(_etfTable.OfType<EtfHoldingEntity>()).RowKey);
    }

    [Fact]
    public async Task DeleteEtfHolding_ThatIsNotTracked_IsANoOp()
    {
        await _service.DeleteEtfHoldingAsync("VWCE");

        Assert.Empty(_etfTable);
    }

    [Fact]
    public async Task SaveEtfSnapshots_RecordsThisWeeksFiguresOnTheTrackedFunds()
    {
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _etfTable.Add(new EtfHoldingEntity { RowKey = "VWCE", Symbol = "VWCE", CreatedAt = createdAt });

        await _service.SaveEtfSnapshotsAsync([
            new EtfPerformance { Symbol = "vwce", Name = "Vanguard FTSE All-World", Quote = "€128.42", WeekChangePercent = -1.4 }
        ]);

        var saved = Assert.Single(_etfTable.OfType<EtfHoldingEntity>());
        Assert.Equal("€128.42", saved.LastQuote);
        Assert.Equal(-1.4, saved.LastWeekChangePercent);
        Assert.NotNull(saved.LastReportedAt);
        Assert.True((DateTimeOffset.UtcNow - saved.LastReportedAt!.Value).Duration() < TimeSpan.FromMinutes(1));
        // A name learned during research fills the blank...
        Assert.Equal("Vanguard FTSE All-World", saved.Name);
        Assert.Equal(createdAt, saved.CreatedAt);
    }

    [Fact]
    public async Task SaveEtfSnapshots_NeverOverwritesANameMatthewChose()
    {
        _etfTable.Add(new EtfHoldingEntity { RowKey = "VWCE", Symbol = "VWCE", Name = "My world tracker" });

        await _service.SaveEtfSnapshotsAsync([
            new EtfPerformance { Symbol = "VWCE", Name = "Vanguard FTSE All-World UCITS ETF" }
        ]);

        Assert.Equal("My world tracker", Assert.Single(_etfTable.OfType<EtfHoldingEntity>()).Name);
    }

    [Fact]
    public async Task SaveEtfSnapshots_IgnoresSymbolsThatArentTracked()
    {
        _etfTable.Add(new EtfHoldingEntity { RowKey = "VWCE", Symbol = "VWCE" });

        // An ad-hoc "/etf VUSA" must not add VUSA to the watchlist behind Matthew's back
        await _service.SaveEtfSnapshotsAsync([
            new EtfPerformance { Symbol = "VWCE", Quote = "€128.42" },
            new EtfPerformance { Symbol = "VUSA", Quote = "$95.10" }
        ]);

        var saved = Assert.Single(_etfTable.OfType<EtfHoldingEntity>());
        Assert.Equal("VWCE", saved.RowKey);
        Assert.Equal("€128.42", saved.LastQuote);
    }

    [Fact]
    public async Task SaveEtfSnapshots_WithNothingToRecord_LeavesTheTableAlone()
    {
        await _service.SaveEtfSnapshotsAsync([]);

        Assert.Empty(_etfTable);
    }

    [Fact]
    public async Task EtfRequest_SaveGetClearRoundTrip_OnItsOwnRowKey()
    {
        Assert.Null(await _service.GetEtfRequestAsync());

        var requestedAt = DateTimeOffset.UtcNow;
        await _service.SaveEtfRequestAsync(new NewsRequestStateEntity { RequestedAt = requestedAt });

        var loaded = await _service.GetEtfRequestAsync();
        Assert.NotNull(loaded);
        Assert.Equal(requestedAt, loaded.RequestedAt);
        Assert.Equal("etf-request", Assert.Single(_newsRequests).RowKey);

        await _service.ClearEtfRequestAsync();
        Assert.Null(await _service.GetEtfRequestAsync());
    }

    [Fact]
    public async Task EtfAndNewsRequests_AreIndependentMarkers()
    {
        // A research sweep for one must never look in flight — or be cleared — by the other
        await _service.SaveNewsRequestAsync(new NewsRequestStateEntity { RequestedAt = DateTimeOffset.UtcNow, Topic = "EU AI Act" });
        await _service.SaveEtfRequestAsync(new NewsRequestStateEntity { RequestedAt = DateTimeOffset.UtcNow });

        Assert.Equal(2, _newsRequests.Count);

        await _service.ClearEtfRequestAsync();

        Assert.Null(await _service.GetEtfRequestAsync());
        var news = await _service.GetNewsRequestAsync();
        Assert.NotNull(news);
        Assert.Equal("EU AI Act", news.Topic);
    }

    [Fact]
    public async Task ClearEtfRequest_WithoutAMarker_IsANoOp()
    {
        await _service.ClearEtfRequestAsync();

        Assert.Empty(_newsRequests);
    }

    [Fact]
    public async Task TryClaimEtfNudge_SucceedsOnceAndNeverAgain()
    {
        Assert.True(await _service.TryClaimEtfNudgeAsync());

        var claim = Assert.Single(_etfTable.OfType<EtfNudgeEntity>());
        Assert.Equal("meta", claim.PartitionKey);
        Assert.Equal("onboarding-nudge", claim.RowKey);
        Assert.True((DateTimeOffset.UtcNow - claim.SentAt).Duration() < TimeSpan.FromMinutes(1));

        // The second Saturday (and every one after) must find the row already there
        Assert.False(await _service.TryClaimEtfNudgeAsync());
        Assert.False(await _service.TryClaimEtfNudgeAsync());
        Assert.Single(_etfTable.OfType<EtfNudgeEntity>());
    }

    [Fact]
    public async Task EtfNudgeMarker_IsInvisibleToTheWatchlist()
    {
        _etfTable.Add(new EtfHoldingEntity { RowKey = "VWCE", Symbol = "VWCE" });
        await _service.TryClaimEtfNudgeAsync();

        // It shares the EtfHoldings table, so only the partition filter keeps it out of
        // the watchlist — a report covering a fund called "onboarding-nudge" would be a mess
        var holdings = await _service.GetEtfHoldingsAsync();

        Assert.Equal("VWCE", Assert.Single(holdings).Symbol);
    }

    [Fact]
    public async Task EtfNudgeMarker_IsNotDisturbedByWatchlistWrites()
    {
        await _service.TryClaimEtfNudgeAsync();

        await _service.SaveEtfHoldingAsync("VWCE", null, null);
        await _service.DeleteEtfHoldingAsync("VWCE");
        await _service.SaveEtfSnapshotsAsync([new EtfPerformance { Symbol = "VWCE", Quote = "€1" }]);

        // A cleared watchlist must not resurrect the one-off nudge
        Assert.Single(_etfTable.OfType<EtfNudgeEntity>());
        Assert.False(await _service.TryClaimEtfNudgeAsync());
    }

    [Fact]
    public async Task ReleaseEtfNudge_HandsTheClaimBackSoItCanBeSentAgain()
    {
        Assert.True(await _service.TryClaimEtfNudgeAsync());

        await _service.ReleaseEtfNudgeAsync();

        Assert.Empty(_etfTable.OfType<EtfNudgeEntity>());
        // The whole point: a nudge that failed to send can be attempted next Saturday
        Assert.True(await _service.TryClaimEtfNudgeAsync());
    }

    [Fact]
    public async Task ReleaseEtfNudge_WithoutAClaim_IsANoOp()
    {
        await _service.ReleaseEtfNudgeAsync();

        Assert.Empty(_etfTable);
    }

    [Fact]
    public async Task ReleaseEtfNudge_LeavesTheWatchlistAlone()
    {
        _etfTable.Add(new EtfHoldingEntity { RowKey = "VWCE", Symbol = "VWCE" });
        await _service.TryClaimEtfNudgeAsync();

        await _service.ReleaseEtfNudgeAsync();

        Assert.Equal("VWCE", Assert.Single(_etfTable.OfType<EtfHoldingEntity>()).Symbol);
    }

    [Fact]
    public async Task SaveNewsRequest_ForcesItsOwnRowKey_SoAnEtfMarkerCannotBeOverwritten()
    {
        // The two markers share a table; an entity carrying the wrong row key must not be
        // able to land on the other command's row
        await _service.SaveEtfRequestAsync(new NewsRequestStateEntity { RequestedAt = DateTimeOffset.UtcNow });
        await _service.SaveNewsRequestAsync(new NewsRequestStateEntity
        {
            RowKey = "etf-request",
            RequestedAt = DateTimeOffset.UtcNow,
            Topic = "EU AI Act"
        });

        Assert.Equal(2, _newsRequests.Count);
        var news = await _service.GetNewsRequestAsync();
        Assert.NotNull(news);
        Assert.Equal("EU AI Act", news.Topic);

        var etf = await _service.GetEtfRequestAsync();
        Assert.NotNull(etf);
        Assert.Null(etf.Topic); // the ETF marker is untouched
    }
}
