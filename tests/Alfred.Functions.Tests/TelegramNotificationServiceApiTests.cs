using System.Text.Json;
using Alfred.Functions.Services.Notifications;
using Alfred.Functions.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Alfred.Functions.Tests;

// Drives TelegramNotificationService through the real Telegram.Bot client over a fake
// HTTP layer: pins the Bot API method, chat routing, HTML mode, button markup, and
// the chunking of over-long messages.
public sealed class TelegramNotificationServiceApiTests : IDisposable
{
    private const string BotToken = "12345:TESTTOKEN";

    private readonly FakeHttpHandler _http = new();
    private readonly TelegramNotificationService _service;
    private readonly Dictionary<string, string?> _originalEnv = [];

    public TelegramNotificationServiceApiTests()
    {
        SetEnv("Telegram__BotToken", BotToken);
        SetEnv("Alfred__TelegramChatId", "555");
        SetEnv("Alfred__PersonalTelegramChatId", "777");

        _http.Route("POST /sendMessage", """
            {"ok":true,"result":{"message_id":1,"date":1723972000,"chat":{"id":555,"type":"private"},"text":"x"}}
            """);
        _http.Route("POST /answerCallbackQuery", """{"ok":true,"result":true}""");

        _service = new TelegramNotificationService(NullLogger<TelegramNotificationService>.Instance)
        {
            BotHttpClient = new HttpClient(_http)
        };
    }

    private void SetEnv(string name, string? value)
    {
        _originalEnv[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _originalEnv)
            Environment.SetEnvironmentVariable(name, value);
    }

    private JsonElement SentBody(int index = 0) =>
        JsonDocument.Parse(_http.Requests[index].Body!).RootElement;

    private static string ChatIdOf(JsonElement body) =>
        body.GetProperty("chat_id").ToString();

    [Fact]
    public async Task SendAlert_PostsHtmlToTheSchoolChatWithPreviewsOff()
    {
        await _service.SendAlertAsync("📩 <b>WEEKLY PLAN</b>");

        var request = _http.Requests.Single();
        Assert.EndsWith("/sendMessage", request.Path);
        Assert.Contains($"/bot{BotToken}/", request.Uri.AbsolutePath);

        var body = SentBody();
        Assert.Equal("555", ChatIdOf(body));
        Assert.Equal("📩 <b>WEEKLY PLAN</b>", body.GetProperty("text").GetString());
        Assert.Equal("HTML", body.GetProperty("parse_mode").GetString(), ignoreCase: true);
        Assert.True(body.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean());
    }

    [Fact]
    public async Task SendPersonalAlert_GoesToThePersonalChat()
    {
        await _service.SendPersonalAlertAsync("hello");

        Assert.Equal("777", ChatIdOf(SentBody()));
    }

    [Fact]
    public async Task SendPersonalAlert_LaysButtonsOutInRowsOfTwo()
    {
        await _service.SendPersonalAlertAsync("GO bill",
        [
            new NotificationButton("Mark unread", "mu:m1"),
            new NotificationButton("Mute sender", "sup:m1"),
            new NotificationButton("Remind me tomorrow", "sn1:m1")
        ]);

        var keyboard = SentBody().GetProperty("reply_markup").GetProperty("inline_keyboard");
        Assert.Equal(2, keyboard.GetArrayLength());       // two rows
        Assert.Equal(2, keyboard[0].GetArrayLength());    // first row: two buttons
        Assert.Equal(1, keyboard[1].GetArrayLength());    // second row: the leftover

        Assert.Equal("Mark unread", keyboard[0][0].GetProperty("text").GetString());
        Assert.Equal("mu:m1", keyboard[0][0].GetProperty("callback_data").GetString());
        Assert.Equal("sn1:m1", keyboard[1][0].GetProperty("callback_data").GetString());
    }

    [Fact]
    public async Task SendPersonalAlert_LongMessage_ChunksAndPutsButtonsOnTheLastChunk()
    {
        var message = new string('a', 4000) + "\n" + new string('b', 500);

        await _service.SendPersonalAlertAsync(message, [new NotificationButton("Mark unread", "mu:m1")]);

        Assert.Equal(2, _http.Requests.Count);
        var first = SentBody(0);
        var second = SentBody(1);
        Assert.Equal(new string('a', 4000), first.GetProperty("text").GetString());
        Assert.Equal(new string('b', 500), second.GetProperty("text").GetString());
        // Buttons sit directly under the message — only on the final chunk
        Assert.False(first.TryGetProperty("reply_markup", out var markup) && markup.ValueKind != JsonValueKind.Null);
        Assert.True(second.GetProperty("reply_markup").GetProperty("inline_keyboard")[0][0]
            .GetProperty("callback_data").GetString() == "mu:m1");
    }

    [Fact]
    public async Task SendMessage_ChunksLongRepliesToTheGivenChat()
    {
        await _service.SendMessageAsync(999, new string('x', 5000));

        Assert.Equal(2, _http.Requests.Count);
        Assert.Equal("999", ChatIdOf(SentBody(0)));
        Assert.Equal(4096, SentBody(0).GetProperty("text").GetString()!.Length);
        Assert.Equal(5000 - 4096, SentBody(1).GetProperty("text").GetString()!.Length);
    }

    [Fact]
    public async Task SendError_PrefixesTheWarningBanner()
    {
        await _service.SendErrorAsync("EmailMonitor failed: boom");

        var text = SentBody().GetProperty("text").GetString();
        Assert.StartsWith("⚠️ Alfred encountered an error:", text);
        Assert.Contains("boom", text);
        Assert.Equal("555", ChatIdOf(SentBody()));
    }

    [Fact]
    public async Task SendPersonalError_GoesToThePersonalChatWithItsOwnBanner()
    {
        await _service.SendPersonalErrorAsync("SnoozeCheck failed");

        var body = SentBody();
        Assert.Equal("777", ChatIdOf(body));
        Assert.StartsWith("⚠️ Alfred (personal inbox) encountered an error:", body.GetProperty("text").GetString());
    }

    [Fact]
    public async Task AnswerCallback_AcksTheButtonPressWithText()
    {
        await _service.AnswerCallbackAsync("cb42", "Done!");

        var request = _http.Requests.Single();
        Assert.EndsWith("/answerCallbackQuery", request.Path);
        var body = SentBody();
        Assert.Equal("cb42", body.GetProperty("callback_query_id").GetString());
        Assert.Equal("Done!", body.GetProperty("text").GetString());
    }

    [Fact]
    public async Task MissingBotToken_FailsFastWithoutTouchingTheNetwork()
    {
        SetEnv("Telegram__BotToken", null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SendAlertAsync("x"));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task MissingChatId_FailsFastNamingTheSetting()
    {
        SetEnv("Alfred__PersonalTelegramChatId", null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SendPersonalAlertAsync("x"));
        Assert.Contains("Alfred__PersonalTelegramChatId", ex.Message);
        Assert.Empty(_http.Requests);
    }

    // ---- HTML rejected: one plain-text retry ----

    private const string OkResponse =
        """{"ok":true,"result":{"message_id":1,"date":1723972000,"chat":{"id":555,"type":"private"},"text":"x"}}""";

    private static string ParseErrorResponse(string description = "Bad Request: can't parse entities: Unsupported start tag \"2%\" at byte offset 20") =>
        $$"""{"ok":false,"error_code":400,"description":{{JsonSerializer.Serialize(description)}}}""";

    // Queue takes precedence over the constructor's route, so the first send fails and
    // the retry falls through to the standard OK response
    private void FailFirstSendWith(string body) =>
        _http.EnqueueJson(body, System.Net.HttpStatusCode.BadRequest);

    [Fact]
    public async Task SendPersonalAlert_HtmlRejected_ResendsThatChunkAsPlainText()
    {
        FailFirstSendWith(ParseErrorResponse());

        await _service.SendPersonalAlertAsync(
            """📈 <b>VWCE</b> — CPI came in &lt;2%, see <a href="https://justetf.example/vwce">the numbers</a>""");

        Assert.Equal(2, _http.Requests.Count);

        var retry = SentBody(1);
        Assert.Equal("777", ChatIdOf(retry));
        // No parse mode on the retry — that is the whole point of the fallback
        Assert.False(retry.TryGetProperty("parse_mode", out var mode) && mode.ValueKind != JsonValueKind.Null,
            "the plain-text retry must not ask Telegram to parse HTML again");
        Assert.Equal("📈 VWCE — CPI came in <2%, see the numbers (https://justetf.example/vwce)",
            retry.GetProperty("text").GetString());
        // ...and the delivery options still hold
        Assert.True(retry.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean());
    }

    [Fact]
    public async Task SendPersonalAlert_HtmlRejected_KeepsTheButtonsOnTheRetry()
    {
        FailFirstSendWith(ParseErrorResponse());

        await _service.SendPersonalAlertAsync("📈 <b>VWCE</b>",
        [
            new NotificationButton("👍", "nf:+:1"),
            new NotificationButton("👎", "nf:-:1")
        ]);

        // Losing the buttons would silently cost the feedback loop
        var keyboard = SentBody(1).GetProperty("reply_markup").GetProperty("inline_keyboard");
        var row = Assert.Single(keyboard.EnumerateArray());
        Assert.Equal(2, row.GetArrayLength());
        Assert.Equal("nf:+:1", row[0].GetProperty("callback_data").GetString());
    }

    [Fact]
    public async Task SendAlert_HtmlRejected_RetriesToTheSchoolChat()
    {
        FailFirstSendWith(ParseErrorResponse("Bad Request: can't parse entities: Unclosed start tag at byte offset 3"));

        await _service.SendAlertAsync("📩 <b>PLAN");

        Assert.Equal(2, _http.Requests.Count);
        Assert.Equal("555", ChatIdOf(SentBody(1)));
        Assert.Equal("📩 PLAN", SentBody(1).GetProperty("text").GetString());
    }

    [Fact]
    public async Task SendMessage_HtmlRejected_RetriesToTheSameChat()
    {
        FailFirstSendWith(ParseErrorResponse());

        await _service.SendMessageAsync(999, "<b>hi</b> &amp; bye");

        Assert.Equal(2, _http.Requests.Count);
        Assert.Equal("999", ChatIdOf(SentBody(1)));
        Assert.Equal("hi & bye", SentBody(1).GetProperty("text").GetString());
    }

    [Fact]
    public async Task SendMessage_RejectedForAnotherReason_IsNotRetried()
    {
        FailFirstSendWith("""{"ok":false,"error_code":400,"description":"Bad Request: chat not found"}""");

        await Assert.ThrowsAsync<Telegram.Bot.Exceptions.ApiRequestException>(
            () => _service.SendMessageAsync(999, "<b>hi</b>"));

        // A retry here would fail identically and bury the real cause
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task SendMessage_PlainTextRetryAlsoRejected_GivesUpInsteadOfLooping()
    {
        FailFirstSendWith(ParseErrorResponse());
        FailFirstSendWith(ParseErrorResponse());

        await Assert.ThrowsAsync<Telegram.Bot.Exceptions.ApiRequestException>(
            () => _service.SendMessageAsync(999, "<b>hi</b>"));

        Assert.Equal(2, _http.Requests.Count); // the send and exactly one retry
    }

    [Theory]
    [InlineData("<b></b>")]
    [InlineData("<i> </i>")]
    public async Task SendMessage_ChunkThatFlattensToNothing_SurfacesTheOriginalFailure(string markup)
    {
        FailFirstSendWith(ParseErrorResponse());

        var ex = await Assert.ThrowsAsync<Telegram.Bot.Exceptions.ApiRequestException>(
            () => _service.SendMessageAsync(999, markup));

        // Telegram rejects an empty message too, so the retry would just fail differently
        // and bury the real complaint — better to let the parse failure through
        Assert.Contains("parse entities", ex.Message);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task SendMessage_ChunkWithSomethingLeftAfterFlattening_StillRetries()
    {
        // The guard above must not swallow the ordinary case it sits next to
        FailFirstSendWith(ParseErrorResponse());

        await _service.SendMessageAsync(999, "<b>VWCE</b> up <2%");

        Assert.Equal(2, _http.Requests.Count);
        Assert.Equal("VWCE up <2%", SentBody(1).GetProperty("text").GetString());
    }

    [Fact]
    public async Task SendMessage_AcceptedFirstTime_IsNeverResent()
    {
        await _service.SendMessageAsync(999, "<b>hi</b>");

        Assert.Single(_http.Requests);
        Assert.Equal("HTML", SentBody().GetProperty("parse_mode").GetString(), ignoreCase: true);
    }
}
