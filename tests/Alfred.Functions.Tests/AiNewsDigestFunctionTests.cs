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

public class AiNewsDigestFunctionTests
{
    private readonly INewsResearchService _research = Substitute.For<INewsResearchService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IStateService _state = Substitute.For<IStateService>();

    public AiNewsDigestFunctionTests()
    {
        _state.GetNewsRulesAsync().Returns(new List<NewsRuleEntity>());
        _state.GetReportedNewsSinceAsync(Arg.Any<DateTimeOffset>()).Returns(new List<ReportedNewsEntity>());
        _state.GetNewsCandidatesSinceAsync(Arg.Any<DateTimeOffset>()).Returns(new List<NewsCandidateEntity>());
        _research.ResearchDailyNewsAsync(
                Arg.Any<List<NewsRuleEntity>>(), Arg.Any<List<ReportedNewsEntity>>(), Arg.Any<List<NewsCandidateEntity>>())
            .Returns(new AiNewsDigest());
    }

    private AiNewsDigestFunction CreateFunction(Action<AlfredOptions>? mutate = null) =>
        new(_research, _notifications, _state, Options(o =>
        {
            o.PersonalTelegramChatId = "777";
            mutate?.Invoke(o);
        }), NullLogger<AiNewsDigestFunction>.Instance);

    private static TimerInfo Timer => new();

    private static AiNewsDigest Digest(string? message = "🗞 Evening! One story.") =>
        new()
        {
            TelegramMessage = message,
            Items = [new AiNewsItem { Headline = "H", Url = "https://u", Category = "competitor" }]
        };

    [Fact]
    public async Task Disabled_SkipsWithoutTouchingStateOrResearch()
    {
        await CreateFunction(o => o.AiNewsEnabled = false).Run(Timer);

        await _state.DidNotReceive().GetNewsRulesAsync();
        await _state.DidNotReceiveWithAnyArgs().GetReportedNewsSinceAsync(default);
        await _research.DidNotReceiveWithAnyArgs().ResearchDailyNewsAsync(default!, default!, default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
    }

    [Fact]
    public async Task NoPersonalChatConfigured_SkipsTheSameWay()
    {
        await CreateFunction(o => o.PersonalTelegramChatId = "").Run(Timer);

        await _state.DidNotReceive().GetNewsRulesAsync();
        await _research.DidNotReceiveWithAnyArgs().ResearchDailyNewsAsync(default!, default!, default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
    }

    [Fact]
    public async Task QuietDay_NoItems_SendsNothingAndRecordsNothing()
    {
        _research.ResearchDailyNewsAsync(
                Arg.Any<List<NewsRuleEntity>>(), Arg.Any<List<ReportedNewsEntity>>(), Arg.Any<List<NewsCandidateEntity>>())
            .Returns(new AiNewsDigest { TelegramMessage = "🗞 stray message", Items = [] });

        await CreateFunction().Run(Timer);

        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _state.DidNotReceiveWithAnyArgs().SaveReportedNewsAsync(default!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ItemsWithoutAUsableMessage_SendNothingAndRecordNothing(string? message)
    {
        _research.ResearchDailyNewsAsync(
                Arg.Any<List<NewsRuleEntity>>(), Arg.Any<List<ReportedNewsEntity>>(), Arg.Any<List<NewsCandidateEntity>>())
            .Returns(Digest(message));

        await CreateFunction().Run(Timer);

        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _state.DidNotReceiveWithAnyArgs().SaveReportedNewsAsync(default!);
    }

    [Fact]
    public async Task HappyPath_FeedsRulesCoveredStoriesAndCandidatesIn_SendsTheBriefing_RecordsTheItems()
    {
        var rules = new List<NewsRuleEntity> { new() { RowKey = "n1", Instruction = "Skip funding rounds" } };
        var covered = new List<ReportedNewsEntity> { new() { RowKey = "old", Headline = "Old story" } };
        var candidates = new List<NewsCandidateEntity> { new() { Headline = "Newsletter lead" } };
        _state.GetNewsRulesAsync().Returns(rules);
        _state.GetReportedNewsSinceAsync(Arg.Any<DateTimeOffset>()).Returns(covered);
        _state.GetNewsCandidatesSinceAsync(Arg.Any<DateTimeOffset>()).Returns(candidates);

        var digest = Digest();
        _research.ResearchDailyNewsAsync(rules, covered, candidates).Returns(digest);

        string? message = null;
        IReadOnlyList<NotificationButton>? buttons = null;
        _notifications.When(n => n.SendPersonalAlertAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<NotificationButton>?>()))
            .Do(ci =>
            {
                message = ci.ArgAt<string>(0);
                buttons = ci.ArgAt<IReadOnlyList<NotificationButton>?>(1);
            });

        await CreateFunction().Run(Timer);

        // The exact lists loaded from state must reach the researcher
        await _research.Received(1).ResearchDailyNewsAsync(rules, covered, candidates);
        Assert.Equal("🗞 Evening! One story.", message);
        // The briefing carries one 👍/👎 feedback pair per story
        Assert.NotNull(buttons);
        Assert.Equal(2, buttons.Count);
        Assert.Equal($"nf:+:{TableStorageStateService.HashUrl("https://u")}", buttons[0].CallbackData);
        Assert.Equal($"nf:-:{TableStorageStateService.HashUrl("https://u")}", buttons[1].CallbackData);
        await _state.Received(1).SaveReportedNewsAsync(digest.Items);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalErrorAsync(default!);
    }

    [Fact]
    public async Task CoveredStoryLookback_IsAboutFourteenDays()
    {
        _research.ResearchDailyNewsAsync(
                Arg.Any<List<NewsRuleEntity>>(), Arg.Any<List<ReportedNewsEntity>>(), Arg.Any<List<NewsCandidateEntity>>())
            .Returns(Digest());

        await CreateFunction().Run(Timer);

        var expected = DateTimeOffset.UtcNow.AddDays(-14);
        await _state.Received(1).GetReportedNewsSinceAsync(
            Arg.Is<DateTimeOffset>(since => (since - expected).Duration() < TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task CandidateLookback_IsAboutTwentySixHours()
    {
        await CreateFunction().Run(Timer);

        var expected = DateTimeOffset.UtcNow.AddHours(-26);
        await _state.Received(1).GetNewsCandidatesSinceAsync(
            Arg.Is<DateTimeOffset>(since => (since - expected).Duration() < TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task ResearchFailure_IsReportedToThePersonalChat()
    {
        _research.ResearchDailyNewsAsync(
                Arg.Any<List<NewsRuleEntity>>(), Arg.Any<List<ReportedNewsEntity>>(), Arg.Any<List<NewsCandidateEntity>>())
            .ThrowsAsync(new InvalidOperationException("search exploded"));

        await CreateFunction().Run(Timer);

        await _notifications.Received(1).SendPersonalErrorAsync(
            Arg.Is<string>(m => m.Contains("AI news digest failed") && m.Contains("search exploded")));
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _state.DidNotReceiveWithAnyArgs().SaveReportedNewsAsync(default!);
    }

    // ---- Feedback buttons ----

    [Fact]
    public void BuildFeedbackButtons_OnePairPerStory_KeyedByUrlHash()
    {
        var items = new List<AiNewsItem>
        {
            new() { Headline = "Short one", Url = "https://a.example/1" },
            new() { Headline = "Another short one", Url = "https://b.example/2" }
        };

        var buttons = AiNewsDigestFunction.BuildFeedbackButtons(items);

        Assert.Equal(4, buttons.Count);
        var hashA = TableStorageStateService.HashUrl("https://a.example/1");
        var hashB = TableStorageStateService.HashUrl("https://b.example/2");
        Assert.Equal("👍 Short one", buttons[0].Text);
        Assert.Equal($"nf:+:{hashA}", buttons[0].CallbackData);
        Assert.Equal("👎 Short one", buttons[1].Text);
        Assert.Equal($"nf:-:{hashA}", buttons[1].CallbackData);
        Assert.Equal($"nf:+:{hashB}", buttons[2].CallbackData);
        Assert.Equal($"nf:-:{hashB}", buttons[3].CallbackData);

        // The hash keys the ReportedNews table row, and Telegram caps callback data at 64 bytes
        Assert.All(buttons, b => Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(b.CallbackData) <= 64,
            $"callback data '{b.CallbackData}' must fit Telegram's 64-byte cap"));
    }

    [Fact]
    public void ShortLabel_HeadlineWithin22Chars_IsKeptVerbatim()
    {
        Assert.Equal("Exactly twenty-two ch.", AiNewsDigestFunction.ShortLabel("Exactly twenty-two ch."));
        Assert.Equal("Short", AiNewsDigestFunction.ShortLabel("Short"));
    }

    [Fact]
    public void ShortLabel_LongHeadline_IsTruncatedWithAnEllipsis()
    {
        var label = AiNewsDigestFunction.ShortLabel("Anthropic ships Claude Opus 6 with new tools");

        Assert.Equal("Anthropic ships Claud…", label);
        Assert.Equal(22, label.Length);
    }

    [Fact]
    public void ShortLabel_TruncationTrimsATrailingSpaceBeforeTheEllipsis()
    {
        // The 21-char cut lands right after a space — it must not survive as "word …"
        var label = AiNewsDigestFunction.ShortLabel("Anthropic ships nice model updates");

        Assert.Equal("Anthropic ships nice…", label);
        Assert.DoesNotContain(" …", label);
    }

    [Fact]
    public void ShortLabel_NeverSplitsAnEmojiSpanningTheCutPoint()
    {
        // 20 filler chars put the 🚀 at UTF-16 indices 20-21 — exactly across the
        // 21-char cut. A lone surrogate in a button label makes Telegram reject
        // the whole message, so the cut must back off to 20
        var label = AiNewsDigestFunction.ShortLabel(new string('a', 20) + "🚀 to the moon");

        Assert.Equal(new string('a', 20) + "…", label);
        Assert.All(label, c => Assert.False(char.IsSurrogate(c), "label must not contain a lone surrogate"));
    }

    [Fact]
    public void ShortLabel_EmojiEndingExactlyAtTheCut_SurvivesWhole()
    {
        // 🚀 at indices 19-20: the cut at 21 lands after the pair, keeping it intact
        var label = AiNewsDigestFunction.ShortLabel(new string('b', 19) + "🚀 and yet more text");

        Assert.Equal(new string('b', 19) + "🚀…", label);
        Assert.Equal(22, label.Length); // 19 + surrogate pair + ellipsis
    }
}
