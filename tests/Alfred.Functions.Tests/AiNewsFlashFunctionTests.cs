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

public class AiNewsFlashFunctionTests
{
    private readonly INewsResearchService _research = Substitute.For<INewsResearchService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IStateService _state = Substitute.For<IStateService>();

    public AiNewsFlashFunctionTests()
    {
        _state.GetNewsRulesAsync().Returns(new List<NewsRuleEntity>());
        _state.GetReportedNewsSinceAsync(Arg.Any<DateTimeOffset>()).Returns(new List<ReportedNewsEntity>());
        _research.CheckUrgentNewsAsync(Arg.Any<List<NewsRuleEntity>>(), Arg.Any<List<ReportedNewsEntity>>())
            .Returns(new AiNewsDigest());
    }

    private AiNewsFlashFunction CreateFunction(Action<AlfredOptions>? mutate = null) =>
        new(_research, _notifications, _state, Options(o =>
        {
            o.PersonalTelegramChatId = "777";
            mutate?.Invoke(o);
        }), NullLogger<AiNewsFlashFunction>.Instance);

    private static TimerInfo Timer => new();

    private static AiNewsDigest Flash(string? message = "🚨 One couldn't wait.") =>
        new()
        {
            TelegramMessage = message,
            Items = [new AiNewsItem { Headline = "Competitor launch", Url = "https://c.example/x", Category = "competitor" }]
        };

    [Theory]
    [InlineData(false, true, "777")]  // master AI-news switch off
    [InlineData(true, false, "777")]  // flash check itself off
    [InlineData(true, true, "")]      // no personal chat configured
    [InlineData(true, true, "   ")]
    public async Task Disabled_SkipsWithoutTouchingStateOrResearch(bool newsEnabled, bool flashEnabled, string chatId)
    {
        await CreateFunction(o =>
        {
            o.AiNewsEnabled = newsEnabled;
            o.AiNewsFlashEnabled = flashEnabled;
            o.PersonalTelegramChatId = chatId;
        }).Run(Timer);

        await _state.DidNotReceive().GetNewsRulesAsync();
        await _research.DidNotReceiveWithAnyArgs().CheckUrgentNewsAsync(default!, default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
    }

    [Fact]
    public async Task NothingUrgent_TheNormalOutcome_StaysCompletelySilent()
    {
        await CreateFunction().Run(Timer);

        await _research.Received(1).CheckUrgentNewsAsync(Arg.Any<List<NewsRuleEntity>>(), Arg.Any<List<ReportedNewsEntity>>());
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _state.DidNotReceiveWithAnyArgs().SaveReportedNewsAsync(default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalErrorAsync(default!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task ItemsWithoutAUsableMessage_SendNothingAndRecordNothing(string? message)
    {
        _research.CheckUrgentNewsAsync(Arg.Any<List<NewsRuleEntity>>(), Arg.Any<List<ReportedNewsEntity>>())
            .Returns(Flash(message));

        await CreateFunction().Run(Timer);

        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _state.DidNotReceiveWithAnyArgs().SaveReportedNewsAsync(default!);
    }

    [Fact]
    public async Task UrgentStory_SendsTheAlertWithFeedbackButtons_AndRecordsItAsCovered()
    {
        var rules = new List<NewsRuleEntity> { new() { RowKey = "n1", Instruction = "Skip funding rounds" } };
        var covered = new List<ReportedNewsEntity> { new() { RowKey = "old" } };
        _state.GetNewsRulesAsync().Returns(rules);
        _state.GetReportedNewsSinceAsync(Arg.Any<DateTimeOffset>()).Returns(covered);

        var flash = Flash();
        _research.CheckUrgentNewsAsync(rules, covered).Returns(flash);

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
        await _research.Received(1).CheckUrgentNewsAsync(rules, covered);
        Assert.Equal("🚨 One couldn't wait.", message);
        Assert.NotNull(buttons);
        Assert.Equal(2, buttons.Count);
        Assert.Equal($"nf:+:{TableStorageStateService.HashUrl("https://c.example/x")}", buttons[0].CallbackData);
        // Recorded as covered so the evening digest doesn't re-report the story
        await _state.Received(1).SaveReportedNewsAsync(flash.Items);
    }

    [Fact]
    public async Task CoveredStoryLookback_MatchesTheDigestWindow()
    {
        await CreateFunction().Run(Timer);

        var expected = DateTimeOffset.UtcNow.AddDays(-AiNewsDigestFunction.CoveredLookbackDays);
        await _state.Received(1).GetReportedNewsSinceAsync(
            Arg.Is<DateTimeOffset>(since => (since - expected).Duration() < TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task CutOffRun_StaysCompletelySilent_TheEveningDigestCoversIt()
    {
        // Unlike the evening digest, a cut-off midday spot check warrants no ping at all
        _research.CheckUrgentNewsAsync(Arg.Any<List<NewsRuleEntity>>(), Arg.Any<List<ReportedNewsEntity>>())
            .Returns(new AiNewsDigest { Incomplete = true });

        await CreateFunction().Run(Timer);

        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalErrorAsync(default!);
        await _state.DidNotReceiveWithAnyArgs().SaveReportedNewsAsync(default!);
    }

    [Fact]
    public async Task ResearchFailure_IsReportedToThePersonalChat()
    {
        _research.CheckUrgentNewsAsync(Arg.Any<List<NewsRuleEntity>>(), Arg.Any<List<ReportedNewsEntity>>())
            .ThrowsAsync(new InvalidOperationException("search exploded"));

        await CreateFunction().Run(Timer);

        await _notifications.Received(1).SendPersonalErrorAsync(
            Arg.Is<string>(m => m.Contains("AI news flash check failed") && m.Contains("search exploded")));
        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
        await _state.DidNotReceiveWithAnyArgs().SaveReportedNewsAsync(default!);
    }
}
