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

        // The in-app notifications of users: what raises one, and what reads the ones that a
        // user has not read (FR-NTF-1). What is built is what four events raise, among them
        // the alert of a failure of the protection of a position (FR-RSK-7) and the warning
        // of a position that approaches liquidation (FR-BIN-F5): see docs/notifications.md.
        services.AddSingleton<INotifications, StoredNotifications>();

        // The part of the export of a user's data that this component keeps (NFR-SEC-9).
        services.AddSingleton<IExportedPart, NotificationsExport>();

        return services;
    }
}
