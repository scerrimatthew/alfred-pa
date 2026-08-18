using Alfred.Functions.Functions;
using Alfred.Functions.Models;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Services.State;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using static Alfred.Functions.Tests.Support.TestData;

namespace Alfred.Functions.Tests;

public class MailingListHygieneFunctionTests
{
    private readonly IStateService _state = Substitute.For<IStateService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();

    private MailingListHygieneFunction CreateFunction(string personalChatId = "777") =>
        new(_state, _notifications,
            Options(o => o.PersonalTelegramChatId = personalChatId),
            NullLogger<MailingListHygieneFunction>.Instance);

    [Fact]
    public async Task WithoutPersonalChatId_DoesNothing()
    {
        await CreateFunction(personalChatId: "").Run(new TimerInfo());

        await _state.DidNotReceiveWithAnyArgs().GetUnsubscribeCandidatesAsync(default, default);
    }

    [Fact]
    public async Task NoCandidates_StaysSilent()
    {
        _state.GetUnsubscribeCandidatesAsync(Arg.Any<int>(), Arg.Any<int>())
            .Returns(new List<SenderStatsEntity>());

        await CreateFunction().Run(new TimerInfo());

        await _notifications.DidNotReceiveWithAnyArgs().SendPersonalAlertAsync(default!);
    }

    [Fact]
    public async Task Candidates_GetProposalsWithButtons_AndAreMarkedProposed()
    {
        var candidate = new SenderStatsEntity
        {
            RowKey = "abc123",
            SenderName = "Shop News",
            SenderEmail = "news@shop.com",
            TotalCount = 7,
            QuietCount = 7
        };
        _state.GetUnsubscribeCandidatesAsync(3, 5).Returns(new List<SenderStatsEntity> { candidate });

        var calls = new List<(string Message, IReadOnlyList<NotificationButton>? Buttons)>();
        _notifications.When(n => n.SendPersonalAlertAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<NotificationButton>?>()))
            .Do(ci => calls.Add((ci.ArgAt<string>(0), ci.ArgAt<IReadOnlyList<NotificationButton>?>(1))));

        await CreateFunction().Run(new TimerInfo());

        Assert.Equal(2, calls.Count);
        Assert.Contains("Monthly inbox hygiene", calls[0].Message);
        Assert.Null(calls[0].Buttons);

        Assert.Contains("Shop News", calls[1].Message);
        Assert.Contains("news@shop.com", calls[1].Message);
        Assert.Contains("7 emails", calls[1].Message);
        Assert.NotNull(calls[1].Buttons);
        Assert.Equal(2, calls[1].Buttons!.Count);
        Assert.Equal("unsub:abc123", calls[1].Buttons![0].CallbackData);
        Assert.Equal("keep:abc123", calls[1].Buttons![1].CallbackData);

        Assert.NotNull(candidate.ProposedAt);
        await _state.Received(1).UpsertSenderStatAsync(candidate);
    }

    [Fact]
    public async Task StateFailure_ReportsPersonalError()
    {
        _state.GetUnsubscribeCandidatesAsync(Arg.Any<int>(), Arg.Any<int>())
            .ThrowsAsync(new TimeoutException("tables down"));

        await CreateFunction().Run(new TimerInfo());

        await _notifications.Received(1).SendPersonalErrorAsync(Arg.Is<string>(m => m.Contains("tables down")));
    }
}
