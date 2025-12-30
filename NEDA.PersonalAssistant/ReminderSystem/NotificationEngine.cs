// Basic usage with DI;
using NEDA.Interface.VisualInterface.NotificationSystem;

public class UserService;
{
    private readonly INotificationEngine _notificationEngine;

    public UserService(INotificationEngine notificationEngine)
    {
        _notificationEngine = notificationEngine;
    }

    public async Task SendWelcomeNotification(string userId, string userName)
    {
        // Using template;
        var notification = _notificationEngine.CreateNotificationFromTemplate(
            "welcome",
            userId,
            new Dictionary<string, object>
            {
                { "UserName", userName }
            });

        await _notificationEngine.SendNotificationAsync(notification);
    }

    public async Task SendPasswordResetNotification(string userId, string email)
    {
        await _notificationEngine.SendInformationAsync(
            userId,
            "Password Reset",
            $"A password reset link has been sent to {email}",
            NotificationCategory.Security);
    }
}

// Event handling;
public class AnalyticsEventHandler : INotificationEventHandler;
{
    public async Task OnNotificationSentAsync(Notification notification, NotificationResult result)
    {
        // Track in analytics;
        await Analytics.TrackEventAsync("notification_sent", new;
        {
            notification_id = notification.Id,
            user_id = notification.UserId,
            type = notification.NotificationType,
            success = result.Success;
        });
    }
}
