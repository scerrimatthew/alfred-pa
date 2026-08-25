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

// Model-written prose reaches Telegram in HTML mode, and one stray "<" or "&" makes the
// API refuse the whole message. These pin the two halves of the plain-text fallback:
// recognizing that refusal, and flattening the markup without losing the links.
public class TelegramHtmlFallbackTests
{
    [Theory]
    [InlineData("Bad Request: can't parse entities: Unsupported start tag \"2%\" at byte offset 42", true)]
    [InlineData("Bad Request: can't parse entities: Unclosed start tag at byte offset 7", true)]
    [InlineData("Bad Request: CAN'T PARSE ENTITIES: something", true)] // matched case-insensitively
    [InlineData("Bad Request: chat not found", false)]                 // a real failure, not formatting
    [InlineData("Forbidden: bot was blocked by the user", false)]
    [InlineData("Too Many Requests: retry after 30", false)]
    [InlineData("", false)]
    public void IsHtmlParseFailure_OnlyMatchesFormattingComplaints(string message, bool expected)
    {
        // Retrying a "chat not found" as plain text would just fail twice and hide the cause
        Assert.Equal(expected, TelegramNotificationService.IsHtmlParseFailure(new InvalidOperationException(message)));
    }

    [Fact]
    public void ToPlainText_KeepsTheLinkTargetNextToItsLabel()
    {
        var plain = TelegramNotificationService.ToPlainText(
            """📈 <b>VWCE</b> slipped — <a href="https://justetf.example/vwce">the numbers</a>.""");

        Assert.Equal("📈 VWCE slipped — the numbers (https://justetf.example/vwce).", plain);
    }

    [Fact]
    public void ToPlainText_HandlesSeveralLinksAndAttributesOnTheAnchor()
    {
        var plain = TelegramNotificationService.ToPlainText(
            """<a href="https://a.example" target="_blank">A</a> and <a href="https://b.example">B</a>""");

        Assert.Equal("A (https://a.example) and B (https://b.example)", plain);
    }

    [Fact]
    public void ToPlainText_KeepsTheStrayCharacterThatCausedTheFailureInTheFirstPlace()
    {
        // The regression this pins: a blanket <[^>]+> swallowed everything from a stray "<"
        // to the next real tag — deleting the very clause the fallback exists to rescue
        var plain = TelegramNotificationService.ToPlainText(
            "📈 <b>VWCE</b> €128.42 ▼ <b>-1.4%</b> — CPI came in <2%, and <b>IWDA</b> held up");

        Assert.Equal("📈 VWCE €128.42 ▼ -1.4% — CPI came in <2%, and IWDA held up", plain);
    }

    [Fact]
    public void ToPlainText_SingleQuotedHref_IsFlattenedToo()
    {
        var plain = TelegramNotificationService.ToPlainText(
            "<a href='https://y.example'>other</a>");

        Assert.Equal("other (https://y.example)", plain);
    }

    [Fact]
    public void ToPlainText_StrayLessThanFollowedByATagLetter_DoesNotEatTheSentence()
    {
        // "<a whisker above 4% while equities rose >" looked like one long <a …> tag to a
        // lazier attribute rule, and 43 characters of prose vanished with it
        const string prose = "the yield stayed <a whisker above 4% while equities rose >1%";

        Assert.Equal(prose, TelegramNotificationService.ToPlainText(prose));
    }

    [Fact]
    public void ToPlainText_BareWordsBetweenAngleBrackets_StayProse()
    {
        // Attributes have to carry a value; "b and y" is arithmetic, not markup
        const string prose = "if x<b and y>c";

        Assert.Equal(prose, TelegramNotificationService.ToPlainText(prose));
    }

    [Fact]
    public void ToPlainText_RealTagsStillGo_EvenNextToAStrayLessThan()
    {
        var plain = TelegramNotificationService.ToPlainText(
            """<b>VWCE</b> up <2% and <span class="tg-spoiler">hidden</span>""");

        Assert.Equal("VWCE up <2% and hidden", plain);
    }

    [Fact]
    public void ToPlainText_QuotedUnquotedAndMiscasedAttributes_AreAllRecognized()
    {
        var plain = TelegramNotificationService.ToPlainText(
            """<a href='u'>l</a> <code class=x>y</code> <B>caps</B>""");

        Assert.Equal("l (u) y caps", plain);
    }

    [Fact]
    public void ToPlainText_LeavesTagsTelegramNeverParsedAlone()
    {
        // Not Telegram markup, so it is ordinary text as far as this fallback is concerned —
        // stripping it would be guessing at content again
        var plain = TelegramNotificationService.ToPlainText("<div>the fund <b>VWCE</b></div>");

        Assert.Equal("<div>the fund VWCE</div>", plain);
    }

    [Theory]
    [InlineData("<b>x</b>", "x")]
    [InlineData("<strong>x</strong>", "x")]
    [InlineData("<i>x</i>", "x")]
    [InlineData("<em>x</em>", "x")]
    [InlineData("<u>x</u>", "x")]
    [InlineData("<s>x</s>", "x")]
    [InlineData("<del>x</del>", "x")]
    [InlineData("<code>x</code>", "x")]
    [InlineData("<pre>x</pre>", "x")]
    [InlineData("<tg-spoiler>x</tg-spoiler>", "x")]
    [InlineData("<blockquote>x</blockquote>", "x")]
    [InlineData("<span class=\"tg-spoiler\">x</span>", "x")] // attributes and all
    [InlineData("<B>x</B>", "x")]                             // matched case-insensitively
    public void ToPlainText_StripsEveryTagTelegramItselfAccepts(string html, string expected)
    {
        Assert.Equal(expected, TelegramNotificationService.ToPlainText(html));
    }

    [Fact]
    public void ToPlainText_StripsRemainingTagsAndDecodesEntities()
    {
        var plain = TelegramNotificationService.ToPlainText(
            "<b>CPI</b> came in &lt;2% &amp; the euro <i>held</i>");

        Assert.Equal("CPI came in <2% & the euro held", plain);
    }

    [Fact]
    public void ToPlainText_LeavesPlainProseAlone()
    {
        const string prose = "📈 Quiet week — nothing moved much.";

        Assert.Equal(prose, TelegramNotificationService.ToPlainText(prose));
    }

    [Fact]
    public void ToPlainText_AnchorSpanningLines_IsStillFlattened()
    {
        var plain = TelegramNotificationService.ToPlainText(
            "<a href=\"https://a.example\">a headline\nover two lines</a>");

        Assert.Equal("a headline\nover two lines (https://a.example)", plain);
    }
}
