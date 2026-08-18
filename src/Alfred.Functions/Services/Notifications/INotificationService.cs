namespace Alfred.Functions.Services.Notifications;

// A single inline button under a Telegram message; CallbackData comes back
// to the webhook when pressed (Telegram caps it at 64 bytes)
public record NotificationButton(string Text, string CallbackData);

public interface INotificationService
{
    Task SendAlertAsync(string message);
    Task SendErrorAsync(string errorMessage);
    Task SendMessageAsync(long chatId, string message);
    Task SendPersonalAlertAsync(string message, IReadOnlyList<NotificationButton>? buttons = null);
    Task SendPersonalErrorAsync(string errorMessage);
    Task AnswerCallbackAsync(string callbackQueryId, string? text = null);
}
