using System.Text;
using Alfred.Functions.Models;
using Alfred.Functions.Services.Gmail;
using Alfred.Functions.Services.Pdf;
using Alfred.Functions.Services.State;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static Alfred.Functions.Tests.Support.TestData;

namespace Alfred.Functions.Tests;

// Pins the Gmail message -> SchoolEmail mapping and the pure text helpers.
// ParseMessageAsync only touches the Gmail client for PDF attachments, so a
// message without attachment parts can be parsed entirely offline.
public class GmailReaderParsingTests
{
    private readonly GmailReaderService _reader = new(
        Options(),
        Microsoft.Extensions.Options.Options.Create(new Alfred.Functions.Configuration.GoogleOptions()),
        Substitute.For<IStateService>(),
        Substitute.For<IPdfExtractorService>(),
        NullLogger<GmailReaderService>.Instance);

    // Never receives a request in these tests — the parsed messages carry no PDF attachments
    private static readonly GmailService IdleGmailService = new();

    private Task<SchoolEmail?> ParseAsync(Message message) =>
        _reader.ParseMessageAsync(IdleGmailService, message, downloadLinkedDocuments: false);

    private static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).Replace('+', '-').Replace('/', '_');

    private static Message PlainTextMessage(
        string body = "Hello there",
        string from = "GO Malta <billing@go.com.mt>",
        IList<string>? labelIds = null,
        params (string Name, string Value)[] extraHeaders)
    {
        var headers = new List<MessagePartHeader>
        {
            new() { Name = "Subject", Value = "August bill" },
            new() { Name = "From", Value = from },
            new() { Name = "Date", Value = "Mon, 17 Aug 2026 09:30:00 +0200" }
        };
        headers.AddRange(extraHeaders.Select(h => new MessagePartHeader { Name = h.Name, Value = h.Value }));

        return new Message
        {
            Id = "m1",
            ThreadId = "t1",
            LabelIds = labelIds,
            Payload = new MessagePart
            {
                MimeType = "text/plain",
                Headers = headers,
                Body = new MessagePartBody { Data = Base64Url(body) }
            }
        };
    }

    [Fact]
    public async Task PlainTextMessage_MapsAllHeaderFields()
    {
        var email = await ParseAsync(PlainTextMessage(labelIds: ["INBOX", "UNREAD"]));

        Assert.NotNull(email);
        Assert.Equal("m1", email.MessageId);
        Assert.Equal("t1", email.ThreadId);
        Assert.Equal("August bill", email.Subject);
        Assert.Equal("GO Malta", email.SenderName);
        Assert.Equal("billing@go.com.mt", email.SenderEmail);
        Assert.Equal("Hello there", email.Body);
        Assert.True(email.WasUnread);
        Assert.Equal(new DateTimeOffset(2026, 8, 17, 9, 30, 0, TimeSpan.FromHours(2)), email.ReceivedDate);
        Assert.Null(email.ListUnsubscribe);
        Assert.False(email.ListUnsubscribeOneClick);
    }

    [Fact]
    public async Task ReadMessage_IsFlaggedAsNotUnread()
    {
        var email = await ParseAsync(PlainTextMessage(labelIds: ["INBOX"]));

        Assert.NotNull(email);
        Assert.False(email.WasUnread);
    }

    [Fact]
    public async Task MissingLabelIds_DefaultsToUnread()
    {
        var email = await ParseAsync(PlainTextMessage(labelIds: null));

        Assert.NotNull(email);
        Assert.True(email.WasUnread);
    }

    [Fact]
    public async Task ListUnsubscribeHeaders_AreCapturedCaseInsensitively()
    {
        var email = await ParseAsync(PlainTextMessage(extraHeaders:
        [
            ("LIST-UNSUBSCRIBE", "<mailto:unsub@shop.com>, <https://shop.com/u>"),
            ("list-unsubscribe-post", "List-Unsubscribe=One-Click")
        ]));

        Assert.NotNull(email);
        Assert.Equal("<mailto:unsub@shop.com>, <https://shop.com/u>", email.ListUnsubscribe);
        Assert.True(email.ListUnsubscribeOneClick);
    }

    [Fact]
    public async Task FromWithoutDisplayName_UsesTheRawValueForBoth()
    {
        var email = await ParseAsync(PlainTextMessage(from: "billing@go.com.mt"));

        Assert.NotNull(email);
        Assert.Equal("billing@go.com.mt", email.SenderName);
        Assert.Equal("billing@go.com.mt", email.SenderEmail);
    }

    [Fact]
    public async Task HtmlOnlyMessage_FallsBackToStrippedHtml()
    {
        var html = "<div>Dear parent,<br>Sports day is <b>Friday</b>.</div><ul><li>Bring a hat</li></ul>";
        var message = new Message
        {
            Id = "m1",
            ThreadId = "t1",
            Payload = new MessagePart
            {
                MimeType = "multipart/alternative",
                Headers =
                [
                    new MessagePartHeader { Name = "Subject", Value = "Sports day" },
                    new MessagePartHeader { Name = "From", Value = "School <news@school.mt>" },
                    new MessagePartHeader { Name = "Date", Value = "Mon, 17 Aug 2026 09:30:00 +0200" }
                ],
                Parts =
                [
                    new MessagePart
                    {
                        MimeType = "text/html",
                        Body = new MessagePartBody { Data = Base64Url(html) }
                    }
                ]
            }
        };

        var email = await ParseAsync(message);

        Assert.NotNull(email);
        Assert.Contains("Dear parent,", email.Body);
        Assert.Contains("Sports day is Friday.", email.Body.Replace("\n", " "));
        Assert.Contains("• Bring a hat", email.Body);
        Assert.DoesNotContain("<", email.Body);
    }

    [Fact]
    public async Task NestedPlainTextPart_IsPreferredOverHtml()
    {
        var message = new Message
        {
            Id = "m1",
            Payload = new MessagePart
            {
                MimeType = "multipart/alternative",
                Headers =
                [
                    new MessagePartHeader { Name = "Subject", Value = "S" },
                    new MessagePartHeader { Name = "From", Value = "A <a@b.com>" },
                    new MessagePartHeader { Name = "Date", Value = "Mon, 17 Aug 2026 09:30:00 +0200" }
                ],
                Parts =
                [
                    new MessagePart { MimeType = "text/html", Body = new MessagePartBody { Data = Base64Url("<p>html version</p>") } },
                    new MessagePart { MimeType = "text/plain", Body = new MessagePartBody { Data = Base64Url("plain version") } }
                ]
            }
        };

        var email = await ParseAsync(message);

        Assert.NotNull(email);
        Assert.Equal("plain version", email.Body);
    }

    [Fact]
    public async Task ThreadIdMissing_FallsBackToTheMessageId()
    {
        var message = PlainTextMessage();
        message.ThreadId = null;

        var email = await ParseAsync(message);

        Assert.NotNull(email);
        Assert.Equal("m1", email.ThreadId);
    }

    // ---- Pure text helpers ----

    [Theory]
    [InlineData("GO Malta <billing@go.com.mt>", "GO Malta")]
    [InlineData("\"Scerri, Matthew\" <m@s.com>", "Scerri, Matthew")]
    [InlineData("plain@address.com", "plain@address.com")]
    public void ExtractSenderName_HandlesDisplayNamesAndBareAddresses(string from, string expected)
    {
        Assert.Equal(expected, GmailReaderService.ExtractSenderName(from));
    }

    [Theory]
    [InlineData("GO Malta <billing@go.com.mt>", "billing@go.com.mt")]
    [InlineData("plain@address.com", "plain@address.com")]
    public void ExtractEmail_PullsTheAddressOutOfAngleBrackets(string from, string expected)
    {
        Assert.Equal(expected, GmailReaderService.ExtractEmail(from));
    }

    [Fact]
    public void StripHtml_RemovesStyleBlocksTagsAndDecodesEntities()
    {
        var html = "<style>body { color: red; }</style><div>Fish &amp; Chips &lt;tonight&gt;</div><p>Second&nbsp;line</p>";

        var text = GmailReaderService.StripHtml(html);

        Assert.DoesNotContain("color", text);
        Assert.Contains("Fish & Chips <tonight>", text);
        Assert.Contains("Second line", text);
        Assert.DoesNotContain("<div>", text);
    }

    [Fact]
    public void StripHtml_TurnsListsIntoBulletsAndCollapsesBlankLines()
    {
        var html = "<ul><li>One</li><li>Two</li></ul><p>End</p><p></p><p></p><p>Tail</p>";

        var text = GmailReaderService.StripHtml(html);

        Assert.Contains("• One", text);
        Assert.Contains("• Two", text);
        Assert.DoesNotContain("\n\n\n", text);
    }

    [Fact]
    public void ExtractLinks_KeepsHttpLinksOnce_AndDropsFilteredDomains()
    {
        var html = """
            <a href="https://example.com/doc.pdf">doc</a>
            <a href="https://example.com/doc.pdf">same doc again</a>
            <a href="mailto:someone@x.com">mail</a>
            <a href="https://www.myschoolmanagement.com/portal">portal</a>
            <a href="https://facebook.com/SacredHeartMalta/photos">fb</a>
            <a href="https://other.com/page">other</a>
            """;

        var links = GmailReaderService.ExtractLinks(html);

        Assert.Equal(new[] { "https://example.com/doc.pdf", "https://other.com/page" }, links);
    }

    [Theory]
    [InlineData("https://x.com/files/My%20Doc.pdf?token=1", "My Doc.pdf")]
    [InlineData("https://x.com/a/b/report.pdf", "report.pdf")]
    [InlineData("https://x.com/", "document")]
    [InlineData("not a url at all", "document")]
    public void GetFileNameFromUrl_UsesTheLastPathSegment(string url, string expected)
    {
        Assert.Equal(expected, GmailReaderService.GetFileNameFromUrl(url));
    }

    [Fact]
    public void EncodeHeaderValue_LeavesAsciiAloneAndEncodesUtf8()
    {
        Assert.Equal("Re: hello", GmailReaderService.EncodeHeaderValue("Re: hello"));

        var encoded = GmailReaderService.EncodeHeaderValue("Re: Ħal Qormi");
        var expected = $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes("Re: Ħal Qormi"))}?=";
        Assert.Equal(expected, encoded);
    }

    [Fact]
    public void SplitAddresses_TrimsAndDropsEmpties()
    {
        var addresses = GmailReaderService.SplitAddresses("a@x.com, b@y.com , ,c@z.com");

        Assert.Equal(new[] { "a@x.com", "b@y.com", "c@z.com" }, addresses.ToList());
    }

    [Fact]
    public void DecodeBase64_HandlesUrlSafeAlphabet()
    {
        // "??>" encodes to "Pz8-" in the URL-safe alphabet ("Pz8+" in standard base64)
        Assert.Equal("??>", GmailReaderService.DecodeBase64("Pz8-"));
    }
}
