namespace Alfred.Functions.Services.Notifications;

public interface INotificationService
{
    Task SendAlertAsync(string message);
    Task SendErrorAsync(string errorMessage);
}
