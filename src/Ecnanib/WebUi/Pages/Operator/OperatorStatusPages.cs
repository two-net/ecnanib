using System.Globalization;
using Ecnanib.Catalog;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Availability;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Persistence;

namespace Ecnanib.WebUi.Pages.Operator;

/// <summary>
/// The texts of what the operator status screen shows, in one place (NFR-USA-4): the health of
/// the scheduler, the depth of the queue of the cycles, the last catalog sync of each provider,
/// the use of the request limits of Binance, the drift of the clock, the sizes of the tables,
/// and the rates of errors (FR-OPS-2), and with them where the calls to the AI providers are
/// paused and where Binance is out (NFR-REL-4), and how available the service was (NFR-REL-1).
/// What frames the screen, and the pages that it leads to, have their texts in
/// <see cref="OperatorPages"/>.
/// </summary>
/// <remarks>
/// A state is said as words, and never as a colour alone. What is not known is said as not
/// known, and never as nothing or as a number. Every time is written by the times of its page.
/// </remarks>
public static class OperatorStatusPages
{
    /// <summary>The id of the heading of the availability, which names its table.</summary>
    public const string AvailabilityId = "status-availability";

    /// <summary>The id of the heading of the scheduler, which names its table.</summary>
    public const string SchedulerId = "status-scheduler";

    /// <summary>The id of the heading of the queue of the cycles.</summary>
    public const string QueueId = "status-queue";

    /// <summary>The id of the heading of the catalog syncs.</summary>
    public const string CatalogId = "status-catalog";

    /// <summary>The id of the heading of the calls to the AI providers.</summary>
    public const string CallsId = "status-ai-calls";

    /// <summary>The id of the heading of the request limits.</summary>
    public const string LimitsId = "status-limits";

    /// <summary>The id of the heading of the clock.</summary>
    public const string ClockId = "status-clock";

    /// <summary>The id of the heading of the outages of Binance.</summary>
    public const string OutagesId = "status-outages";

    /// <summary>The id of the heading of the tables.</summary>
    public const string TablesId = "status-tables";

    /// <summary>The id of the heading of the errors and warnings.</summary>
    public const string ProblemsId = "status-problems";

    /// <summary>When the screen was written: it follows nothing, so it says of when it is.</summary>
    public static string AsItStood(DateTimeOffset now, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(times);

        return $"This is how the service stood on {times.AtSecond(now)}. Load the page again for how it stands now.";
    }

    /// <summary>Which of the readings are of one instance alone.</summary>
    public static string OfThisInstance =>
        "The queue of the cycles, the pauses of the calls to the AI providers, the request limits, the clock, the outages of " +
        "Binance, and the counts of errors are kept in memory by the instance that answered this page. The rest is read from " +
        "the database.";

    /// <summary>What is written in place of something that is not known.</summary>
    public static string NotKnown => "Not known";

    /// <summary>What is written in place of something that there is none of.</summary>
    public static string None => "None";

    /// <summary>A count, with its thousands apart.</summary>
    public static string Count(long count) => count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The heading of the availability of the service.</summary>
    public static string AvailabilityHeading => "Availability";

    /// <summary>What the availability is the share of, and what it is held to (NFR-REL-1).</summary>
    /// <param name="targetPercent">The target of the configuration file, in percent.</param>
    public static string AvailabilityExplained(decimal targetPercent) =>
        "The share of each month, by the calendar in UTC, that the service was up for, held against a target of " +
        $"{targetPercent.ToString("0.###", CultureInfo.InvariantCulture)}%. The service is up while an instance runs the " +
        "background work and the database answers it, which it says every minute.";

    /// <summary>What counts as down, and what is left out.</summary>
    public static string AvailabilityCounts =>
        "Time that nothing said so for counts as down. Maintenance that was announced in advance is left out, and " +
        "maintenance that was announced late is not. The figures are read from the database.";

    /// <summary>The name of the table of the months.</summary>
    public static string AvailabilityCaption => "The availability of the service by the month";

    /// <summary>The heading of a month.</summary>
    public static string MonthHeading => "Month";

    /// <summary>The heading of the share of a month that the service was up for.</summary>
    public static string AvailableHeading => "Availability";

    /// <summary>The heading of whether that is the target or more.</summary>
    public static string TargetHeading => "Target";

    /// <summary>The heading of how long the service was down for.</summary>
    public static string DownHeading => "Down";

    /// <summary>The heading of how long the maintenance lasted that is left out.</summary>
    public static string MaintenanceHeading => "Maintenance left out";

    /// <summary>The heading of from when to when a month was measured.</summary>
    public static string MeasuredHeading => "Measured";

    /// <summary>A month of the calendar, by its name and its year.</summary>
    public static string Month(DateOnly month) => month.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// The share of a month that the service was up for, in percent. It is cut after three
    /// places and never rounded up, so it says no more than was measured. A month that nothing
    /// was measured of is not known.
    /// </summary>
    public static string Available(MonthAvailability month)
    {
        ArgumentNullException.ThrowIfNull(month);

        return month.Percent is { } percent
            ? (Math.Floor(percent * 1000m) / 1000m).ToString("0.###", CultureInfo.InvariantCulture) + "%"
            : NotKnown;
    }

    /// <summary>Whether a month is at its target, in words.</summary>
    public static string Target(MonthAvailability month)
    {
        ArgumentNullException.ThrowIfNull(month);

        return month.MeetsTarget switch
        {
            true => "Met",
            false => "Missed",
            null => NotKnown,
        };
    }

    /// <summary>How long the service was down for in a month, or that this is not known.</summary>
    public static string Down(MonthAvailability month)
    {
        ArgumentNullException.ThrowIfNull(month);

        return month.Percent is null ? NotKnown : Lasting(month.Down);
    }

    /// <summary>How long the maintenance lasted that is left out of a month, or that this is not known.</summary>
    public static string Maintenance(MonthAvailability month)
    {
        ArgumentNullException.ThrowIfNull(month);

        return month.MeasuredFrom is null ? NotKnown : Lasting(month.Maintenance);
    }

    /// <summary>From when to when a month was measured.</summary>
    public static string Measured(MonthAvailability month, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(month);
        ArgumentNullException.ThrowIfNull(times);

        return month is { MeasuredFrom: { } from, MeasuredUntil: { } until }
            ? $"From {times.At(from)} to {times.At(until)}"
            : "Nothing of it was measured";
    }

    /// <summary>A length of time in days, hours, minutes, and seconds, without the parts that are nothing.</summary>
    public static string Lasting(TimeSpan time)
    {
        if (time < TimeSpan.FromSeconds(1))
        {
            return None;
        }

        string[] parts =
        [
            time.Days > 0 ? Count(time.Days) + " d" : string.Empty,
            time.Hours > 0 ? Count(time.Hours) + " h" : string.Empty,
            time.Minutes > 0 ? Count(time.Minutes) + " min" : string.Empty,
            time.Seconds > 0 ? Count(time.Seconds) + " s" : string.Empty,
        ];

        return string.Join(' ', parts.Where(part => part.Length > 0));
    }

    /// <summary>The heading of the health of the scheduler.</summary>
    public static string SchedulerHeading => "Scheduler";

    /// <summary>What the table of the jobs holds, and what makes a job overdue.</summary>
    public static string SchedulerExplained =>
        "Each scheduled job with its latest run, as the database has it. A scheduler that runs starts a job within a second " +
        "of when it is due.";

    /// <summary>The health of the scheduler in one sentence: how many jobs are overdue, and what that says.</summary>
    public static string SchedulerHealth(IReadOnlyList<BackgroundJobStatus> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        const string means =
            "no instance runs the background work, a run outlasts its schedule, or the work has just started and still settles and reconciles first.";

        return jobs.Count(job => job.Overdue) switch
        {
            0 => "No job is overdue.",
            1 => $"1 job is overdue: {means}",
            var overdue => $"{Count(overdue)} jobs are overdue: {means}",
        };
    }

    /// <summary>The name of the table of the jobs.</summary>
    public static string JobsCaption => "The scheduled jobs";

    /// <summary>The heading of the name of a job.</summary>
    public static string JobHeading => "Job";

    /// <summary>The heading of when a job runs.</summary>
    public static string ScheduleHeading => "Schedule";

    /// <summary>The heading of when the latest run of a job started.</summary>
    public static string StartedHeading => "Latest run started";

    /// <summary>The heading of when that run ended.</summary>
    public static string EndedHeading => "Ended";

    /// <summary>The heading of how that run ended.</summary>
    public static string OutcomeHeading => "How it ended";

    /// <summary>The heading of when the next run of a job is due.</summary>
    public static string DueHeading => "Next run";

    /// <summary>When a job runs, or that this version of the application does not have it.</summary>
    public static string Schedule(BackgroundJobSchedule? schedule) => schedule switch
    {
        null => "Not a job of this version",
        { OnUtcBoundaries: true } => $"{Every(schedule.Period)}, on the UTC clock",
        _ => Every(schedule.Period),
    };

    /// <summary>When the latest run of a job started, or that it has not run yet.</summary>
    public static string Started(BackgroundJobStatus job, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(times);

        return job.LastStartedAt is { } at ? times.AtSecond(at) : NameOf(BackgroundJobRunState.NoRunYet);
    }

    /// <summary>When the latest run of a job ended, or that no end of it is recorded.</summary>
    public static string Ended(BackgroundJobStatus job, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(times);

        return job switch
        {
            { LastFinishedAt: { } at } => times.AtSecond(at),
            { State: BackgroundJobRunState.NoRunYet } => None,
            _ => "No end recorded",
        };
    }

    /// <summary>
    /// What the database says of the latest run of a job. A run with no end is still going, or
    /// the process ended before it did: the database cannot tell which, and neither does this.
    /// </summary>
    public static string NameOf(BackgroundJobRunState state) => state switch
    {
        BackgroundJobRunState.NoRunYet => "No run yet",
        BackgroundJobRunState.NotEnded => "Still going, or cut off",
        BackgroundJobRunState.Succeeded => "Succeeded",
        BackgroundJobRunState.Failed => "Failed. The error is in the log.",
        BackgroundJobRunState.Stopped => "Stopped by the application",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "The state of a run has no text."),
    };

    /// <summary>When the next run of a job is due, and whether it is overdue.</summary>
    public static string Due(BackgroundJobStatus job, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(times);

        return job switch
        {
            { Schedule: null } => NotKnown,
            { DueAt: null } => "Due at once",
            { Overdue: true, DueAt: { } at } => $"Overdue since {times.AtSecond(at)}",
            { DueAt: { } at } => times.AtSecond(at),
        };
    }

    /// <summary>The heading of the depth of the queue of the cycles.</summary>
    public static string QueueHeading => "Queue of the cycles";

    /// <summary>What the queue is of.</summary>
    public static string QueueExplained =>
        "The cycles of the candle close that this instance runs now, by how far each has come. A test cycle runs outside the queue.";

    /// <summary>That no close is under way on the instance that answered.</summary>
    public static string NoCloseUnderWay =>
        "No close is under way: the cycles of the last close are over, or this instance stands by.";

    /// <summary>The name of the table of the queue.</summary>
    public static string QueueCaption => "The cycles of the close that is under way";

    /// <summary>The heading of the close that the cycles are of.</summary>
    public static string CloseHeading => "Close";

    /// <summary>The heading of the cycles whose candles the exchange has not given yet.</summary>
    public static string AwaitingCandlesHeading => "Waiting for candles";

    /// <summary>The heading of the cycles that wait for a place with their providers and keys.</summary>
    public static string QueuedHeading => "Waiting for a place";

    /// <summary>The heading of the cycles that run.</summary>
    public static string RunningHeading => "Running";

    /// <summary>The heading of the last catalog sync of each provider.</summary>
    public static string CatalogHeading => "Catalog syncs";

    /// <summary>What the table of the syncs holds.</summary>
    public static string CatalogExplained =>
        "The latest sync of the catalog of each AI provider, and the latest one that succeeded, which is what the catalog holds.";

    /// <summary>The name of the table of the syncs.</summary>
    public static string SyncsCaption => "The latest catalog sync of each provider";

    /// <summary>The heading of which key the catalog of a provider is synced with.</summary>
    public static string SyncedWithHeading => "Synced with";

    /// <summary>The heading of the latest sync of a provider.</summary>
    public static string LatestSyncHeading => "Latest sync";

    /// <summary>The heading of how the latest sync ended.</summary>
    public static string SyncResultHeading => "How it ended";

    /// <summary>The heading of what the latest sync counted.</summary>
    public static string SyncCountsHeading => "Models";

    /// <summary>The heading of the latest sync that succeeded.</summary>
    public static string LatestSucceededHeading => "Latest sync that succeeded";

    /// <summary>
    /// Which key the catalog of a provider is synced with, and so when: the key that the
    /// configuration file holds for it, or the keys of users (FR-CAT-3). It says nothing of a key.
    /// </summary>
    public static string SyncedWith(bool hasCatalogKey) => hasCatalogKey
        ? "The key of the service, on the schedule and on demand"
        : "The keys of users, as each is checked";

    /// <summary>When a sync started and why, or that the provider was never synced.</summary>
    public static string Sync(CatalogSyncRun? run, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(times);

        if (run is null)
        {
            return "Not synced yet";
        }

        var why = run.Cause switch
        {
            CatalogSyncCause.Scheduled => "on the schedule",
            CatalogSyncCause.OnDemand => "on demand",
            CatalogSyncCause.KeyValidated => "with the key of a user",
            _ => throw new ArgumentOutOfRangeException(nameof(run), run.Cause, "The cause of a sync has no text."),
        };

        return $"{times.At(run.StartedAt)}, {why}";
    }

    /// <summary>How a sync ended, and of one that failed why, in the word that it was recorded with (FR-CAT-7).</summary>
    public static string Result(CatalogSyncRun? run) => run switch
    {
        null => None,
        { Result: CatalogSyncResult.Succeeded } => "Succeeded",
        { Failure: CatalogSyncFailure.KeyRefused } => "Failed: the provider refused the key",
        { Failure: CatalogSyncFailure.TimedOut } => "Failed: the provider took too long",
        { Failure: CatalogSyncFailure.NoList } => "Failed: the provider gave no list of its models",
        { Failure: CatalogSyncFailure.Error } => "Failed: the list could not be stored. The error is in the log.",
        _ => "Failed",
    };

    /// <summary>How many models a sync added, changed, and marked unavailable.</summary>
    public static string SyncCounts(CatalogSyncRun? run) => run is null
        ? None
        : $"{Count(run.Added)} added, {Count(run.Changed)} changed, {Count(run.Unavailable)} marked unavailable";

    /// <summary>When the latest sync that succeeded started, or that none did.</summary>
    public static string Succeeded(CatalogSyncRun? run, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(times);

        return run is null ? "None yet" : times.At(run.StartedAt);
    }

    /// <summary>The heading of the calls to the AI providers.</summary>
    public static string CallsHeading => "Calls to the AI providers";

    /// <summary>What the table of the calls holds, and what a pause is.</summary>
    public static string CallsExplained =>
        "Whether the calls to the models of each provider are made. After failures in a row the calls to a provider, of all " +
        "users, are paused for a while and then made again, and so are the calls with one key that a provider keeps refusing. " +
        "A key is counted, and is shown nowhere.";

    /// <summary>The name of the table of the calls.</summary>
    public static string CallsCaption => "The calls to each AI provider";

    /// <summary>The heading of an AI provider.</summary>
    public static string CallProviderHeading => "Provider";

    /// <summary>The heading of whether the calls to a provider are made.</summary>
    public static string CallStateHeading => "Calls";

    /// <summary>The heading of the calls in a row that a provider left without an answer.</summary>
    public static string CallFailuresHeading => "Failures in a row";

    /// <summary>The heading of the pauses in a row of the calls to a provider.</summary>
    public static string CallTripsHeading => "Pauses in a row";

    /// <summary>The heading of the keys that a provider keeps refusing.</summary>
    public static string KeysInTroubleHeading => "Keys that it refuses";

    /// <summary>The heading of the keys that no call is made with now.</summary>
    public static string KeysPausedHeading => "Of them paused now";

    /// <summary>Whether the calls to a provider are made, and until when they are not (NFR-REL-4).</summary>
    public static string NameOf(CallPause pause, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentNullException.ThrowIfNull(times);

        return pause switch
        {
            { PausedUntil: { } until } => $"Paused until {times.AtSecond(until)}: the provider left calls without an answer",
            { Trips: > 0 } => "Made again after a pause: the next failure pauses them again",
            _ => "Made",
        };
    }

    /// <summary>The heading of the use of the request limits of Binance.</summary>
    public static string LimitsHeading => "Request limits of Binance";

    /// <summary>What the table of the limits holds, and whose the limits are.</summary>
    public static string LimitsExplained =>
        "The request weight that the service used of each limit in the last minute, as its own limiter counts it. Binance " +
        "counts each limit against the address of the service, for all users together.";

    /// <summary>The name of the table of the limits.</summary>
    public static string LimitsCaption => "The use of each request limit";

    /// <summary>The heading of what a limit is on.</summary>
    public static string LimitHeading => "Limit";

    /// <summary>The heading of the environment of a limit or of a clock: the real exchange, or the test environment.</summary>
    public static string EnvironmentHeading => "Environment";

    /// <summary>The heading of the request weight that the service used.</summary>
    public static string UsedHeading => "Used";

    /// <summary>The heading of the part of a limit that the service allows itself.</summary>
    public static string AllowedHeading => "Allowed to the service";

    /// <summary>The heading of what Binance allows the address of the service.</summary>
    public static string LimitOfBinanceHeading => "Limit of Binance";

    /// <summary>The heading of what Binance itself last said that it counted.</summary>
    public static string ReportedHeading => "Counted by Binance";

    /// <summary>The heading of the calls that wait for their turn.</summary>
    public static string WaitingHeading => "Calls waiting";

    /// <summary>The heading of whether calls are held back because Binance signalled too many.</summary>
    public static string HeldHeading => "Held back";

    /// <summary>The request weight that was used, and how much that is of what the service allows itself.</summary>
    public static string Used(ExchangeLimitUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        return usage.Allowed > 0
            ? $"{Count(usage.Used)}, which is {(usage.Used * 100m / usage.Allowed).ToString("0.#", CultureInfo.InvariantCulture)} percent of what is allowed"
            : Count(usage.Used);
    }

    /// <summary>What Binance last said that it had counted, and when, or that it has not said.</summary>
    public static string Reported(ExchangeLimitUsage usage, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(times);

        return usage switch
        {
            { Reported: null } => "Not said yet",
            { Reported: { } weight, ReportedAt: { } at } => $"{Count(weight)}, on {times.AtSecond(at)}",
            { Reported: { } weight } => Count(weight),
        };
    }

    /// <summary>Until when no call is sent, because Binance signalled that it has had too many, or that calls are sent.</summary>
    public static string Held(ExchangeLimitUsage usage, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(times);

        return usage.HeldUntil is { } until ? $"Until {times.AtSecond(until)}: Binance signalled too many requests" : "No";
    }

    /// <summary>The heading of the drift of the clock.</summary>
    public static string ClockHeading => "Clock";

    /// <summary>What the table of the clocks holds, and what follows from a clock that is off.</summary>
    public static string ClockExplained =>
        "How far the clock of the service is from each clock of Binance. New entries are paused on a market while its clock " +
        "is not within the tolerance.";

    /// <summary>The name of the table of the clocks.</summary>
    public static string ClocksCaption => "The clock of the service against each clock of Binance";

    /// <summary>The heading of the market of a clock.</summary>
    public static string MarketHeading => "Market";

    /// <summary>The heading of how a clock stands.</summary>
    public static string ClockStateHeading => "State";

    /// <summary>The heading of how far the clock of the service is from a clock of Binance.</summary>
    public static string DriftHeading => "Drift";

    /// <summary>The heading of how long Binance took to answer a reading.</summary>
    public static string RoundTripHeading => "Round trip";

    /// <summary>The heading of when a clock was last read.</summary>
    public static string ReadHeading => "Read";

    /// <summary>The heading of how far the clock of the service may be from a clock of Binance.</summary>
    public static string ToleranceHeading => "Tolerance";

    /// <summary>How the clock of the service stands against a clock of Binance, and what follows from it (FR-BIN-5).</summary>
    public static string NameOf(ExchangeClockState state) => state switch
    {
        ExchangeClockState.Unknown => "Not read: new entries are paused",
        ExchangeClockState.Within => "Within the tolerance",
        ExchangeClockState.Exceeded => "Beyond the tolerance: new entries are paused",
        ExchangeClockState.Refused => "Binance refused a call for its time: new entries are paused",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "The state of a clock has no text."),
    };

    /// <summary>How far the clock of the service was from a clock of Binance at the latest reading, and which way.</summary>
    public static string Drift(TimeSpan? drift) => drift switch
    {
        null => NotKnown,
        { } ahead when ahead > TimeSpan.Zero => $"{Milliseconds(ahead)} ahead of Binance",
        { } behind when behind < TimeSpan.Zero => $"{Milliseconds(behind.Negate())} behind Binance",
        _ => None,
    };

    /// <summary>A short span of time, in milliseconds, or that it is not known.</summary>
    public static string Milliseconds(TimeSpan? span)
    {
        if (span is not { } known)
        {
            return NotKnown;
        }

        var milliseconds = Math.Round(known.TotalMilliseconds, 1);

        return milliseconds == 1 ? "1 millisecond" : $"{milliseconds.ToString("0.#", CultureInfo.InvariantCulture)} milliseconds";
    }

    /// <summary>When a clock was last read, or that it has not been read.</summary>
    public static string Read(DateTimeOffset? at, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(times);

        return at is { } read ? times.AtSecond(read) : "Not read yet";
    }

    /// <summary>The heading of the outages of Binance.</summary>
    public static string OutagesHeading => "Outages of Binance";

    /// <summary>What the table of the outages holds, and what follows from an outage.</summary>
    public static string OutagesExplained =>
        "Whether Binance answers the calls of the service on each market. While it is out on a market, new entries are " +
        "paused there, and the users whose agents run there are told. Every other call goes on being made: what is open " +
        "is protected and closed whenever Binance takes it.";

    /// <summary>The name of the table of the outages.</summary>
    public static string OutagesCaption => "Whether Binance answers on each market";

    /// <summary>The heading of whether Binance answers.</summary>
    public static string AnswersHeading => "State";

    /// <summary>The heading of the calls in a row that Binance left without an answer.</summary>
    public static string UnansweredHeading => "Calls in a row without an answer";

    /// <summary>The heading of when Binance last answered a call.</summary>
    public static string AnsweredHeading => "Last answered";

    /// <summary>Whether Binance answers on a market, and since when it does not, with what follows (NFR-REL-4).</summary>
    public static string NameOf(ExchangeAnswers answers, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(times);

        return answers.OutageSince is { } since ? $"Out since {times.AtSecond(since)}: new entries are paused" : "Answers";
    }

    /// <summary>When Binance last answered a call on a market, or that it has answered none since the start.</summary>
    public static string Answered(DateTimeOffset? at, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(times);

        return at is { } answered ? times.AtSecond(answered) : "Not since the start";
    }

    /// <summary>The heading of the sizes of the tables.</summary>
    public static string TablesHeading => "Tables";

    /// <summary>What the table of the sizes holds.</summary>
    public static string TablesExplained =>
        "What each table of the database takes on disk, with its indexes, the largest first.";

    /// <summary>What the tables take together.</summary>
    public static string TotalSize(IReadOnlyList<TableSize> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        return $"The {Count(tables.Count)} tables take {Size(tables.Sum(table => table.Bytes))} together.";
    }

    /// <summary>The name of the table of the sizes.</summary>
    public static string TablesCaption => "The size of each table";

    /// <summary>The heading of the name of a table of the database.</summary>
    public static string TableHeading => "Table";

    /// <summary>The heading of what a table takes on disk.</summary>
    public static string SizeHeading => "Size";

    /// <summary>A size on disk, in the largest unit that it is at least one of. A unit is 1,024 of the one below it.</summary>
    public static string Size(long bytes)
    {
        const double kilobyte = 1024;
        const double megabyte = kilobyte * 1024;
        const double gigabyte = megabyte * 1024;

        return bytes switch
        {
            1 => "1 byte",
            < (long)kilobyte => $"{Count(bytes)} bytes",
            < (long)megabyte => $"{In(bytes / kilobyte)} kB",
            < (long)gigabyte => $"{In(bytes / megabyte)} MB",
            _ => $"{In(bytes / gigabyte)} GB",
        };

        static string In(double units) => units.ToString("#,0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>The heading of the rates of errors.</summary>
    public static string ProblemsHeading => "Errors and warnings";

    /// <summary>What is counted, by whom, and since when.</summary>
    public static string ProblemsExplained(DateTimeOffset since, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(times);

        return "The errors and warnings that this instance logged, by the area of the application that logged them. It counts " +
            $"since it started, on {times.At(since)}. What went wrong is in the log.";
    }

    /// <summary>That nothing was counted.</summary>
    public static string NoProblems => "No error and no warning was logged in the last 24 hours.";

    /// <summary>The name of the table of the counts.</summary>
    public static string ProblemsCaption => "The errors and warnings of each area";

    /// <summary>The heading of the area that logged.</summary>
    public static string AreaHeading => "Area";

    /// <summary>The heading of the errors of the last hour.</summary>
    public static string ErrorsLastHourHeading => "Errors, last hour";

    /// <summary>The heading of the warnings of the last hour.</summary>
    public static string WarningsLastHourHeading => "Warnings, last hour";

    /// <summary>The heading of the errors of the last 24 hours.</summary>
    public static string ErrorsLastDayHeading => "Errors, last 24 hours";

    /// <summary>The heading of the warnings of the last 24 hours.</summary>
    public static string WarningsLastDayHeading => "Warnings, last 24 hours";

    /// <summary>What was logged outside the application's own code: by the framework and the libraries.</summary>
    public static string OutsideTheApplication => "Outside the application";

    /// <summary>How often a job runs, in the largest unit that its period is whole in.</summary>
    private static string Every(TimeSpan period)
    {
        var (amount, unit) = period switch
        {
            _ when period.Ticks % TimeSpan.TicksPerDay == 0 => (period.TotalDays, "day"),
            _ when period.Ticks % TimeSpan.TicksPerHour == 0 => (period.TotalHours, "hour"),
            _ when period.Ticks % TimeSpan.TicksPerMinute == 0 => (period.TotalMinutes, "minute"),
            _ => (period.TotalSeconds, "second"),
        };

        return amount == 1 ? $"Every {unit}" : $"Every {amount.ToString("0.###", CultureInfo.InvariantCulture)} {unit}s";
    }
}
