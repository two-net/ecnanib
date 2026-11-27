using Ecnanib.Platform.BackgroundWork;

namespace Ecnanib.Agents;

/// <summary>
/// Has the users whose agents run where the exchange is out told of it, and told when it answers
/// again, every minute (NFR-REL-4). See <see cref="ExchangeOutageAlerts"/> and "A Binance
/// outage" in docs/reliability.md.
/// </summary>
/// <remarks>
/// It asks the gateway what it knows, which is in memory, and reads which agents run only when
/// there is something to tell. A run calls no exchange. A run that fails tells with the next:
/// what was told is marked only after the telling.
/// </remarks>
internal sealed class ExchangeOutageAlertsJob(ExchangeOutageAlerts alerts) : IBackgroundJob
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "exchange-outage-alerts";

    /// <summary>The time between two runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.Every(Interval);

    public Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        return alerts.TellAsync(cancellationToken);
    }
}
