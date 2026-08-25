using Alfred.Functions.Configuration;
using Alfred.Functions.Functions;
using Alfred.Functions.Models;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.State;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using static Alfred.Functions.Tests.Support.TestData;

namespace Alfred.Functions.Tests;

// Pins the Saturday-morning ETF report: the gate, the silent empty-watchlist skip, the
// three failure replies that must stay distinguishable from each other, and the
// watchlist assembly (saved holdings + configured tickers, deduped and capped).
public class EtfWeeklyReportFunctionTests
{
    private readonly IEtfResearchService _research = Substitute.For<IEtfResearchService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IStateService _state = Substitute.For<IStateService>();

    // Backing store for the shared etf-request marker, so Save/Get/Clear behave like the
    // real single-row table and the finally-block ownership check sees its own write
    private NewsRequestStateEntity? _marker;

    public EtfWeeklyReportFunctionTests()
    {
        _state.GetEtfHoldingsAsync().Returns(new List<EtfHoldingEntity>());
        _state.TryClaimEtfNudgeAsync().Returns(true);
        _state.GetEtfRequestAsync().Returns(_ => _marker);
        _state.When(s => s.SaveEtfRequestAsync(Arg.Any<NewsRequestStateEntity>()))
            .Do(ci => _marker = ci.Arg<NewsRequestStateEntity>());
        _state.When(s => s.ClearEtfRequestAsync()).Do(_ => _marker = null);
        _research.ResearchWeeklyPerformanceAsync(Arg.Any<List<EtfHoldingEntity>>(), Arg.Any<bool>())
            .Returns(new EtfReport());
    }

    private EtfWeeklyReportFunction CreateFunction(Action<AlfredOptions>? mutate = null) =>
        new(_research, _notifications, _state, Options(o =>
        {
            o.PersonalTelegramChatId = "777";
            mutate?.Invoke(o);
        }), NullLogger<EtfWeeklyReportFunction>.Instance);

    private static TimerInfo Timer => new();

    private static EtfReport Report(string? message = "📈 Week of 10-14 Aug", params string[] symbols) =>
        new()
        {
            TelegramMessage = message,
            Items = (symbols.Length == 0 ? ["VWCE"] : symbols)
                .Select(s => new EtfPerformance { Symbol = s, Quote = "€128.42", WeekChangePercent = 1.2 })
                .ToList()
        };

    // ---- Gate ----

    [Theory]
    [InlineData(false, "777")] // report switched off
    [InlineData(true, "")]     // no personal chat configured
    [InlineData(true, "   ")]  // ...or only whitespace
    public async Task Disabled_SkipsWithoutReadingStateOrResearching(bool enabled, string chatId)
    {
        await CreateFunction(o =>
        {
            o.EtfReportEnabled = enabled;
            o.PersonalTelegramChatId = chatId;
        }).Run(Timer);

        await _state.DidNotReceive().GetEtfHoldingsAsync();
        await _state.DidNotReceive().TryClaimEtfNudgeAsync();
        await _research.DidNotReceiveWithAnyArgs().ResearchWeeklyPerformanceAsync(default!, default);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalErrorAsync(default!);
    }

    // ---- Empty watchlist: nudge once, then silence ----

    [Fact]
    public async Task EmptyWatchlist_FirstEverRun_AsksWhichFundsToFollow()
    {
        await CreateFunction().Run(Timer);

        await _state.Received(1).TryClaimEtfNudgeAsync();
        await _notifications.Received(1).SendPersonalAlertAsync(
            Arg.Is<string>(m => m.Contains("Which ones should I follow?") && m.Contains("track VWCE")));
        // Nothing to research, so nothing is billed and no marker is taken
        await _research.DidNotReceiveWithAnyArgs().ResearchWeeklyPerformanceAsync(default!, default);
        await _state.DidNotReceiveWithAnyArgs().SaveEtfRequestAsync(default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalErrorAsync(default!);
    }

    [Fact]
    public async Task EmptyWatchlist_AfterTheNudgeWasClaimed_StaysSilentForever()
    {
        _state.TryClaimEtfNudgeAsync().Returns(false);

        await CreateFunction().Run(Timer);

        // The claim is what makes it a one-off — a weekly "which ETFs?" would be nagging
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalErrorAsync(default!);
        await _research.DidNotReceiveWithAnyArgs().ResearchWeeklyPerformanceAsync(default!, default);
    }

    [Fact]
    public async Task EmptyWatchlist_NudgeThatFailsToSend_HandsTheClaimBack()
    {
        // A one-shot claim spent on a message that never arrived would mean Matthew is
        // never asked again — the claim is only spent if the nudge actually went out
        _notifications.SendPersonalAlertAsync(Arg.Any<string>()).ThrowsAsync(new HttpRequestException("telegram down"));

        await CreateFunction().Run(Timer);

        await _state.Received(1).TryClaimEtfNudgeAsync();
        await _state.Received(1).ReleaseEtfNudgeAsync();
        // The outer handler still reports the failure rather than swallowing it
        await _notifications.Received(1).SendPersonalErrorAsync(
            Arg.Is<string>(m => m.Contains("Weekly ETF report failed") && m.Contains("telegram down")));
    }

    [Fact]
    public async Task EmptyWatchlist_ReleaseAlsoFailing_StillReportsTheSendFailure()
    {
        // Two failures, one story: what Matthew hears about is why the nudge never arrived,
        // not the bookkeeping that failed while cleaning up after it
        _notifications.SendPersonalAlertAsync(Arg.Any<string>()).ThrowsAsync(new HttpRequestException("telegram down"));
        _state.ReleaseEtfNudgeAsync().ThrowsAsync(new TimeoutException("tables down"));

        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<EtfWeeklyReportFunction>>();
        await new EtfWeeklyReportFunction(_research, _notifications, _state,
            Options(o => o.PersonalTelegramChatId = "777"), logger).Run(Timer);

        await _notifications.Received(1).SendPersonalErrorAsync(
            Arg.Is<string>(m => m.Contains("telegram down") && !m.Contains("tables down")));
        // ...and the swallowed release failure is still on the record
        Assert.Contains(logger.ReceivedCalls(), c =>
            c.GetMethodInfo().Name == "Log"
            && Equals(c.GetArguments()[0], Microsoft.Extensions.Logging.LogLevel.Warning));
    }

    [Fact]
    public async Task EmptyWatchlist_NudgeThatSends_KeepsTheClaimSpent()
    {
        await CreateFunction().Run(Timer);

        await _notifications.Received(1).SendPersonalAlertAsync(Arg.Is<string>(m => m.Contains("Which ones should I follow?")));
        await _state.DidNotReceive().ReleaseEtfNudgeAsync();
    }

    [Fact]
    public async Task EmptyWatchlist_NudgeNotClaimed_NeverReleasesSomeoneElsesClaim()
    {
        _state.TryClaimEtfNudgeAsync().Returns(false);

        await CreateFunction().Run(Timer);

        await _state.DidNotReceive().ReleaseEtfNudgeAsync();
    }

    [Fact]
    public async Task NonEmptyWatchlist_NeverClaimsTheOnboardingNudge()
    {
        _state.GetEtfHoldingsAsync().Returns(new List<EtfHoldingEntity> { EtfHolding() });
        _research.ResearchWeeklyPerformanceAsync(Arg.Any<List<EtfHoldingEntity>>(), Arg.Any<bool>())
            .Returns(Report());

        await CreateFunction().Run(Timer);

        // Burning the one-off claim here would silently cost Matthew the nudge he never got
        await _state.DidNotReceive().TryClaimEtfNudgeAsync();
    }

    // ---- Happy path ----

    [Fact]
    public async Task TrackedFunds_AreResearchedAsTheWeeklyRunAndTheSnapshotsSavedAfterSending()
    {
        var holding = EtfHolding("VWCE", name: "Vanguard FTSE All-World");
        _state.GetEtfHoldingsAsync().Returns(new List<EtfHoldingEntity> { holding });
        var report = Report();
        _research.ResearchWeeklyPerformanceAsync(Arg.Any<List<EtfHoldingEntity>>(), Arg.Any<bool>()).Returns(report);

        await CreateFunction().Run(Timer);

        // The saved holding travels through untouched, and the timer run is never "on demand"
        await _research.Received(1).ResearchWeeklyPerformanceAsync(
            Arg.Is<List<EtfHoldingEntity>>(h => h.Count == 1 && ReferenceEquals(h[0], holding)),
            false);
        await _notifications.Received(1).SendPersonalAlertAsync("📈 Week of 10-14 Aug");
        await _state.Received(1).SaveEtfSnapshotsAsync(report.Items);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalErrorAsync(default!);
    }

    [Fact]
    public async Task ConfiguredTickers_SeedTheWatchlistWhenNothingIsSaved()
    {
        _research.ResearchWeeklyPerformanceAsync(Arg.Any<List<EtfHoldingEntity>>(), Arg.Any<bool>())
            .Returns(Report());

        await CreateFunction(o => o.EtfTickers = "VWCE, IWDA").Run(Timer);

        await _research.Received(1).ResearchWeeklyPerformanceAsync(
            Arg.Is<List<EtfHoldingEntity>>(h => h.Select(x => x.Symbol).SequenceEqual(new[] { "VWCE", "IWDA" })),
            false);
    }

    [Fact]
    public async Task OverflowingWatchlist_SaysSoInTheMessageItSends()
    {
        _state.GetEtfHoldingsAsync().Returns(new List<EtfHoldingEntity>
        {
            EtfHolding("VWCE", createdAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            EtfHolding("IWDA", createdAt: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)),
            EtfHolding("SXR8", createdAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero))
        });
        _research.ResearchWeeklyPerformanceAsync(Arg.Any<List<EtfHoldingEntity>>(), Arg.Any<bool>())
            .Returns(Report());

        await CreateFunction(o => o.EtfMaxHoldings = 1).Run(Timer);

        await _research.Received(1).ResearchWeeklyPerformanceAsync(
            Arg.Is<List<EtfHoldingEntity>>(h => h.Count == 1 && h[0].Symbol == "VWCE"), false);
        // A silent cap would read as full coverage — the message must own up to it
        await _notifications.Received(1).SendPersonalAlertAsync(
            Arg.Is<string>(m => m.StartsWith("📈 Week of 10-14 Aug") && m.Contains("2 more funds are")));
    }

    // ---- Failure paths, each with its own reply ----

    [Fact]
    public async Task CutOffResearch_ApologizesAndSavesNothing()
    {
        _state.GetEtfHoldingsAsync().Returns(new List<EtfHoldingEntity> { EtfHolding() });
        _research.ResearchWeeklyPerformanceAsync(Arg.Any<List<EtfHoldingEntity>>(), Arg.Any<bool>())
            .Returns(new EtfReport { Incomplete = true });

        await CreateFunction().Run(Timer);

        await _notifications.Received(1).SendPersonalErrorAsync(
            Arg.Is<string>(m => m.Contains("ran out of time")));
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _state.DidNotReceiveWithAnyArgs().SaveEtfSnapshotsAsync(default!);
    }

    [Fact]
    public async Task NoItems_ReportsTheMissingNumbersRatherThanStayingQuiet()
    {
        _state.GetEtfHoldingsAsync().Returns(new List<EtfHoldingEntity> { EtfHolding() });
        _research.ResearchWeeklyPerformanceAsync(Arg.Any<List<EtfHoldingEntity>>(), Arg.Any<bool>())
            .Returns(new EtfReport { TelegramMessage = "📈 something" });

        await CreateFunction().Run(Timer);

        // Told apart from the cut-off run: different wording, same silence on the alert channel
        await _notifications.Received(1).SendPersonalErrorAsync(
            Arg.Is<string>(m => m.Contains("couldn't pull this week's ETF numbers")));
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _state.DidNotReceiveWithAnyArgs().SaveEtfSnapshotsAsync(default!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ItemsWithoutAMessage_AreAlsoReportedAsAFailure(string? message)
    {
        _state.GetEtfHoldingsAsync().Returns(new List<EtfHoldingEntity> { EtfHolding() });
        _research.ResearchWeeklyPerformanceAsync(Arg.Any<List<EtfHoldingEntity>>(), Arg.Any<bool>())
            .Returns(Report(message));

        await CreateFunction().Run(Timer);

        await _notifications.Received(1).SendPersonalErrorAsync(
            Arg.Is<string>(m => m.Contains("couldn't pull this week's ETF numbers")));
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
    }

    [Fact]
    public async Task ResearchThrowing_IsReportedWithTheReason()
    {
        _state.GetEtfHoldingsAsync().Returns(new List<EtfHoldingEntity> { EtfHolding() });
        _research.ResearchWeeklyPerformanceAsync(Arg.Any<List<EtfHoldingEntity>>(), Arg.Any<bool>())
            .ThrowsAsync(new InvalidOperationException("model down"));

        await CreateFunction().Run(Timer);

        await _notifications.Received(1).SendPersonalErrorAsync(
            Arg.Is<string>(m => m.Contains("Weekly ETF report failed") && m.Contains("model down")));
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
    }

    [Fact]
    public async Task SnapshotSaveThrowing_StillLeavesTheReportSentAndIsReported()
    {
        _state.GetEtfHoldingsAsync().Returns(new List<EtfHoldingEntity> { EtfHolding() });
        _research.ResearchWeeklyPerformanceAsync(Arg.Any<List<EtfHoldingEntity>>(), Arg.Any<bool>())
            .Returns(Report());
        _state.SaveEtfSnapshotsAsync(Arg.Any<List<EtfPerformance>>()).ThrowsAsync(new TimeoutException("tables down"));

        await CreateFunction().Run(Timer);

        await _notifications.Received(1).SendPersonalAlertAsync("📈 Week of 10-14 Aug");
        await _notifications.Received(1).SendPersonalErrorAsync(
            Arg.Is<string>(m => m.Contains("tables down")));
    }

    // ---- Shared research marker (the timer and /etf must not both research) ----

    [Fact]
    public async Task Run_TakesTheResearchMarkerAndClearsItAfterwards()
    {
        _state.GetEtfHoldingsAsync().Returns(new List<EtfHoldingEntity> { EtfHolding() });
        _research.ResearchWeeklyPerformanceAsync(Arg.Any<List<EtfHoldingEntity>>(), Arg.Any<bool>())
            .Returns(Report());

        NewsRequestStateEntity? written = null;
        _state.When(s => s.SaveEtfRequestAsync(Arg.Any<NewsRequestStateEntity>()))
            .Do(ci => written = ci.Arg<NewsRequestStateEntity>());

        await CreateFunction().Run(Timer);

        Assert.NotNull(written);
        Assert.True((DateTimeOffset.UtcNow - written.RequestedAt).Duration() < TimeSpan.FromMinutes(1));
        await _state.Received(1).ClearEtfRequestAsync();
        Assert.Null(_marker);
    }

    [Fact]
    public async Task Run_WhileAnOnDemandRunIsInFlight_SkipsEntirelyAndSaysNothing()
    {
        _state.GetEtfHoldingsAsync().Returns(new List<EtfHoldingEntity> { EtfHolding() });
        _marker = new NewsRequestStateEntity { RequestedAt = DateTimeOffset.UtcNow.AddMinutes(-3) };

        await CreateFunction().Run(Timer);

        // The /etf run already in flight will send the report — a second run would bill a
        // whole web-search sweep and double-message the same chat
        await _research.DidNotReceiveWithAnyArgs().ResearchWeeklyPerformanceAsync(default!, default);
        await _state.DidNotReceiveWithAnyArgs().SaveEtfRequestAsync(default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalErrorAsync(default!);
        // ...and the in-flight run's own marker must survive for its own finally
        await _state.DidNotReceive().ClearEtfRequestAsync();
    }

    [Fact]
    public async Task Run_WithAStaleMarkerFromACrashedRun_ProceedsAnyway()
    {
        _state.GetEtfHoldingsAsync().Returns(new List<EtfHoldingEntity> { EtfHolding() });
        _marker = new NewsRequestStateEntity
        {
            RequestedAt = DateTimeOffset.UtcNow - EtfWeeklyReportFunction.ResearchInFlightWindow - TimeSpan.FromMinutes(1)
        };
        _research.ResearchWeeklyPerformanceAsync(Arg.Any<List<EtfHoldingEntity>>(), Arg.Any<bool>())
            .Returns(Report());

        await CreateFunction().Run(Timer);

        await _research.Received(1).ResearchWeeklyPerformanceAsync(Arg.Any<List<EtfHoldingEntity>>(), false);
        await _notifications.Received(1).SendPersonalAlertAsync("📈 Week of 10-14 Aug");
    }

    [Fact]
    public async Task Run_MarkerReplacedByASuccessor_IsLeftForTheSuccessorToClear()
    {
        _state.GetEtfHoldingsAsync().Returns(new List<EtfHoldingEntity> { EtfHolding() });
        var successor = new NewsRequestStateEntity { RequestedAt = DateTimeOffset.UtcNow.AddMinutes(20) };
        var report = Report();
        _research.ResearchWeeklyPerformanceAsync(Arg.Any<List<EtfHoldingEntity>>(), Arg.Any<bool>())
            .Returns(_ =>
            {
                _marker = successor;
                return report;
            });

        await CreateFunction().Run(Timer);

        await _state.DidNotReceive().ClearEtfRequestAsync();
        Assert.Same(successor, _marker);
    }

    [Fact]
    public async Task Run_FailingResearch_StillReleasesTheMarker()
    {
        _state.GetEtfHoldingsAsync().Returns(new List<EtfHoldingEntity> { EtfHolding() });
        _research.ResearchWeeklyPerformanceAsync(Arg.Any<List<EtfHoldingEntity>>(), Arg.Any<bool>())
            .ThrowsAsync(new InvalidOperationException("model down"));

        await CreateFunction().Run(Timer);

        // A leaked marker would lock out next Saturday's run for its whole window
        await _state.Received(1).ClearEtfRequestAsync();
        Assert.Null(_marker);
        await _notifications.Received(1).SendPersonalErrorAsync(Arg.Is<string>(m => m.Contains("model down")));
    }

    [Fact]
    public async Task Run_MarkerClearFailing_DoesNotMaskTheReportThatWasSent()
    {
        _state.GetEtfHoldingsAsync().Returns(new List<EtfHoldingEntity> { EtfHolding() });
        _research.ResearchWeeklyPerformanceAsync(Arg.Any<List<EtfHoldingEntity>>(), Arg.Any<bool>())
            .Returns(Report());
        _state.ClearEtfRequestAsync().ThrowsAsync(new TimeoutException("tables down"));

        await CreateFunction().Run(Timer);

        await _notifications.Received(1).SendPersonalAlertAsync("📈 Week of 10-14 Aug");
        // The cleanup hiccup must not turn a delivered report into an error message
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalErrorAsync(default!);
    }

    [Fact]
    public async Task Run_MarkerReadBackWithSubSecondDrift_IsStillRecognizedAsItsOwn()
    {
        // The stored timestamp round-trips through Table Storage, which need not preserve
        // sub-millisecond ticks; an exact match would leave the marker behind and lock the
        // next run (and every /etf) out for the whole in-flight window
        _state.GetEtfHoldingsAsync().Returns(new List<EtfHoldingEntity> { EtfHolding() });
        _research.ResearchWeeklyPerformanceAsync(Arg.Any<List<EtfHoldingEntity>>(), Arg.Any<bool>())
            .Returns(Report());
        _state.When(s => s.SaveEtfRequestAsync(Arg.Any<NewsRequestStateEntity>()))
            .Do(ci => _marker = new NewsRequestStateEntity
            {
                RequestedAt = ci.Arg<NewsRequestStateEntity>().RequestedAt.AddMilliseconds(200)
            });

        await CreateFunction().Run(Timer);

        await _state.Received(1).ClearEtfRequestAsync();
        Assert.Null(_marker);
    }

    [Theory]
    [InlineData(0, true)]        // the same run
    [InlineData(200, true)]      // storage dropped some precision
    [InlineData(-200, true)]     // ...in either direction
    [InlineData(999, true)]      // just inside the window
    [InlineData(1500, false)]    // a different run
    [InlineData(-1500, false)]
    [InlineData(600_000, false)] // ten minutes later — plainly a successor
    public void IsSameRun_TreatsASecondOfDriftAsTheSameRunAndNothingMore(int offsetMs, bool expected)
    {
        var mine = new DateTimeOffset(2026, 8, 15, 8, 30, 0, TimeSpan.Zero);

        Assert.Equal(expected, EtfWeeklyReportFunction.IsSameRun(mine.AddMilliseconds(offsetMs), mine));
    }

    [Fact]
    public void IsSameRun_IgnoresTheOffsetSoAUtcReadBackMatchesALocalWrite()
    {
        // Table Storage hands the timestamp back in UTC; the same instant in another
        // offset is still this run's marker
        var mine = new DateTimeOffset(2026, 8, 15, 10, 30, 0, TimeSpan.FromHours(2));

        Assert.True(EtfWeeklyReportFunction.IsSameRun(mine.ToUniversalTime(), mine));
    }

    // ---- Schedule ----

    [Fact]
    public void Run_IsScheduledForSaturdayMorningAfterFridaysClose()
    {
        // The feature's premise is a full trading week in the numbers; a day-of-week typo
        // here would silently report a half-finished week, and nothing else would notice
        var timerParameter = typeof(EtfWeeklyReportFunction)
            .GetMethod(nameof(EtfWeeklyReportFunction.Run))!
            .GetParameters()
            .Single();
        var trigger = timerParameter.GetCustomAttributes(typeof(TimerTriggerAttribute), false)
            .Cast<TimerTriggerAttribute>()
            .Single();

        // second minute hour day month day-of-week — 6 is Saturday, 08:30 UTC
        Assert.Equal("0 30 8 * * 6", trigger.Schedule);
    }

    // ---- BuildWatchlist ----

    [Fact]
    public void BuildWatchlist_SavedHoldingsComeFirstInTheOrderTheyWereAdded()
    {
        var saved = new List<EtfHoldingEntity>
        {
            EtfHolding("SXR8", createdAt: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero)),
            EtfHolding("VWCE", createdAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            EtfHolding("IWDA", createdAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero))
        };

        var (holdings, dropped) = EtfWeeklyReportFunction.BuildWatchlist(saved, "", 8);

        Assert.Equal(["VWCE", "IWDA", "SXR8"], holdings.Select(h => h.Symbol));
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void BuildWatchlist_ConfiguredTickersAreAppendedAfterTheSavedOnes()
    {
        var saved = new List<EtfHoldingEntity> { EtfHolding("VWCE") };

        var (holdings, _) = EtfWeeklyReportFunction.BuildWatchlist(saved, "IWDA; SXR8.DE", 8);

        Assert.Equal(["VWCE", "IWDA", "SXR8.DE"], holdings.Select(h => h.Symbol));
        // Config-seeded entries carry the normalized row key so a later save lands on the same row
        Assert.Equal("SXR8.DE", holdings[2].RowKey);
    }

    [Fact]
    public void BuildWatchlist_ConfiguredTickerAlreadySaved_DoesNotDuplicateOrOverrideIt()
    {
        var saved = new List<EtfHoldingEntity> { EtfHolding("VWCE", name: "Vanguard FTSE All-World", notes: "core, monthly DCA") };

        var (holdings, _) = EtfWeeklyReportFunction.BuildWatchlist(saved, " vwce , VWCE", 8);

        var only = Assert.Single(holdings);
        // The saved holding wins — its name and notes must not be blanked by the config seed
        Assert.Equal("Vanguard FTSE All-World", only.Name);
        Assert.Equal("core, monthly DCA", only.Notes);
    }

    [Fact]
    public void BuildWatchlist_RepeatedConfiguredTickers_AreDedupedAmongstThemselves()
    {
        var (holdings, dropped) = EtfWeeklyReportFunction.BuildWatchlist([], "VWCE, vwce, VWCE ", 8);

        Assert.Equal("VWCE", Assert.Single(holdings).Symbol);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void BuildWatchlist_BeyondTheCap_ReturnsTheOverflowCountInsteadOfDroppingSilently()
    {
        var saved = Enumerable.Range(1, 5)
            .Select(i => EtfHolding($"ETF{i}", createdAt: new DateTimeOffset(2026, i, 1, 0, 0, 0, TimeSpan.Zero)))
            .ToList();

        var (holdings, dropped) = EtfWeeklyReportFunction.BuildWatchlist(saved, "", 3);

        Assert.Equal(["ETF1", "ETF2", "ETF3"], holdings.Select(h => h.Symbol));
        Assert.Equal(2, dropped);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void BuildWatchlist_NonsensicalCap_StillCoversOneFund(int configuredCap)
    {
        var saved = new List<EtfHoldingEntity>
        {
            EtfHolding("VWCE", createdAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            EtfHolding("IWDA", createdAt: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero))
        };

        var (holdings, dropped) = EtfWeeklyReportFunction.BuildWatchlist(saved, "", configuredCap);

        Assert.Equal("VWCE", Assert.Single(holdings).Symbol);
        Assert.Equal(1, dropped);
    }

    [Fact]
    public void BuildWatchlist_NothingSavedAndNothingConfigured_IsEmpty()
    {
        var (holdings, dropped) = EtfWeeklyReportFunction.BuildWatchlist([], "   ", 8);

        Assert.Empty(holdings);
        Assert.Equal(0, dropped);
    }

    // ---- ParseSymbols ----

    [Theory]
    [InlineData("VWCE", new[] { "VWCE" })]
    [InlineData("VWCE, IWDA", new[] { "VWCE", "IWDA" })]
    [InlineData("VWCE;IWDA SXR8.DE", new[] { "VWCE", "IWDA", "SXR8.DE" })]
    [InlineData("  VWCE ,, IWDA  ", new[] { "VWCE", "IWDA" })]
    [InlineData("VWCE\nIWDA\tSXR8", new[] { "VWCE", "IWDA", "SXR8" })]
    public void ParseSymbols_SplitsOnCommasSemicolonsAndWhitespace(string value, string[] expected)
    {
        Assert.Equal(expected, EtfWeeklyReportFunction.ParseSymbols(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseSymbols_BlankInput_IsEmpty(string value)
    {
        Assert.Empty(EtfWeeklyReportFunction.ParseSymbols(value));
    }

    // ---- AppendDroppedNote ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AppendDroppedNote_NothingDropped_LeavesTheMessageAlone(int dropped)
    {
        Assert.Equal("📈 report", EtfWeeklyReportFunction.AppendDroppedNote("📈 report", dropped));
    }

    [Fact]
    public void AppendDroppedNote_OneDropped_ReadsInTheSingular()
    {
        var message = EtfWeeklyReportFunction.AppendDroppedNote("📈 report", 1);

        Assert.StartsWith("📈 report", message);
        Assert.Contains("1 more fund is", message);
    }

    [Fact]
    public void AppendDroppedNote_SeveralDropped_ReadsInThePlural()
    {
        var message = EtfWeeklyReportFunction.AppendDroppedNote("📈 report", 3);

        Assert.Contains("3 more funds are", message);
    }


    // ---- BuildRequestedWatchlist ("/etf VWCE, IWDA") ----

    [Fact]
    public void BuildRequestedWatchlist_KeepsTheOrderHeTypedAndSubstitutesSavedHoldingsInPlace()
    {
        var saved = new List<EtfHoldingEntity>
        {
            EtfHolding("IWDA", name: "iShares Core MSCI World", notes: "world sleeve", lastQuote: "$102.10")
        };

        var (holdings, dropped) = EtfWeeklyReportFunction.BuildRequestedWatchlist(["VWCE", "IWDA", "SXR8"], saved, 8);

        Assert.Equal(["VWCE", "IWDA", "SXR8"], holdings.Select(h => h.Symbol));
        // The tracked one arrives with everything already known about it...
        Assert.Same(saved[0], holdings[1]);
        Assert.Equal("world sleeve", holdings[1].Notes);
        Assert.Equal("$102.10", holdings[1].LastQuote);
        // ...and the untracked ones are researched cold under their normalized keys
        Assert.Equal("VWCE", holdings[0].RowKey);
        Assert.Null(holdings[2].Name);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void BuildRequestedWatchlist_CapsTheTailOfHisListInsteadOfReorderingIt()
    {
        // The bug this pins: a saved holding late in the list must not be promoted to the
        // front, pushing an earlier requested ticker out of the capped window
        var saved = new List<EtfHoldingEntity> { EtfHolding("I") };

        var (holdings, dropped) = EtfWeeklyReportFunction.BuildRequestedWatchlist(
            ["A", "B", "C", "D", "E", "F", "G", "H", "I"], saved, 8);

        Assert.Equal(["A", "B", "C", "D", "E", "F", "G", "H"], holdings.Select(h => h.Symbol));
        Assert.Equal(1, dropped);
    }

    [Fact]
    public void BuildRequestedWatchlist_DedupesByNormalizedKeyKeepingTheFirstMention()
    {
        var (holdings, dropped) = EtfWeeklyReportFunction.BuildRequestedWatchlist(
            ["vwce", "VWCE", " VWCE ", "IWDA"], [], 8);

        Assert.Equal(["vwce", "IWDA"], holdings.Select(h => h.Symbol));
        Assert.Equal("VWCE", holdings[0].RowKey);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void BuildRequestedWatchlist_DuplicateSavedRows_DoNotDuplicateTheHolding()
    {
        // Two rows can only normalize to the same key through a bad write, but the grouping
        // must survive it rather than throwing mid-report
        var saved = new List<EtfHoldingEntity> { EtfHolding("VWCE", name: "first"), EtfHolding("vwce", name: "second") };

        var (holdings, _) = EtfWeeklyReportFunction.BuildRequestedWatchlist(["VWCE"], saved, 8);

        Assert.Equal("first", Assert.Single(holdings).Name);
    }

    [Fact]
    public void BuildRequestedWatchlist_NothingRequested_IsEmpty()
    {
        var (holdings, dropped) = EtfWeeklyReportFunction.BuildRequestedWatchlist([], [EtfHolding()], 8);

        Assert.Empty(holdings);
        Assert.Equal(0, dropped);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void BuildRequestedWatchlist_NonsensicalCap_StillCoversTheFirstOne(int cap)
    {
        var (holdings, dropped) = EtfWeeklyReportFunction.BuildRequestedWatchlist(["VWCE", "IWDA"], [], cap);

        Assert.Equal("VWCE", Assert.Single(holdings).Symbol);
        Assert.Equal(1, dropped);
    }
}
