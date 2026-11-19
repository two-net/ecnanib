using Ecnanib.Platform.BackgroundWork;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Follows the account event feed of the exchange (FR-BIN-7), every minute and as the background
/// work starts when there is an account to follow: a run has the gateway follow the accounts in
/// which an agent holds something, and then listens to what the feeds say for most of its
/// minute, reconciling an agent at once when something changed in its account. See
/// <see cref="AccountFeed"/>, and "The account event feed" in docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A run lasts for most of the time between two runs</b>, as a run of the cycles does: what
/// the feeds say is taken as it arrives, and not when a job is next due. It ends well before the
/// next run is due, so that an agent that is being reconciled as it ends is done by then. What
/// the feeds say between two runs waits with the gateway, for a few seconds, and is taken by the
/// next. It is a job of the platform's scheduler, and no service of its own, so that it runs on
/// the one instance that does the background work, and stops with it.
/// </para>
/// <para>
/// A run ends when its time is up by the clock of the application, or when that long has
/// passed in time that no change of the time of day moves, whichever comes first: a clock that
/// is set back does not keep a run going.
/// </para>
/// <para>
/// A run with no account to follow asks nobody, and is over at once.
/// </para>
/// </remarks>
internal sealed class AccountFeedJob(AccountFeed feed, TimeProvider clock, ILogger<AccountFeedJob> logger) : IBackgroundJob, IDueAtStart
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "account-feed";

    /// <summary>The time between two runs, in which an account that is to be followed comes to be, and a feed is said to be wanted still.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    /// <summary>For how long a run listens to the feeds.</summary>
    public static readonly TimeSpan Listens = TimeSpan.FromSeconds(40);

    // How long one wait for a change lasts, after which the clock is looked at again.
    private static readonly TimeSpan Beat = TimeSpan.FromSeconds(1);

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.Every(Every);

    public Task<bool> IsDueAtStartAsync(CancellationToken cancellationToken) => feed.HasAccountsAsync(cancellationToken);

    public async Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var began = clock.GetTimestamp();
        var until = clock.GetUtcNow() + Listens;

        // Nothing is followed: what a feed that has just ended still said is taken, without a
        // wait, and is about agents that hold nothing any more, or that the schedule reconciles.
        var follows = await feed.FollowAsync(cancellationToken) > 0;

        var changes = 0;
        var agents = 0;

        // What the feeds said is taken once at least, however long it took to follow them: a
        // feed that is slow to open keeps nothing that another said from being taken.
        do
        {
            var taken = await feed.TakeAsync(follows ? Beat : TimeSpan.Zero, cancellationToken);

            changes += taken.Changes;
            agents += taken.Agents;
        }
        while (follows && clock.GetElapsedTime(began) < Listens && clock.GetUtcNow() < until);

        if (changes > 0)
        {
            logger.LogInformation(
                "The account event feed said {Changes} changes, and agents were reconciled {Reconciliations} times for them", changes, agents);
        }
    }
}
