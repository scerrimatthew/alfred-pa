using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Alfred.Functions.Services.Notifications;

public class TelegramNotificationService : INotificationService
{
    private const int MaxMessageLength = 4096;

    private readonly ILogger<TelegramNotificationService> _logger;

    public TelegramNotificationService(ILogger<TelegramNotificationService> logger)
    {
        _logger = logger;
    }

    public async Task SendAlertAsync(string message)
    {
        var (client, chatId) = GetClientAndChatId("Alfred__TelegramChatId");

        var chunks = SplitMessage(message);
        foreach (var chunk in chunks)
        {
            await SendHtmlAsync(client, chatId, chunk);
        }

        _logger.LogInformation("Sent Telegram alert ({Chunks} chunk(s))", chunks.Count);
    }

    public async Task SendErrorAsync(string errorMessage)
    {
        var (client, chatId) = GetClientAndChatId("Alfred__TelegramChatId");

        var message = $"⚠️ Alfred encountered an error:\n\n{errorMessage}";
        await client.SendMessage(chatId, message);

        _logger.LogInformation("Sent Telegram error notification");
    }

    public async Task SendPersonalAlertAsync(string message, IReadOnlyList<NotificationButton>? buttons = null)
    {
        var (client, chatId) = GetClientAndChatId("Alfred__PersonalTelegramChatId");

        Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup? markup = null;
        if (buttons is { Count: > 0 })
        {
            markup = new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(
                buttons
                    .Select(b => Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData(b.Text, b.CallbackData))
                    .Chunk(2));
        }

        var chunks = SplitMessage(message);
        for (var i = 0; i < chunks.Count; i++)
        {
            // Buttons go on the last chunk so they sit directly under the message
            await SendHtmlAsync(client, chatId, chunks[i], i == chunks.Count - 1 ? markup : null);
        }

        _logger.LogInformation("Sent personal Telegram alert ({Chunks} chunk(s))", chunks.Count);
    }

    public async Task AnswerCallbackAsync(string callbackQueryId, string? text = null)
    {
        var client = CreateBotClient();

        await client.AnswerCallbackQuery(callbackQueryId, text);
    }

    public async Task SendPersonalErrorAsync(string errorMessage)
    {
        var (client, chatId) = GetClientAndChatId("Alfred__PersonalTelegramChatId");

        var message = $"⚠️ Alfred (personal inbox) encountered an error:\n\n{errorMessage}";
        await client.SendMessage(chatId, message);

        _logger.LogInformation("Sent personal Telegram error notification");
    }

    public async Task SendMessageAsync(long chatId, string message)
    {
        var client = CreateBotClient();

        var chunks = SplitMessage(message);
        foreach (var chunk in chunks)
        {
            await SendHtmlAsync(client, chatId, chunk);
        }

        _logger.LogInformation("Sent Telegram reply to chat {ChatId} ({Chunks} chunk(s))", chatId, chunks.Count);
    }

    // Sends one chunk as Telegram HTML, falling back to plain text if Telegram rejects the
    // markup. Model-written prose reaches this method — a market round-up saying "CPI came
    // in <2%" or a headline with a stray & makes Telegram refuse the whole message — and
    // losing a briefing to one punctuation mark is far worse than losing its formatting.
    private async Task SendHtmlAsync(
        TelegramBotClient client,
        Telegram.Bot.Types.ChatId chatId,
        string text,
        Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup? markup = null)
    {
        var noPreview = new Telegram.Bot.Types.LinkPreviewOptions { IsDisabled = true };

        try
        {
            await client.SendMessage(chatId, text, parseMode: ParseMode.Html,
                linkPreviewOptions: noPreview, replyMarkup: markup);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (IsHtmlParseFailure(ex))
        {
            var plain = ToPlainText(text);
            if (string.IsNullOrWhiteSpace(plain))
            {
                // Nothing left to send (a chunk of pure markup) — Telegram would reject an
                // empty message too, so surface the original failure instead
                throw;
            }

            _logger.LogWarning(ex, "Telegram rejected the HTML formatting — resending as plain text");
            await client.SendMessage(chatId, plain, linkPreviewOptions: noPreview, replyMarkup: markup);
        }
    }

    internal static bool IsHtmlParseFailure(Exception ex) =>
        ex.Message.Contains("parse entities", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("unsupported start tag", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("unclosed start tag", StringComparison.OrdinalIgnoreCase);

    // Only Telegram's own tags are stripped — a blanket <[^>]+> would swallow everything
    // between a stray "<" and the next real tag, which is exactly the text this fallback
    // exists to rescue ("CPI came in <2%, and <b>IWDA</b> held up")
    // The attribute clause has to look like attributes, not "any junk up to the next >" —
    // otherwise "<a whisker above 4% while equities rose >1%" reads as one long tag and the
    // sentence disappears
    private const string TelegramTagPattern =
        @"</?(?:b|strong|i|em|u|ins|s|strike|del|code|pre|span|tg-spoiler|blockquote|a)"
        // Attributes must carry a value (Telegram's own are href/class/translate/language),
        // so "x<b and y>c" stays prose instead of parsing as a tag with two bare attributes
        + @"(?:\s+[A-Za-z-]+\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+))*\s*/?>";

    // Links become "label (url)" so nothing is lost, then tags go and entities decode
    internal static string ToPlainText(string html)
    {
        const System.Text.RegularExpressions.RegexOptions options =
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Singleline;

        var withLinks = System.Text.RegularExpressions.Regex.Replace(
            html, """<a\s[^>]*href\s*=\s*("[^"]*"|'[^']*')[^>]*>(.*?)</a>""",
            m => $"{m.Groups[2].Value} ({m.Groups[1].Value.Trim('"', '\'')})", options);
        var withoutTags = System.Text.RegularExpressions.Regex.Replace(withLinks, TelegramTagPattern, "", options);
        return System.Net.WebUtility.HtmlDecode(withoutTags);
    }

    // Test seam: HttpClient handed to TelegramBotClient so tests can fake the Bot API.
    // Never set in production.
    internal HttpClient? BotHttpClient { get; set; }

    private TelegramBotClient CreateBotClient()
    {
        var botToken = Environment.GetEnvironmentVariable("Telegram__BotToken")
            ?? throw new InvalidOperationException("Telegram bot token not configured");

        return new TelegramBotClient(botToken, BotHttpClient);
    }

    private (TelegramBotClient client, string chatId) GetClientAndChatId(string chatIdVariable)
    {
        var client = CreateBotClient();
        var chatId = Environment.GetEnvironmentVariable(chatIdVariable)
            ?? throw new InvalidOperationException($"Telegram chat ID not configured ({chatIdVariable})");

        return (client, chatId);
    }

    internal static List<string> SplitMessage(string message)
    {
        if (message.Length <= MaxMessageLength)
            return [message];

        var chunks = new List<string>();
        var remaining = message;

        while (remaining.Length > 0)
        {
            if (remaining.Length <= MaxMessageLength)
            {
                chunks.Add(remaining);
                break;
            }

            var splitIndex = remaining.LastIndexOf('\n', MaxMessageLength);
            if (splitIndex <= 0)
                splitIndex = MaxMessageLength;

            chunks.Add(remaining[..splitIndex]);
            remaining = remaining[splitIndex..].TrimStart('\n');
        }

        return chunks;
    }
}
