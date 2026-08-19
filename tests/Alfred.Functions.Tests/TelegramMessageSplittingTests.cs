using Alfred.Functions.Services.Notifications;
using Xunit;

namespace Alfred.Functions.Tests;

// Telegram rejects messages over 4096 characters; SplitMessage is what keeps long
// digests deliverable.
public class TelegramMessageSplittingTests
{
    private const int MaxLength = 4096;

    private static List<string> Split(string message) =>
        TelegramNotificationService.SplitMessage(message);

    [Fact]
    public void ShortMessage_StaysInOnePiece()
    {
        var chunks = Split("hello");

        Assert.Equal(new[] { "hello" }, chunks);
    }

    [Fact]
    public void ExactlyMaxLength_IsNotSplit()
    {
        var message = new string('a', MaxLength);

        Assert.Single(Split(message));
    }

    [Fact]
    public void LongMessage_SplitsAtTheLastNewlineBeforeTheLimit()
    {
        var first = new string('a', 4000);
        var second = new string('b', 500);
        var chunks = Split(first + "\n" + second);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(first, chunks[0]);
        Assert.Equal(second, chunks[1]);
    }

    [Fact]
    public void MessageWithoutNewlines_IsHardSplitAtTheLimit()
    {
        var message = new string('a', MaxLength + 100);
        var chunks = Split(message);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(MaxLength, chunks[0].Length);
        Assert.Equal(100, chunks[1].Length);
    }

    [Fact]
    public void EveryChunkRespectsTheTelegramLimit_AndNoTextIsLost()
    {
        var lines = Enumerable.Range(0, 300).Select(i => $"line {i} " + new string('x', 40));
        var message = string.Join("\n", lines);

        var chunks = Split(message);

        Assert.True(chunks.Count > 1, "test message must actually require splitting");
        Assert.All(chunks, c => Assert.True(c.Length <= MaxLength, $"chunk of {c.Length} chars exceeds the limit"));
        // Joining the chunks back with newlines restores the original text
        Assert.Equal(message, string.Join("\n", chunks));
    }
}
