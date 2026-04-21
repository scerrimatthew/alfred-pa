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
        var (client, chatId) = GetClientAndChatId();

        var chunks = SplitMessage(message);
        foreach (var chunk in chunks)
        {
            await client.SendMessage(chatId, chunk, parseMode: ParseMode.Html, linkPreviewOptions: new Telegram.Bot.Types.LinkPreviewOptions { IsDisabled = true });
        }

        _logger.LogInformation("Sent Telegram alert ({Chunks} chunk(s))", chunks.Count);
    }

    public async Task SendErrorAsync(string errorMessage)
    {
        var (client, chatId) = GetClientAndChatId();

        var message = $"⚠️ Alfred encountered an error:\n\n{errorMessage}";
        await client.SendMessage(chatId, message);

        _logger.LogInformation("Sent Telegram error notification");
    }

    private static (TelegramBotClient client, string chatId) GetClientAndChatId()
    {
        var botToken = Environment.GetEnvironmentVariable("Telegram__BotToken")
            ?? throw new InvalidOperationException("Telegram bot token not configured");
        var chatId = Environment.GetEnvironmentVariable("Alfred__TelegramChatId")
            ?? throw new InvalidOperationException("Telegram chat ID not configured");

        return (new TelegramBotClient(botToken), chatId);
    }

    private static List<string> SplitMessage(string message)
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
