namespace Alfred.Functions.Services.Notifications;

public interface INotificationService
{
    Task SendAlertAsync(string message);
    Task SendErrorAsync(string errorMessage);
    Task SendMessageAsync(long chatId, string message);
    Task SendPersonalAlertAsync(string message);
    Task SendPersonalErrorAsync(string errorMessage);
}
