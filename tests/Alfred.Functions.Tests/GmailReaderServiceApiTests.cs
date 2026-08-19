using System.Text;
using System.Text.Json;
using Alfred.Functions.Configuration;
using Alfred.Functions.Models;
using Alfred.Functions.Services.Gmail;
using Alfred.Functions.Services.Pdf;
using Alfred.Functions.Services.State;
using Alfred.Functions.Tests.Support;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static Alfred.Functions.Tests.Support.TestData;

namespace Alfred.Functions.Tests;

// Drives GmailReaderService end to end over a fake HTTP layer: the real Google SDK
// builds the requests (queries, URLs, bodies) and parses canned Gmail JSON, so these
// tests pin what actually goes over the wire — Gmail query construction included.
public class GmailReaderServiceApiTests
{
    private readonly FakeHttpHandler _http = new();
    private readonly IStateService _state = Substitute.For<IStateService>();
    private readonly IPdfExtractorService _pdfExtractor = Substitute.For<IPdfExtractorService>();

    private GmailReaderService CreateReader(Action<AlfredOptions>? mutate = null)
    {
        var reader = new GmailReaderService(
            Options(mutate),
            Microsoft.Extensions.Options.Options.Create(new GoogleOptions()),
            _state,
            _pdfExtractor,
            NullLogger<GmailReaderService>.Instance)
        {
            GmailServiceOverride = new GmailService(new BaseClientService.Initializer
            {
                HttpClientFactory = new FakeGoogleHttpClientFactory(_http),
                ApplicationName = "AlfredTests",
                GZipEnabled = false // keep recorded request bodies readable
            })
        };
        return reader;
    }

    private string ListQuery(int index = 0)
    {
        var request = _http.Requests
            .Where(r => r.Path.EndsWith("/users/me/messages", StringComparison.Ordinal))
            .ElementAt(index);
        return System.Web.HttpUtility.ParseQueryString(request.Query)["q"]
            ?? throw new InvalidOperationException("list request had no q parameter");
    }

    private static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).Replace('+', '-').Replace('/', '_');

    private static string ListJson(string? nextPageToken, params (string Id, string ThreadId)[] refs) =>
        JsonSerializer.Serialize(new
        {
            messages = refs.Select(r => new { id = r.Id, threadId = r.ThreadId }).ToArray(),
            nextPageToken
        });

    private static string FullMessageJson(
        string id, string threadId, string subject = "A subject",
        string from = "Sender <sender@example.com>", string body = "Hello",
        string[]? labelIds = null) =>
        JsonSerializer.Serialize(new
        {
            id,
            threadId,
            labelIds = labelIds ?? ["INBOX", "UNREAD"],
            payload = new
            {
                mimeType = "text/plain",
                headers = new[]
                {
                    new { name = "Subject", value = subject },
                    new { name = "From", value = from },
                    new { name = "Date", value = "Mon, 17 Aug 2026 09:30:00 +0200" }
                },
                body = new { data = Base64Url(body) }
            }
        });

    private static long ExtractAfterEpoch(string query)
    {
        var afterIndex = query.IndexOf("after:", StringComparison.Ordinal);
        Assert.True(afterIndex >= 0, $"query has no after: clause: {query}");
        var value = query[(afterIndex + "after:".Length)..].Split(' ')[0];
        return long.Parse(value);
    }

    // ---- School fetch ----

    [Fact]
    public async Task GetNewEmails_QueriesTheSchoolSenderWithAnEpochSecondsWindow()
    {
        _http.Route("GET /gmail/v1/users/me/messages/m1", FullMessageJson("m1", "t1", subject: "Weekly plan"));
        _http.Route("GET /gmail/v1/users/me/messages", ListJson(null, ("m1", "t1")));
        _state.IsEmailProcessedAsync("m1").Returns(false);

        var emails = await CreateReader().GetNewEmailsAsync();

        var email = Assert.Single(emails);
        Assert.Equal("Weekly plan", email.Subject);

        var query = ListQuery();
        Assert.Contains("from:noreply@myschoolmanagement.com", query);
        Assert.DoesNotContain("is:unread", query); // IncludeReadEmails default: date-window query

        // after: must be Unix SECONDS (Gmail ignores milliseconds), ~25h back by default
        var epoch = ExtractAfterEpoch(query);
        var expected = DateTimeOffset.UtcNow.AddHours(-25).ToUnixTimeSeconds();
        Assert.InRange(epoch, expected - 300, expected + 300);
    }

    [Fact]
    public async Task GetNewEmails_UnreadOnlyMode_PrependsIsUnread()
    {
        _http.Route("GET /gmail/v1/users/me/messages", ListJson(null));

        await CreateReader(o => o.IncludeReadEmails = false).GetNewEmailsAsync();

        Assert.StartsWith("is:unread ", ListQuery());
    }

    [Fact]
    public async Task GetNewEmails_SkipsAlreadyProcessedWithoutFetchingThem()
    {
        _http.Route("GET /gmail/v1/users/me/messages/m-new", FullMessageJson("m-new", "t2"));
        _http.Route("GET /gmail/v1/users/me/messages", ListJson(null, ("m-old", "t1"), ("m-new", "t2")));
        _state.IsEmailProcessedAsync("m-old").Returns(true);
        _state.IsEmailProcessedAsync("m-new").Returns(false);

        var emails = await CreateReader().GetNewEmailsAsync();

        Assert.Equal("m-new", Assert.Single(emails).MessageId);
        Assert.Empty(_http.RequestsTo("/messages/m-old"));
    }

    [Fact]
    public async Task GetNewEmails_FollowsPageTokensAndReturnsOldestFirst()
    {
        // Gmail lists newest first: page 1 has the newer message, page 2 the older one
        _http.EnqueueJson(ListJson("page-2", ("m-newer", "t2")));
        _http.EnqueueJson(ListJson(null, ("m-older", "t1")));
        _http.Route("GET /gmail/v1/users/me/messages/m-newer", FullMessageJson("m-newer", "t2"));
        _http.Route("GET /gmail/v1/users/me/messages/m-older", FullMessageJson("m-older", "t1"));
        _state.IsEmailProcessedAsync(Arg.Any<string>()).Returns(false);

        var emails = await CreateReader().GetNewEmailsAsync();

        Assert.Equal(new[] { "m-older", "m-newer" }, emails.Select(e => e.MessageId).ToArray());

        var secondList = _http.Requests
            .Where(r => r.Path.EndsWith("/users/me/messages", StringComparison.Ordinal))
            .ElementAt(1);
        Assert.Equal("page-2", System.Web.HttpUtility.ParseQueryString(secondList.Query)["pageToken"]);
    }

    // ---- Personal fetch ----

    [Fact]
    public async Task GetNewPersonalEmails_ExcludesSchoolPromotionsAndSocial()
    {
        _http.Route("GET /gmail/v1/users/me/messages", ListJson(null));

        await CreateReader().GetNewPersonalEmailsAsync();

        var query = ListQuery();
        Assert.Contains("in:inbox", query);
        Assert.Contains("-from:noreply@myschoolmanagement.com", query);
        Assert.Contains("-category:promotions", query);
        Assert.Contains("-category:social", query);
    }

    [Fact]
    public async Task GetNewPersonalEmails_HonorsThePersonalLookbackOverride()
    {
        _http.Route("GET /gmail/v1/users/me/messages", ListJson(null));

        await CreateReader(o => o.PersonalLookbackHours = 100).GetNewPersonalEmailsAsync();

        var epoch = ExtractAfterEpoch(ListQuery());
        var expected = DateTimeOffset.UtcNow.AddHours(-100).ToUnixTimeSeconds();
        Assert.InRange(epoch, expected - 300, expected + 300);
    }

    // ---- Backfill batching ----

    [Fact]
    public async Task GetBackfillBatch_WalksOldestFirstSkipsProcessedAndStopsAtTheBatchSize()
    {
        var oldestDate = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        // Gmail order: newest first
        _http.Route("GET /gmail/v1/users/me/messages/m-2", FullMessageJson("m-2", "t2"));
        _http.Route("GET /gmail/v1/users/me/messages", ListJson(null, ("m-3", "t3"), ("m-2", "t2"), ("m-1", "t1")));
        _state.IsPersonalEmailProcessedAsync("m-1").Returns(true);
        _state.IsPersonalEmailProcessedAsync("m-2").Returns(false);

        var batch = await CreateReader().GetBackfillBatchAsync(oldestDate, batchSize: 1);

        // m-1 (oldest) already processed -> skipped; m-2 fills the batch; m-3 never fetched
        Assert.Equal("m-2", Assert.Single(batch).MessageId);
        Assert.Empty(_http.RequestsTo("/messages/m-3"));
        Assert.Empty(_http.RequestsTo("/messages/m-1"));

        var query = ListQuery();
        Assert.DoesNotContain("is:unread", query); // read state is irrelevant to a backfill
        Assert.Equal(oldestDate.ToUnixTimeSeconds(), ExtractAfterEpoch(query));
    }

    // ---- On-demand search and read ----

    [Fact]
    public async Task SearchInbox_ClampsMaxResultsAndMapsMetadataOnlyResults()
    {
        _http.Route("GET /gmail/v1/users/me/messages/m1", _ => JsonSerializer.Serialize(new
        {
            id = "m1",
            threadId = "t1",
            snippet = "Fish &amp; chips tonight",
            payload = new
            {
                headers = new[]
                {
                    new { name = "Subject", value = "Dinner" },
                    new { name = "From", value = "Bob <bob@x.com>" },
                    new { name = "Date", value = "Tue, 18 Aug 2026 20:00:00 +0200" }
                }
            }
        }));
        _http.Route("GET /gmail/v1/users/me/messages", ListJson(null, ("m1", "t1")));

        var results = await CreateReader().SearchInboxAsync("from:bob", maxResults: 50);

        var hit = Assert.Single(results);
        Assert.Equal("m1", hit.MessageId);
        Assert.Equal("Dinner", hit.Subject);
        Assert.Equal("Bob", hit.SenderName);
        Assert.Equal("bob@x.com", hit.SenderEmail);
        Assert.Equal("Fish & chips tonight", hit.Snippet); // HTML entities decoded

        var listRequest = _http.Requests.First(r => r.Path.EndsWith("/users/me/messages", StringComparison.Ordinal));
        var listParams = System.Web.HttpUtility.ParseQueryString(listRequest.Query);
        Assert.Equal("20", listParams["maxResults"]); // capped from 50
        Assert.Equal("from:bob", listParams["q"]);

        var getRequest = _http.Requests.First(r => r.Path.EndsWith("/messages/m1", StringComparison.Ordinal));
        Assert.Contains("format=metadata", getRequest.Query);
    }

    [Fact]
    public async Task GetEmail_NotFound_ReturnsNullInsteadOfThrowing()
    {
        _http.Route("GET /gmail/v1/users/me/messages/gone",
            """{"error":{"code":404,"message":"Not Found","errors":[{"reason":"notFound"}]}}""",
            System.Net.HttpStatusCode.NotFound);

        Assert.Null(await CreateReader().GetEmailAsync("gone"));
    }

    [Fact]
    public async Task GetEmail_Found_ParsesTheFullMessage()
    {
        _http.Route("GET /gmail/v1/users/me/messages/m1",
            FullMessageJson("m1", "t1", subject: "Contract", body: "please sign", labelIds: ["INBOX"]));

        var email = await CreateReader().GetEmailAsync("m1");

        Assert.NotNull(email);
        Assert.Equal("Contract", email.Subject);
        Assert.Equal("please sign", email.Body);
        Assert.False(email.WasUnread);
    }

    // ---- Labeling ----

    [Fact]
    public async Task MarkAsReadAndLabel_CreatesMissingAncestorLabelsThenModifies()
    {
        _http.Route("POST /gmail/v1/users/me/messages/m1/modify", """{"id":"m1"}""");
        _http.Route("GET /gmail/v1/users/me/labels", """{"labels":[]}""");
        _http.Route("POST /gmail/v1/users/me/labels", req =>
        {
            var name = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                .RootElement.GetProperty("name").GetString()!;
            return JsonSerializer.Serialize(new { id = $"L-{name}", name });
        });

        await CreateReader().MarkAsReadAndLabelAsync("m1", "Alfred/School/Event");

        // Gmail only nests labels whose parents exist — ancestors first
        var created = _http.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Path.EndsWith("/users/me/labels", StringComparison.Ordinal))
            .Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("name").GetString())
            .ToArray();
        Assert.Equal(new[] { "Alfred", "Alfred/School", "Alfred/School/Event" }, created);

        var modify = JsonDocument.Parse(_http.RequestsTo("/messages/m1/modify").Single().Body!).RootElement;
        Assert.Equal("L-Alfred/School/Event", modify.GetProperty("addLabelIds")[0].GetString());
        Assert.Equal("UNREAD", modify.GetProperty("removeLabelIds")[0].GetString());
    }

    [Fact]
    public async Task MarkAsReadAndLabel_UsesTheLabelCacheOnRepeatCalls()
    {
        _http.Route("POST /modify", """{"id":"x"}""");
        _http.Route("GET /gmail/v1/users/me/labels",
            """{"labels":[{"id":"L1","name":"Alfred"},{"id":"L2","name":"Alfred/School"},{"id":"L3","name":"Alfred/School/Event"}]}""");

        var reader = CreateReader();
        await reader.MarkAsReadAndLabelAsync("m1", "Alfred/School/Event");
        await reader.MarkAsReadAndLabelAsync("m2", "Alfred/School/Event");

        Assert.Single(_http.RequestsTo("/users/me/labels"));
        Assert.Equal(2, _http.RequestsTo("/modify").Count());
    }

    [Fact]
    public async Task LabelWithoutMarkingRead_NeverTouchesTheUnreadFlag()
    {
        _http.Route("POST /gmail/v1/users/me/messages/m1/modify", """{"id":"m1"}""");
        _http.Route("GET /gmail/v1/users/me/labels", """{"labels":[{"id":"L9","name":"Invoice"}]}""");

        await CreateReader().LabelWithoutMarkingReadAsync("m1", "Invoice");

        var modify = JsonDocument.Parse(_http.RequestsTo("/messages/m1/modify").Single().Body!).RootElement;
        Assert.Equal("L9", modify.GetProperty("addLabelIds")[0].GetString());
        Assert.False(modify.TryGetProperty("removeLabelIds", out var removed) && removed.ValueKind == JsonValueKind.Array && removed.GetArrayLength() > 0,
            "a quiet backfill label must not remove UNREAD");
    }

    [Fact]
    public async Task MarkAsUnread_AddsTheUnreadLabel()
    {
        _http.Route("POST /gmail/v1/users/me/messages/m1/modify", """{"id":"m1"}""");

        await CreateReader().MarkAsUnreadAsync("m1");

        var modify = JsonDocument.Parse(_http.RequestsTo("/messages/m1/modify").Single().Body!).RootElement;
        Assert.Equal("UNREAD", modify.GetProperty("addLabelIds")[0].GetString());
    }

    [Fact]
    public async Task Recategorize_SwapsTheOldAlfredLabelForTheNewOne()
    {
        _http.Route("GET /gmail/v1/users/me/messages/m1",
            FullMessageJson("m1", "t1", labelIds: ["L-Invoice", "INBOX"]));
        _http.Route("GET /gmail/v1/users/me/labels",
            """{"labels":[{"id":"L-Invoice","name":"Invoice"},{"id":"L-Delivery","name":"Delivery"},{"id":"L-Random","name":"Random"}]}""");
        _http.Route("POST /gmail/v1/users/me/messages/m1/modify", """{"id":"m1"}""");

        await CreateReader().RecategorizeAsync("m1", "Delivery");

        var modify = JsonDocument.Parse(_http.RequestsTo("/messages/m1/modify").Single().Body!).RootElement;
        Assert.Equal("L-Delivery", modify.GetProperty("addLabelIds")[0].GetString());
        Assert.Equal("L-Invoice", modify.GetProperty("removeLabelIds")[0].GetString());
        Assert.Equal(1, modify.GetProperty("removeLabelIds").GetArrayLength()); // INBOX and non-Alfred labels stay
    }

    // ---- Reply detection ----

    [Fact]
    public async Task HasReplied_TrueOnlyWhenASentMessageFollowsTheTarget()
    {
        _http.Route("GET /gmail/v1/users/me/threads/t1", JsonSerializer.Serialize(new
        {
            id = "t1",
            messages = new object[]
            {
                new { id = "m1", internalDate = "1000", labelIds = new[] { "INBOX" } },
                new { id = "m2", internalDate = "2000", labelIds = new[] { "SENT" } }
            }
        }));
        _http.Route("GET /gmail/v1/users/me/threads/t2", JsonSerializer.Serialize(new
        {
            id = "t2",
            messages = new object[]
            {
                new { id = "m8", internalDate = "500", labelIds = new[] { "SENT" } },
                new { id = "m9", internalDate = "1000", labelIds = new[] { "INBOX" } }
            }
        }));

        var reader = CreateReader();
        Assert.True(await reader.HasRepliedAsync("t1", "m1"));
        Assert.False(await reader.HasRepliedAsync("t2", "m9")); // earlier sent mail is not a reply
    }

    [Fact]
    public async Task HasReplied_MissingThread_CountsAsNoReply()
    {
        _http.Route("GET /gmail/v1/users/me/threads/gone",
            """{"error":{"code":404,"message":"Not Found"}}""", System.Net.HttpStatusCode.NotFound);

        Assert.False(await CreateReader().HasRepliedAsync("gone", "m1"));
    }

    // ---- PDF attachments ----

    private static string PdfMessageJson(string attachmentId = "att1") =>
        JsonSerializer.Serialize(new
        {
            id = "m1",
            threadId = "t1",
            labelIds = new[] { "INBOX" },
            payload = new
            {
                mimeType = "multipart/mixed",
                headers = new[]
                {
                    new { name = "Subject", value = "Invoice attached" },
                    new { name = "From", value = "Aeris <billing@aeris.mt>" },
                    new { name = "Date", value = "Mon, 17 Aug 2026 09:30:00 +0200" }
                },
                parts = new object[]
                {
                    new
                    {
                        mimeType = "text/plain",
                        body = new { data = Base64Url("see attached") }
                    },
                    new
                    {
                        mimeType = "application/pdf",
                        filename = "invoice.pdf",
                        body = new { attachmentId }
                    }
                }
            }
        });

    [Fact]
    public async Task PdfAttachment_IsDownloadedDecodedAndHandedToTheExtractor()
    {
        // Bytes across the full range so the base64url '-'/'_' alphabet actually appears
        var pdfBytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var attachmentData = Convert.ToBase64String(pdfBytes).Replace('+', '-').Replace('/', '_');

        _http.Route("GET /messages/m1/attachments/att1",
            JsonSerializer.Serialize(new { size = pdfBytes.Length, data = attachmentData }));
        _http.Route("GET /gmail/v1/users/me/messages/m1", PdfMessageJson());
        _pdfExtractor.ExtractText(Arg.Any<byte[]>(), "invoice.pdf").Returns("Total due €120");

        var email = await CreateReader().GetEmailAsync("m1");

        Assert.NotNull(email);
        Assert.Equal("see attached", email.Body);
        var document = Assert.Single(email.Documents);
        Assert.Equal("invoice.pdf", document.Title);
        Assert.Equal(LinkedDocumentSource.EmailAttachment, document.Source);
        Assert.Equal("Total due €120", document.ExtractedText);

        // The exact bytes must survive the base64url round trip
        _pdfExtractor.Received(1).ExtractText(
            Arg.Is<byte[]>(b => b.SequenceEqual(pdfBytes)), "invoice.pdf");
    }

    [Fact]
    public async Task PdfAttachmentFetchFailure_SkipsTheAttachmentButKeepsTheEmail()
    {
        _http.Route("GET /messages/m1/attachments/att1",
            """{"error":{"code":404,"message":"Not Found"}}""", System.Net.HttpStatusCode.NotFound);
        _http.Route("GET /gmail/v1/users/me/messages/m1", PdfMessageJson());

        var email = await CreateReader().GetEmailAsync("m1");

        Assert.NotNull(email);
        Assert.Equal("Invoice attached", email.Subject);
        Assert.Empty(email.Documents);
    }

    // ---- Drafts and the unsubscribe email ----

    private static string DecodeBase64Url(string raw)
    {
        var base64 = raw.Replace('-', '+').Replace('_', '/');
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '=')));
    }

    [Fact]
    public async Task CreateReplyDraft_ThreadsAProperMimeReply()
    {
        _http.Route("GET /gmail/v1/users/me/messages/m1", JsonSerializer.Serialize(new
        {
            id = "m1",
            threadId = "t1",
            payload = new
            {
                headers = new[]
                {
                    new { name = "Subject", value = "Quote for the fence" },
                    new { name = "From", value = "Antonio <antonio@x.com>" },
                    new { name = "Message-ID", value = "<orig-id@x.com>" },
                    new { name = "References", value = "<earlier@x.com>" }
                }
            }
        }));
        _http.Route("POST /gmail/v1/users/me/drafts", """{"id":"d1","message":{"id":"dm1","threadId":"t1"}}""");

        var result = await CreateReader().CreateReplyDraftAsync("m1", "Thursday works for me.", replyAll: false);

        var draftBody = JsonDocument.Parse(_http.RequestsTo("/users/me/drafts").Single().Body!).RootElement;
        Assert.Equal("t1", draftBody.GetProperty("message").GetProperty("threadId").GetString());

        var mime = DecodeBase64Url(draftBody.GetProperty("message").GetProperty("raw").GetString()!);
        Assert.Contains("To: Antonio <antonio@x.com>", mime);
        Assert.Contains("Subject: Re: Quote for the fence", mime);
        Assert.Contains("In-Reply-To: <orig-id@x.com>", mime);
        Assert.Contains("References: <earlier@x.com> <orig-id@x.com>", mime);
        // The body itself travels base64-encoded
        var bodyStart = mime.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
        Assert.Equal("Thursday works for me.",
            Encoding.UTF8.GetString(Convert.FromBase64String(mime[bodyStart..].Replace("\r\n", ""))));

        Assert.Contains("Re: Quote for the fence", result);
        Assert.Contains(GmailLinks.ForThread("t1"), result);
    }

    [Fact]
    public async Task CreateReplyDraft_ReplyAll_CcsEveryoneExceptSelfAndTheSender()
    {
        _http.Route("GET /gmail/v1/users/me/profile", """{"emailAddress":"me@self.com"}""");
        _http.Route("GET /gmail/v1/users/me/messages/m1", JsonSerializer.Serialize(new
        {
            id = "m1",
            threadId = "t1",
            payload = new
            {
                headers = new[]
                {
                    new { name = "Subject", value = "Re: Plans" },
                    new { name = "From", value = "Antonio <antonio@x.com>" },
                    new { name = "To", value = "me@self.com, Bob <bob@x.com>" },
                    new { name = "Cc", value = "Carol <carol@x.com>" }
                }
            }
        }));
        _http.Route("POST /gmail/v1/users/me/drafts", """{"id":"d1","message":{"id":"dm1","threadId":"t1"}}""");

        await CreateReader().CreateReplyDraftAsync("m1", "ok", replyAll: true);

        var draftBody = JsonDocument.Parse(_http.RequestsTo("/users/me/drafts").Single().Body!).RootElement;
        var mime = DecodeBase64Url(draftBody.GetProperty("message").GetProperty("raw").GetString()!);

        Assert.Contains("Cc: Bob <bob@x.com>, Carol <carol@x.com>", mime);
        Assert.DoesNotContain("me@self.com", mime.Split("\r\n").First(l => l.StartsWith("Cc:", StringComparison.Ordinal)));
        // Subject already had Re: — must not double up
        Assert.Contains("Subject: Re: Plans", mime);
        Assert.DoesNotContain("Re: Re:", mime);
    }

    [Fact]
    public async Task SendUnsubscribeEmail_SendsABareUnsubscribeMessage()
    {
        _http.Route("POST /gmail/v1/users/me/messages/send", """{"id":"sent1"}""");

        await CreateReader().SendUnsubscribeEmailAsync("unsub@list.com", "stop-mailing");

        var body = JsonDocument.Parse(_http.RequestsTo("/messages/send").Single().Body!).RootElement;
        var mime = DecodeBase64Url(body.GetProperty("raw").GetString()!);
        Assert.Equal("To: unsub@list.com\r\nSubject: stop-mailing\r\n\r\nunsubscribe", mime);
    }
}
