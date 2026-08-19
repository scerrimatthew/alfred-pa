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
        _research.ResearchDailyNewsAsync(Arg.Any<List<NewsRuleEntity>>(), Arg.Any<List<ReportedNewsEntity>>())
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
        await _research.DidNotReceiveWithAnyArgs().ResearchDailyNewsAsync(default!, default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
    }

    [Fact]
    public async Task NoPersonalChatConfigured_SkipsTheSameWay()
    {
        await CreateFunction(o => o.PersonalTelegramChatId = "").Run(Timer);

        await _state.DidNotReceive().GetNewsRulesAsync();
        await _research.DidNotReceiveWithAnyArgs().ResearchDailyNewsAsync(default!, default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
    }

    [Fact]
    public async Task QuietDay_NoItems_SendsNothingAndRecordsNothing()
    {
        _research.ResearchDailyNewsAsync(Arg.Any<List<NewsRuleEntity>>(), Arg.Any<List<ReportedNewsEntity>>())
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
        _research.ResearchDailyNewsAsync(Arg.Any<List<NewsRuleEntity>>(), Arg.Any<List<ReportedNewsEntity>>())
            .Returns(Digest(message));

        await CreateFunction().Run(Timer);

        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _state.DidNotReceiveWithAnyArgs().SaveReportedNewsAsync(default!);
    }

    [Fact]
    public async Task HappyPath_FeedsRulesAndCoveredStoriesIn_SendsTheBriefing_RecordsTheItems()
    {
        var rules = new List<NewsRuleEntity> { new() { RowKey = "n1", Instruction = "Skip funding rounds" } };
        var covered = new List<ReportedNewsEntity> { new() { RowKey = "old", Headline = "Old story" } };
        _state.GetNewsRulesAsync().Returns(rules);
        _state.GetReportedNewsSinceAsync(Arg.Any<DateTimeOffset>()).Returns(covered);

        var digest = Digest();
        _research.ResearchDailyNewsAsync(rules, covered).Returns(digest);

        await CreateFunction().Run(Timer);

        // The exact lists loaded from state must reach the researcher
        await _research.Received(1).ResearchDailyNewsAsync(rules, covered);
        await _notifications.Received(1).SendPersonalAlertAsync("🗞 Evening! One story.");
        await _state.Received(1).SaveReportedNewsAsync(digest.Items);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalErrorAsync(default!);
    }

    [Fact]
    public async Task CoveredStoryLookback_IsAboutFourteenDays()
    {
        _research.ResearchDailyNewsAsync(Arg.Any<List<NewsRuleEntity>>(), Arg.Any<List<ReportedNewsEntity>>())
            .Returns(Digest());

        await CreateFunction().Run(Timer);

        var expected = DateTimeOffset.UtcNow.AddDays(-14);
        await _state.Received(1).GetReportedNewsSinceAsync(
            Arg.Is<DateTimeOffset>(since => (since - expected).Duration() < TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task ResearchFailure_IsReportedToThePersonalChat()
    {
        _research.ResearchDailyNewsAsync(Arg.Any<List<NewsRuleEntity>>(), Arg.Any<List<ReportedNewsEntity>>())
            .ThrowsAsync(new InvalidOperationException("search exploded"));

        await CreateFunction().Run(Timer);

        await _notifications.Received(1).SendPersonalErrorAsync(
            Arg.Is<string>(m => m.Contains("AI news digest failed") && m.Contains("search exploded")));
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _state.DidNotReceiveWithAnyArgs().SaveReportedNewsAsync(default!);
    }
}
