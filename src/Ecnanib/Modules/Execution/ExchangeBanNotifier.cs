using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Tells administrators when Binance bans this server's address (EXT-BIN-04, FR-NTF-01). The adapter
/// can't use Notifications, so it reports the ban here. The notification is saved as a job run, so the
/// request that found the ban doesn't wait for the database, and a failure is recorded like any other.
/// </summary>
internal sealed class ExchangeBanNotifier(IJobRunner jobs, INotifications notifications) : IExchangeBanObserver
{
    public const string JobName = "exchange-ban-notification";

    /// <summary>The system health page (FR-ADM-04), which #49 adds.</summary>
    public const string Link = "/admin/health";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    // RunAsync never throws for the work's failure, so the task can be left to run.
    public void OnBanned(ExchangeBan ban) =>
        _ = jobs.RunAsync(JobName, Timeout, (_, cancellationToken) => notifications.RaiseForAdministratorsAsync(
            new AdministratorNotice(NotificationType.ExchangeRateLimitBan, Link, ExchangeServers.Describe(ban.Server)),
            cancellationToken));
}
