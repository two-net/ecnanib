using Ecnanib.Accounts;
using Ecnanib.Notifications.Adapters.Smtp;
using Ecnanib.Platform.BackgroundWork;

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

        // The in-app notifications of users: what raises one, what reads them for their user,
        // and what marks them as read (FR-NTF-1). See docs/notifications.md for what raises one.
        services.AddSingleton<INotifications, StoredNotifications>();

        // Whether a user is sent email for critical events, which the user chooses, and the job
        // that sends the notifications about them, every minute (FR-NTF-3).
        services.AddSingleton<IEmailOptIn, StoredEmailOptIn>();
        services.AddSingleton<NotificationEmails>();
        services.AddBackgroundJob<NotificationEmailJob>();

        // Deletes the notifications that were raised longer ago than they are kept, every hour,
        // in small batches (NFR-DAT-5). See docs/retention.md.
        services.AddSingleton<NotificationRetention>();
        services.AddBackgroundJob<NotificationRetentionJob>();

        // The parts of the export of a user's data that this component keeps (NFR-SEC-9).
        services.AddSingleton<IExportedPart, NotificationsExport>();
        services.AddSingleton<IExportedPart, EmailOptInExport>();

        return services;
    }
}
