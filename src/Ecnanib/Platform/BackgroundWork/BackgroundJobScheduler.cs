namespace Ecnanib.Platform.BackgroundWork;

/// <summary>
/// Runs the registered jobs on their schedules, in one instance at a time. An instance runs them
/// only while it holds the background work lock in the database, and it keeps each job's latest
/// run in the database, so a schedule goes on after a restart (NFR-ARC-4). See
/// docs/background-work.md.
/// </summary>
/// <remarks>
/// <para>
/// Version 1 targets a single running instance. The lock makes a second one safe: it stands by
/// and runs nothing, and takes over when the first one stops.
/// </para>
/// <para>
/// Nothing that goes wrong here stops the application. A database that is away is waited for.
/// </para>
/// </remarks>
internal sealed class BackgroundJobScheduler(
    IEnumerable<IBackgroundJob> jobs,
    BackgroundJobStore store,
    BackgroundWorkLock workLock,
    TimeProvider clock,
    ILogger<BackgroundJobScheduler> logger) : BackgroundService
{
    // How often the lock is asked for while another instance holds it, how often it is checked
    // while this one does, and the longest the scheduler sleeps before it looks at the clock
    // again. So a clock that was set, or a machine that slept, is noticed within a beat.
    private static readonly TimeSpan Beat = TimeSpan.FromSeconds(1);

    // A timer can fire a moment early. The scheduler then sleeps again, and never spins.
    private static readonly TimeSpan ShortestSleep = TimeSpan.FromMilliseconds(10);

    private readonly IReadOnlyList<Scheduled> _jobs = Named(jobs);

    // Set by the first report of what keeps the work from going on, so that the same trouble
    // is not reported again every beat. Cleared when the work goes on.
    private bool _troubled;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StandByAsync(stoppingToken);
                await RunTheWorkAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // The application is stopping.
            }
            catch (Exception error)
            {
                // The database is away, most likely. An error that left this method would stop
                // the application, which can do everything else without its background work.
                logger.Log(
                    OnceAt(LogLevel.Error, stoppingToken),
                    error,
                    "The background work cannot go on. This instance tries again every second until it can");
            }
            finally
            {
                await workLock.ReleaseAsync();
            }

            try
            {
                // Also after the lock was lost: whatever happened, it is not tried again at once.
                await Task.Delay(Beat, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Waits for the lock, for as long as another instance holds it.</summary>
    private async Task StandByAsync(CancellationToken stopping)
    {
        var said = false;

        while (!await workLock.TryTakeAsync(stopping))
        {
            // The database answered, so whatever kept this instance from asking is over, and
            // is reported again when it comes back.
            _troubled = false;

            if (!said)
            {
                logger.LogInformation(
                    "Another instance runs the background work. This one stands by, and takes over when that one stops");
                said = true;
            }

            await Task.Delay(Beat, clock, stopping);
        }
    }

    /// <summary>
    /// Runs the jobs for as long as this instance holds the lock. It returns when the lock is
    /// lost, and only after every run it started has ended.
    /// </summary>
    private async Task RunTheWorkAsync(CancellationToken stopping)
    {
        // Canceled on the way out, whatever the reason: a run never outlives the lock it began under.
        using var active = CancellationTokenSource.CreateLinkedTokenSource(stopping);

        try
        {
            // Asked once before anything else, so that a session which cannot keep a lock at
            // all never gets as far as starting a job.
            if (!await StillHoldsTheLockAsync(active.Token))
            {
                return;
            }

            await FollowTheStoredStateAsync(active.Token);

            _troubled = false;
            logger.LogInformation("This instance runs the background work, with {JobCount} jobs", _jobs.Count);

            while (true)
            {
                await StartWhatIsDueAsync(active.Token);
                await Task.Delay(UntilTheNextRun(), clock, active.Token);

                if (!await StillHoldsTheLockAsync(active.Token))
                {
                    return;
                }
            }
        }
        finally
        {
            await active.CancelAsync();

            // A run handles its own errors, so this waits and nothing more.
            await Task.WhenAll(_jobs.Select(job => job.Run));
        }
    }

    private async Task<bool> StillHoldsTheLockAsync(CancellationToken stopping)
    {
        bool held;
        try
        {
            held = await workLock.IsHeldAsync(stopping);
        }
        catch (Exception error) when (!stopping.IsCancellationRequested)
        {
            logger.Log(
                OnceAt(LogLevel.Warning, stopping),
                error,
                "The database session that holds the background work lock has ended or does not answer, so this instance stops its jobs and stands by");

            return false;
        }

        if (!held)
        {
            // The lock belongs to one session. A connection that is handed from session to
            // session between commands, as a pooler in transaction mode does, takes the lock
            // on one and asks another about it.
            logger.Log(
                OnceAt(LogLevel.Warning, stopping),
                "The background work lock is not held by the session that took it, so this instance runs no jobs. " +
                "Connect to the database directly, or through a pooler in session mode");
        }

        return held;
    }

    /// <summary>Works out when each job is next due from its latest run in the database.</summary>
    private async Task FollowTheStoredStateAsync(CancellationToken stopping)
    {
        // How a run of this instance ended may not be in the database yet, when the database
        // was away at the time. It is written first, so that what is read below is all of it.
        foreach (var job in _jobs)
        {
            await RecordAsync(job, stopping);
        }

        var states = await store.LoadAsync(stopping);
        var now = Now();

        foreach (var job in _jobs)
        {
            var state = states.GetValueOrDefault(job.Name);
            job.Follow(state, now);

            if (state is { LastFinishedAt: null })
            {
                // The process ended during the run. It is not repeated at once: a job that
                // brings the process down would do so at every start.
                logger.LogWarning(
                    "Background job {Job} did not finish the run it started at {StartedAt}. It runs again when it is next due, at {NextRun}",
                    job.Name,
                    state.LastStartedAt,
                    job.NextRun);
            }
        }
    }

    private async Task StartWhatIsDueAsync(CancellationToken active)
    {
        var now = Now();

        foreach (var job in _jobs)
        {
            await RecordWhatIsLeftAsync(job, active);

            if (!job.Schedule.SkipsMissedRuns)
            {
                // After the clock was set back, the previous run lies in the future. The job
                // then waits one interval from now, as it would after a restart, and no longer.
                var fromNow = job.Schedule.NextRun(job.PreviousStart, now);
                if (fromNow < job.NextRun)
                {
                    job.NextRun = fromNow;
                }
            }

            if (job.NextRun > now)
            {
                continue;
            }

            if (!job.Run.IsCompleted)
            {
                HoldBack(job, now);
            }
            else if (job.Schedule.SkipsMissedRuns && job.Schedule.NextRun(previousStart: null, job.NextRun) <= now)
            {
                // The boundary after the one that is due has passed as well: the machine slept,
                // the clock was set forward, or the start could not be written for that long.
                // What holds for a restart holds here: a boundary that passed is not made up for.
                logger.LogWarning(
                    "Background job {Job} could not start its run due at {DueAt} before the following one was due, so that run is skipped",
                    job.Name,
                    job.NextRun);

                job.NextRun = job.Schedule.NextRun(job.PreviousStart, now);
            }
            else
            {
                await StartAsync(job, now, active);
            }
        }
    }

    /// <summary>Tries again to record how the job's latest run ended, when that failed as the run ended.</summary>
    private async Task RecordWhatIsLeftAsync(Scheduled job, CancellationToken active)
    {
        // Only once the run is over: until then, what it leaves behind is the run's to write.
        if (!job.Run.IsCompleted)
        {
            return;
        }

        try
        {
            await RecordAsync(job, active);
        }
        catch (Exception error) when (!active.IsCancellationRequested)
        {
            // Said as a warning when the run ended. It is tried again at the next beat.
            logger.LogDebug(error, "How the run of background job {Job} ended can still not be recorded", job.Name);
        }
    }

    /// <summary>Writes how the job's latest run ended, if that is still to be written.</summary>
    private async Task RecordAsync(Scheduled job, CancellationToken cancellationToken)
    {
        if (job.Unrecorded is not { } ending)
        {
            return;
        }

        // Nothing is written when the row is about a later run by now, and nothing needs to be.
        await store.FinishAsync(job.Name, ending.StartedAt, ending.FinishedAt, ending.Outcome, cancellationToken);

        job.Unrecorded = null;
    }

    /// <summary>A job never runs twice at the same time: a run that is due while the one before it is still going does not start.</summary>
    private void HoldBack(Scheduled job, DateTimeOffset now)
    {
        var due = job.NextRun;
        var next = job.Schedule.NextRun(job.PreviousStart, now);

        if (job.HeldBack != due)
        {
            if (next > now)
            {
                logger.LogWarning(
                    "Background job {Job} is still running since {StartedAt}, so its run due at {DueAt} is skipped",
                    job.Name,
                    job.PreviousStart,
                    due);
            }
            else
            {
                logger.LogWarning(
                    "Background job {Job} is still running since {StartedAt}, so its run due at {DueAt} waits until it ends",
                    job.Name,
                    job.PreviousStart,
                    due);
            }

            job.HeldBack = due;
        }

        job.NextRun = next;
    }

    private async Task StartAsync(Scheduled job, DateTimeOffset now, CancellationToken active)
    {
        var due = job.NextRun;
        var startedAt = now;

        try
        {
            if (!await store.TryStartAsync(job.Name, job.PreviousStart, now, active))
            {
                var state = await store.FindAsync(job.Name, active);

                if (state is { LastFinishedAt: null } && job.UnconfirmedStarts.Contains(state.LastStartedAt))
                {
                    // The start in the row is one this instance wrote itself, in an attempt
                    // that failed after the database had taken it. The run is still to do.
                    startedAt = state.LastStartedAt;
                }
                else
                {
                    // This instance held the lock a moment ago, and so did the other one a
                    // moment before that. Its run stands, and this instance goes by it from here on.
                    job.UnconfirmedStarts.Clear();
                    job.Follow(state, now);

                    logger.LogWarning(
                        "Background job {Job} was started by another instance at {StartedAt}, so it does not start here",
                        job.Name,
                        state?.LastStartedAt);

                    return;
                }
            }
        }
        catch (Exception error) when (!active.IsCancellationRequested)
        {
            // Whether the start was written is not known: the connection can fail after the
            // database has taken it. The job stays due and is tried again at the next beat,
            // which finds out. The runs of other jobs go on.
            job.UnconfirmedStarts.Add(now);

            logger.Log(
                job.StartFailing ? LogLevel.Debug : LogLevel.Warning,
                error,
                "Background job {Job} could not be started, because its state could not be written. It is tried again every second",
                job.Name);
            job.StartFailing = true;

            return;
        }

        job.UnconfirmedStarts.Clear();
        job.StartFailing = false;
        job.PreviousStart = startedAt;
        job.NextRun = job.Schedule.NextRun(startedAt, now);

        // The row is about this run from here on, so what the run before it left unwritten is void.
        job.Unrecorded = null;

        logger.LogDebug("Background job {Job} starts its run due at {DueAt}", job.Name, due);

        job.Run = Task.Run(() => RunAsync(job, new BackgroundJobRun(due), startedAt, active), CancellationToken.None);
    }

    /// <summary>Runs a job once and records how the run ended. It does not throw.</summary>
    private async Task RunAsync(Scheduled job, BackgroundJobRun run, DateTimeOffset startedAt, CancellationToken active)
    {
        var began = clock.GetTimestamp();
        BackgroundJobOutcome outcome;

        try
        {
            await job.Job.RunAsync(run, active);

            outcome = BackgroundJobOutcome.Succeeded;
            logger.LogInformation("Background job {Job} finished in {DurationMs} ms", job.Name, Milliseconds(began));
        }
        catch (OperationCanceledException) when (active.IsCancellationRequested)
        {
            outcome = BackgroundJobOutcome.Stopped;
            logger.LogInformation(
                "Background job {Job} was stopped after {DurationMs} ms, before it was done",
                job.Name,
                Milliseconds(began));
        }
        catch (Exception error)
        {
            // Logged here, once, because nothing above this handles it. The job is not tried
            // again before its time: a failure that repeats must not become a loop.
            outcome = BackgroundJobOutcome.Failed;
            logger.LogError(
                error,
                "Background job {Job} failed after {DurationMs} ms. It runs again when it is next due",
                job.Name,
                Milliseconds(began));
        }

        // Kept until it is written. When the database is away as the run ends, which is also
        // what stops a run when the lock is lost with it, the scheduler writes it later.
        job.Unrecorded = new Ending(startedAt, Now(), outcome);

        try
        {
            // Not canceled with the run: how a stopped run ended is recorded too.
            await RecordAsync(job, CancellationToken.None);
        }
        catch (Exception error)
        {
            logger.LogWarning(
                error,
                "How the run of background job {Job} ended could not be recorded. It is tried again every second",
                job.Name);
        }
    }

    /// <summary>Until the next job is due, and a beat at most.</summary>
    private TimeSpan UntilTheNextRun()
    {
        var now = Now();
        var sleep = Beat;

        foreach (var job in _jobs)
        {
            var until = job.NextRun - now;
            if (until > TimeSpan.Zero && until < sleep)
            {
                sleep = until;
            }
        }

        return sleep < ShortestSleep ? ShortestSleep : sleep;
    }

    /// <summary>
    /// The time, to the microsecond, which is what the database keeps. So a time that was
    /// written and read back equals the one in memory.
    /// </summary>
    private DateTimeOffset Now()
    {
        var ticks = clock.GetUtcNow().UtcTicks;

        return new DateTimeOffset(ticks - (ticks % TimeSpan.TicksPerMicrosecond), TimeSpan.Zero);
    }

    private double Milliseconds(long began) => Math.Round(clock.GetElapsedTime(began).TotalMilliseconds, 1);

    /// <summary>
    /// The level to report at what keeps the work from going on: the given one the first time,
    /// and the debug level while the trouble lasts and while the application is stopping.
    /// </summary>
    private LogLevel OnceAt(LogLevel level, CancellationToken stopping)
    {
        var quietly = _troubled || stopping.IsCancellationRequested;
        _troubled = true;

        return quietly ? LogLevel.Debug : level;
    }

    private static List<Scheduled> Named(IEnumerable<IBackgroundJob> jobs)
    {
        var scheduled = new List<Scheduled>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.Name))
            {
                throw new InvalidOperationException($"The background job {job.GetType().FullName} has no name.");
            }

            if (!names.Add(job.Name))
            {
                throw new InvalidOperationException(
                    $"More than one background job is named '{job.Name}'. The name is the key of a job's state, so each job needs its own.");
            }

            if (job.Schedule is null)
            {
                throw new InvalidOperationException($"The background job '{job.Name}' has no schedule.");
            }

            scheduled.Add(new Scheduled(job));
        }

        return scheduled;
    }

    /// <summary>How a run ended, as it goes into the job's row.</summary>
    private sealed record Ending(DateTimeOffset StartedAt, DateTimeOffset FinishedAt, BackgroundJobOutcome Outcome);

    /// <summary>A job and what the scheduler knows about it while it holds the lock.</summary>
    private sealed class Scheduled(IBackgroundJob job)
    {
        public IBackgroundJob Job { get; } = job;

        public string Name { get; } = job.Name;

        public BackgroundJobSchedule Schedule { get; } = job.Schedule;

        /// <summary>The start of the job's latest run, as the database has it. Null when it has never run.</summary>
        public DateTimeOffset? PreviousStart { get; set; }

        public DateTimeOffset NextRun { get; set; }

        /// <summary>The run this instance started last. Completed when there is none.</summary>
        public Task Run { get; set; } = Task.CompletedTask;

        /// <summary>The due time that was last reported as held back, so that it is reported once.</summary>
        public DateTimeOffset? HeldBack { get; set; }

        public bool StartFailing { get; set; }

        /// <summary>
        /// The starts this instance tried to write and got an error for, since the last one it
        /// is sure of. One of them may be in the database all the same.
        /// </summary>
        public List<DateTimeOffset> UnconfirmedStarts { get; } = [];

        /// <summary>
        /// How the latest run ended, until that is in the database. It is set by the run as it
        /// ends, and read by the scheduler only after the run is over.
        /// </summary>
        public Ending? Unrecorded { get; set; }

        public void Follow(BackgroundJobState? state, DateTimeOffset now)
        {
            PreviousStart = state?.LastStartedAt;

            // A run that the application stopped left its work to do, so the job is due as if
            // that run had not been.
            var ran = state is null || state.LastOutcome == BackgroundJobOutcome.Stopped ? null : PreviousStart;

            NextRun = Schedule.NextRun(ran, now);
        }
    }
}
