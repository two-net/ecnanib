using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Notifications;

public static class NotificationsModule
{
    public static IServiceCollection AddNotifications(this IServiceCollection services)
    {
        services.AddSingleton(provider => NotificationSettings.Read(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<NotificationEmails>();
        services.AddSingleton<NotificationObservers>();
        services.AddSingleton<INotifications, NotificationInbox>();
        services.AddSingleton<IRecurringJob, NotificationEmailJob>();

        return services;
    }
}
