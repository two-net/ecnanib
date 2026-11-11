using Ecnanib.Accounts;
using Ecnanib.Notifications.Adapters.Smtp;

namespace Ecnanib.Notifications;

/// <summary>Notifications: in-app and email messages (SRS 3.1).</summary>
public static class NotificationsModule
{
    public static IServiceCollection AddNotifications(this IServiceCollection services)
    {
        // The email delivery service, behind its common interface.
        services.AddSingleton<IEmailAdapter, SmtpEmailAdapter>();

        // Accounts sits below this component, so it declares the emails it needs and this
        // component sends them. See "When a lower component must trigger a higher one" in
        // docs/architecture.md.
        services.AddSingleton<IAccountMailer, AccountMailer>();

        return services;
    }
}
