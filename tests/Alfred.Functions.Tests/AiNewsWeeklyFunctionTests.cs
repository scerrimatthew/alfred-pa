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

public class AiNewsWeeklyFunctionTests
{
    private readonly INewsResearchService _research = Substitute.For<INewsResearchService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IStateService _state = Substitute.For<IStateService>();

    public AiNewsWeeklyFunctionTests()
    {
        _state.GetNewsRulesAsync().Returns(new List<NewsRuleEntity>());
        _state.GetReportedNewsSinceAsync(Arg.Any<DateTimeOffset>()).Returns(new List<ReportedNewsEntity>());
        _research.BuildWeeklySynthesisAsync(Arg.Any<List<ReportedNewsEntity>>(), Arg.Any<List<NewsRuleEntity>>())
            .Returns((string?)null);
    }

    private AiNewsWeeklyFunction CreateFunction(Action<AlfredOptions>? mutate = null) =>
        new(_research, _notifications, _state, Options(o =>
        {
            o.PersonalTelegramChatId = "777";
            mutate?.Invoke(o);
        }), NullLogger<AiNewsWeeklyFunction>.Instance);

    private static TimerInfo Timer => new();

    [Theory]
    [InlineData(false, true, "777")]  // master AI-news switch off
    [InlineData(true, false, "777")]  // weekly synthesis itself off
    [InlineData(true, true, "")]      // no personal chat configured
    public async Task Disabled_SkipsWithoutTouchingStateOrResearch(bool newsEnabled, bool weeklyEnabled, string chatId)
    {
        await CreateFunction(o =>
        {
            o.AiNewsEnabled = newsEnabled;
            o.AiNewsWeeklyEnabled = weeklyEnabled;
            o.PersonalTelegramChatId = chatId;
        }).Run(Timer);

        await _state.DidNotReceiveWithAnyArgs().GetReportedNewsSinceAsync(default);
        await _research.DidNotReceiveWithAnyArgs().BuildWeeklySynthesisAsync(default!, default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
    }

    [Fact]
    public async Task NothingReportedThisWeek_SkipsSilentlyWithoutSynthesizing()
    {
        await CreateFunction().Run(Timer);

        await _research.DidNotReceiveWithAnyArgs().BuildWeeklySynthesisAsync(default!, default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalErrorAsync(default!);
    }

    [Fact]
    public async Task WeekLookback_IsAboutSevenDays()
    {
        await CreateFunction().Run(Timer);

        var expected = DateTimeOffset.UtcNow.AddDays(-7);
        await _state.Received(1).GetReportedNewsSinceAsync(
            Arg.Is<DateTimeOffset>(since => (since - expected).Duration() < TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task ReportedWeek_SendsTheSynthesisWithoutButtons()
    {
        var weekItems = new List<ReportedNewsEntity> { new() { RowKey = "s1", Headline = "DORA lands" } };
        var rules = new List<NewsRuleEntity> { new() { RowKey = "n1", Instruction = "More on EU AI Act" } };
        _state.GetReportedNewsSinceAsync(Arg.Any<DateTimeOffset>()).Returns(weekItems);
        _state.GetNewsRulesAsync().Returns(rules);
        _research.BuildWeeklySynthesisAsync(weekItems, rules).Returns("🗞 The week the thesis held.");

        await CreateFunction().Run(Timer);

        // The exact lists loaded from state must reach the researcher
        await _research.Received(1).BuildWeeklySynthesisAsync(weekItems, rules);
        // No feedback buttons on the synthesis — it covers already-rated stories
        await _notifications.Received(1).SendPersonalAlertAsync("🗞 The week the thesis held.");
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalErrorAsync(default!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptySynthesis_SkipsTheSendWithoutAnError(string? synthesis)
    {
        _state.GetReportedNewsSinceAsync(Arg.Any<DateTimeOffset>())
            .Returns(new List<ReportedNewsEntity> { new() { RowKey = "s1" } });
        _research.BuildWeeklySynthesisAsync(Arg.Any<List<ReportedNewsEntity>>(), Arg.Any<List<NewsRuleEntity>>())
            .Returns(synthesis);

        await CreateFunction().Run(Timer);

        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalErrorAsync(default!);
    }

    [Fact]
    public async Task SynthesisFailure_IsReportedToThePersonalChat()
    {
        _state.GetReportedNewsSinceAsync(Arg.Any<DateTimeOffset>())
            .Returns(new List<ReportedNewsEntity> { new() { RowKey = "s1" } });
        _research.BuildWeeklySynthesisAsync(Arg.Any<List<ReportedNewsEntity>>(), Arg.Any<List<NewsRuleEntity>>())
            .ThrowsAsync(new InvalidOperationException("model down"));

        await CreateFunction().Run(Timer);

        await _notifications.Received(1).SendPersonalErrorAsync(
            Arg.Is<string>(m => m.Contains("Weekly AI-news synthesis failed") && m.Contains("model down")));
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
    }
}
