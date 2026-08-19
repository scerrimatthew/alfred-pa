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
            await client.SendMessage(chatId, chunk, parseMode: ParseMode.Html, linkPreviewOptions: new Telegram.Bot.Types.LinkPreviewOptions { IsDisabled = true });
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
            await client.SendMessage(chatId, chunks[i], parseMode: ParseMode.Html,
                linkPreviewOptions: new Telegram.Bot.Types.LinkPreviewOptions { IsDisabled = true },
                replyMarkup: i == chunks.Count - 1 ? markup : null);
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
            await client.SendMessage(chatId, chunk, parseMode: ParseMode.Html, linkPreviewOptions: new Telegram.Bot.Types.LinkPreviewOptions { IsDisabled = true });
        }

        _logger.LogInformation("Sent Telegram reply to chat {ChatId} ({Chunks} chunk(s))", chatId, chunks.Count);
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
