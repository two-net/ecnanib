using Microsoft.Extensions.Options;

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
/// <para>
/// <b>The work starts and stops in an order</b> (NFR-REL-3, NFR-REL-5). When this instance takes
/// up the work, the jobs that run first run before any other job starts: see
/// <see cref="IRunsFirst"/>. When the application stops, every run is stopped and waited for,
/// and then the jobs that run last do what must not be left undone: see <see cref="IRunsLast"/>.
/// </para>
/// </remarks>
internal sealed class BackgroundJobScheduler(
    IEnumerable<IBackgroundJob> jobs,
    BackgroundJobStore store,
    BackgroundWorkLock workLock,
    IHostApplicationLifetime lifetime,
    IOptions<HostOptions> host,
    TimeProvider clock,
    ILogger<BackgroundJobScheduler> logger) : BackgroundService
{
    /// <summary>
    /// How long the other jobs wait for the jobs that run first, at the most, from when this
    /// instance took up the work. Then they start beside the runs that still go on: an exchange
    /// that does not answer keeps no backup and no alert waiting for longer.
    /// </summary>
    internal static readonly TimeSpan FirstRunsWithin = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long before the application must have stopped nothing more is begun of what is done
    /// last. What is under way with the exchange then is not given up: an order call in flight
    /// ends within 15 seconds, and one more call that protects takes 25 at the most, as for a
    /// cycle that is stopped. Both are over, and said in the log, before the host waits no longer.
    /// </summary>
    internal static readonly TimeSpan EndsBefore = TimeSpan.FromSeconds(45);

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

    // Whether the other jobs wait for the jobs that run first, since this instance took up the
    // work, and when it took the work up: by the time of day, and in time that no change of the
    // time of day moves. The wait ends by whichever of the two says so first.
    private bool _firstRuns;
    private DateTimeOffset _tookUpOn;
    private long _tookUpAt;

    // The run of a job that runs first which the other jobs wait for now.
    private Task? _waitedFor;

    // When the application began to stop, in time that no change of the time of day moves.
    // Zero until then.
    private long _stoppingSince;

    // Set when it was said that the application stops before the work has ended, so that it is
    // said once: the host, a test, and what disposes the application may each stop this service.
    private int _saidUnended;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The work stops as the application begins to stop, and not when the host gets to this
        // service: the host stops its services one after the other, and the web server, which
        // waits for its requests, before this one. The time that the application has to stop in
        // counts from that moment, so the cycles are told then.
        using var noted = lifetime.ApplicationStopping.Register(
            () => Interlocked.CompareExchange(ref _stoppingSince, clock.GetTimestamp(), 0));
        using var stops = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lifetime.ApplicationStopping);
        var stopping = stops.Token;

        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await StandByAsync(stopping);
                await RunTheWorkAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                // The application is stopping.
            }
            catch (Exception error)
            {
                // The database is away, most likely. An error that left this method would stop
                // the application, which can do everything else without its background work.
                logger.Log(
                    OnceAt(LogLevel.Error, stopping),
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
                await Task.Delay(Beat, clock, stopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Stops the work, and waits for it for as long as the host does. When the host waits no
    /// longer and the work has not ended, that is said, once: the process ends with it under way.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (ExecuteTask is { IsCompleted: false } && Interlocked.Exchange(ref _saidUnended, 1) == 0)
        {
            logger.LogCritical(
                "The application stops before its background work has ended, because the time that it has to stop in is over. Still running: {Jobs}",
                Names(_jobs.Where(job => !job.Run.IsCompleted)));
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

        // Whether this instance took the work up, and whether it then lost the lock: what is
        // done last is done by the instance that runs the work, when the application stops.
        var tookUp = false;
        var lost = false;

        try
        {
            // Asked once before anything else, so that a session which cannot keep a lock at
            // all never gets as far as starting a job.
            if (!await StillHoldsTheLockAsync(active.Token))
            {
                return;
            }

            tookUp = true;
            TakeUp();

            await FollowTheStoredStateAsync(active.Token);

            _troubled = false;
            logger.LogInformation("This instance runs the background work, with {JobCount} jobs", _jobs.Count);

            while (true)
            {
                await StartWhatIsDueAsync(active.Token);
                await SleepAsync(active.Token);

                if (!await StillHoldsTheLockAsync(active.Token))
                {
                    lost = true;

                    return;
                }
            }
        }
        finally
        {
            await active.CancelAsync();

            if (tookUp && !lost && lifetime.ApplicationStopping.IsCancellationRequested)
            {
                await RunWhatComesLastAsync();
            }

            // A run handles its own errors, so this waits and nothing more.
            await Task.WhenAll(_jobs.Select(job => job.Run));
        }
    }

    /// <summary>
    /// Sleeps until the next job is due, a beat at the most, and no longer than the run that the
    /// other jobs wait for goes on: what comes after a job that runs first starts as it ends.
    /// </summary>
    private async Task SleepAsync(CancellationToken active)
    {
        var sleep = Task.Delay(UntilTheNextRun(), clock, active);

        if (_waitedFor is { IsCompleted: false } run)
        {
            await Task.WhenAny(sleep, run);

            active.ThrowIfCancellationRequested();

            return;
        }

        await sleep;
    }

    /// <summary>
    /// Says to the jobs that run first that this instance takes up the background work, and has
    /// the other jobs wait for them (NFR-REL-3).
    /// </summary>
    private void TakeUp()
    {
        _firstRuns = false;
        _waitedFor = null;
        _tookUpOn = Now();
        _tookUpAt = clock.GetTimestamp();

        foreach (var job in _jobs)
        {
            job.RunsFirst = false;
            job.RanFirst = false;
            job.AskedAtStart = true;

            if (job.Job is not IRunsFirst first)
            {
                continue;
            }

            job.RunsFirst = true;
            _firstRuns = true;

            try
            {
                first.TakenUp();
            }
            catch (Exception error)
            {
                // The job's own. It runs first all the same, and so does every other.
                logger.LogError(
                    error,
                    "Background job {Job} could not be told that this instance takes up the background work. It runs first all the same",
                    job.Name);
            }
        }
    }

    /// <summary>
    /// Does what is done last as the application stops: once every run has ended, each job that
    /// runs last is called, in the order that the jobs were registered in (NFR-REL-5). It does
    /// not throw, and it ends before the application must have stopped.
    /// </summary>
    private async Task RunWhatComesLastAsync()
    {
        var last = _jobs.Where(job => job.Job is IRunsLast).ToList();

        if (last.Count == 0)
        {
            return;
        }

        using var within = BeforeTheApplicationMustHaveStopped();

        try
        {
            // The runs were told to stop. What they leave is what is done last, so they end first.
            var runs = Task.WhenAll(_jobs.Select(job => job.Run));

            await runs.WaitAsync(within.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            if (!runs.IsCompleted || within.IsCancellationRequested)
            {
                logger.LogWarning(
                    "The time that the application has to stop in is nearly over before the runs of its background jobs have ended, so nothing is done last: {Jobs}. Still running: {Running}",
                    Names(last),
                    Names(_jobs.Where(job => !job.Run.IsCompleted)));

                return;
            }

            // Asked of the session itself: what is done last is done under the lock, as a run is.
            if (!await workLock.IsHeldAsync(within.Token))
            {
                logger.LogWarning(
                    "The background work lock is no longer held as the application stops, so nothing is done last: {Jobs}", Names(last));

                return;
            }
        }
        catch (Exception error)
        {
            logger.LogWarning(
                error,
                "Whether this instance still holds the background work lock could not be found out as the application stops, so nothing is done last: {Jobs}",
                Names(last));

            return;
        }

        foreach (var job in last)
        {
            var began = clock.GetTimestamp();

            try
            {
                await ((IRunsLast)job.Job).RunLastAsync(within.Token);

                logger.LogInformation(
                    "Background job {Job} did what it does last as the application stops, in {DurationMs} ms", job.Name, Milliseconds(began));
            }
            catch (OperationCanceledException) when (within.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Background job {Job} did not end what it does last as the application stops, after {DurationMs} ms: the time to stop in is nearly over",
                    job.Name,
                    Milliseconds(began));
            }
            catch (Exception error)
            {
                // Logged here, once, because nothing above this handles it. The application stops all the same.
                logger.LogError(
                    error,
                    "Background job {Job} failed at what it does last as the application stops, after {DurationMs} ms",
                    job.Name,
                    Milliseconds(began));
            }
        }
    }

    /// <summary>
    /// What is canceled <see cref="EndsBefore"/> before the host waits no longer for the
    /// application to stop, counted from when it began to stop. A host that waits without a
    /// limit cancels nothing.
    /// </summary>
    private CancellationTokenSource BeforeTheApplicationMustHaveStopped()
    {
        var timeout = host.Value.ShutdownTimeout;

        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return new CancellationTokenSource();
        }

        var since = Volatile.Read(ref _stoppingSince);
        var left = timeout - EndsBefore - (since == 0 ? TimeSpan.Zero : clock.GetElapsedTime(since));

        if (left > TimeSpan.Zero)
        {
            return new CancellationTokenSource(left, clock);
        }

        var over = new CancellationTokenSource();
        over.Cancel();

        return over;
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

            if (job.RunsFirst)
            {
                // It runs first whatever its row says: also when its time has not come, and
                // when the process ended during its latest run. What reconciles after a start
                // is not left out of the start that follows a process that was cut off (NFR-REL-3).
                if (state is { LastFinishedAt: null })
                {
                    logger.LogWarning(
                        "Background job {Job} did not finish the run it started at {StartedAt}. It runs first all the same, as the background work starts",
                        job.Name,
                        state.LastStartedAt);
                }

                continue;
            }

            if (state is { LastFinishedAt: null })
            {
                // The process ended during the run. It is not repeated at once: a job that
                // brings the process down would do so at every start.
                logger.LogWarning(
                    "Background job {Job} did not finish the run it started at {StartedAt}. It runs again when it is next due, at {NextRun}",
                    job.Name,
                    state.LastStartedAt,
                    job.NextRun);

                continue;
            }

            // A job that waits may have work all the same, now that the work starts. Where jobs
            // run first, it is asked once they have: what they wrote is what it answers from.
            job.AskedAtStart = false;
        }

        if (!_firstRuns)
        {
            await AskWhatIsDueAtStartAsync(now, stopping);
        }
    }

    /// <summary>
    /// Asks the jobs that can be due as the background work starts whether they are, each once
    /// for each time that this instance takes up the work.
    /// </summary>
    private async Task AskWhatIsDueAtStartAsync(DateTimeOffset now, CancellationToken stopping)
    {
        foreach (var job in _jobs)
        {
            if (job.AskedAtStart)
            {
                continue;
            }

            job.AskedAtStart = true;

            if (job.NextRun > now && await IsDueAtStartAsync(job, stopping))
            {
                job.NextRun = now;
            }
        }
    }

    /// <summary>
    /// Asks a job that can be due when the background work starts whether it is. What goes wrong
    /// with the question is the job's own, so it does not keep the other jobs from running.
    /// </summary>
    private async Task<bool> IsDueAtStartAsync(Scheduled job, CancellationToken stopping)
    {
        if (job.Job is not IDueAtStart asked)
        {
            return false;
        }

        try
        {
            return await asked.IsDueAtStartAsync(stopping);
        }
        catch (Exception error) when (!stopping.IsCancellationRequested)
        {
            logger.LogError(
                error,
                "Background job {Job} could not say whether it is due now that the background work starts. It runs when it is next due, at {NextRun}",
                job.Name,
                job.NextRun);

            return false;
        }
    }

    private async Task StartWhatIsDueAsync(CancellationToken active)
    {
        var now = Now();

        if (_firstRuns && await StartWhatComesFirstAsync(now, active))
        {
            // The other jobs wait. How a run of an earlier time ended is written all the same.
            foreach (var job in _jobs)
            {
                await RecordWhatIsLeftAsync(job, active);
            }

            return;
        }

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

    /// <summary>
    /// Runs the jobs that run first, one after the other in the order that they were registered
    /// in, since this instance took up the work (NFR-REL-3): the one whose turn it is starts,
    /// and the next one when its run has ended, however it ended.
    /// </summary>
    /// <returns>Whether the other jobs still wait. They do not once all have run, or when they have waited for long enough.</returns>
    private async Task<bool> StartWhatComesFirstAsync(DateTimeOffset now, CancellationToken active)
    {
        // By the time of day, and by time that no change of the time of day moves: a clock
        // that was set back keeps nothing waiting for longer.
        if (now >= _tookUpOn + FirstRunsWithin || clock.GetElapsedTime(_tookUpAt) >= FirstRunsWithin)
        {
            var late = _jobs.Where(job => job.RunsFirst && !(job.RanFirst && job.Run.IsCompleted)).ToList();

            foreach (var job in _jobs.Where(job => job.RunsFirst))
            {
                // One that has not started is due at once, beside the others.
                if (!job.RanFirst)
                {
                    job.NextRun = now;
                }

                job.RunsFirst = false;
            }

            logger.LogWarning(
                "The background jobs that run first did not end within {Minutes} minutes of the start of the background work, so the other jobs start beside them. Still to end: {Jobs}",
                FirstRunsWithin.TotalMinutes,
                Names(late));

            await OpenTheScheduleAsync(now, active);

            return false;
        }

        foreach (var job in _jobs)
        {
            if (!job.RunsFirst)
            {
                continue;
            }

            if (!job.Run.IsCompleted)
            {
                // Its run goes on. It is what the others wait for.
                _waitedFor = job.Run;

                return true;
            }

            if (job.RanFirst)
            {
                // Its run has ended, however it ended: a run that failed keeps nothing waiting.
                job.RunsFirst = false;

                continue;
            }

            await RecordWhatIsLeftAsync(job, active);

            job.NextRun = now;

            switch (await StartAsync(job, now, active))
            {
                case Started.Here:
                    job.RanFirst = true;
                    _waitedFor = job.Run;

                    return true;

                case Started.Elsewhere:
                    // Another instance ran it a moment ago, as it gave up the work. That run stands.
                    job.RunsFirst = false;

                    continue;

                default:
                    // Its start could not be written. It is tried again at the next beat.
                    return true;
            }
        }

        logger.LogInformation("The background jobs that run first have ended, so the other jobs start");

        await OpenTheScheduleAsync(now, active);

        return false;
    }

    /// <summary>Lets the other jobs start, once the jobs that run first have, or were waited for long enough.</summary>
    private async Task OpenTheScheduleAsync(DateTimeOffset now, CancellationToken active)
    {
        _firstRuns = false;
        _waitedFor = null;

        // Asked now, and not as the work was taken up: what ran first may have left them work.
        await AskWhatIsDueAtStartAsync(now, active);
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

    /// <summary>Writes the start of a run of the job, and starts the run when the start is this instance's.</summary>
    private async Task<Started> StartAsync(Scheduled job, DateTimeOffset now, CancellationToken active)
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

                    return Started.Elsewhere;
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

            return Started.NotYet;
        }

        job.UnconfirmedStarts.Clear();
        job.StartFailing = false;
        job.PreviousStart = startedAt;
        job.NextRun = job.Schedule.NextRun(startedAt, now);

        // The row is about this run from here on, so what the run before it left unwritten is void.
        job.Unrecorded = null;

        logger.LogDebug("Background job {Job} starts its run due at {DueAt}", job.Name, due);

        job.Run = Task.Run(() => RunAsync(job, new BackgroundJobRun(due), startedAt, active), CancellationToken.None);

        return Started.Here;
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

            if (job is IDueAtStart && job.Schedule.SkipsMissedRuns)
            {
                // A run on boundaries is for its boundary, and the start of the work is none.
                throw new InvalidOperationException(
                    $"The background job '{job.Name}' runs on UTC boundaries, so it cannot be due when the background work starts. " +
                    "Only a job on an interval can.");
            }

            if (job is IRunsFirst && job.Schedule.SkipsMissedRuns)
            {
                // The same holds for a run that comes first: it would be a run for no boundary.
                throw new InvalidOperationException(
                    $"The background job '{job.Name}' runs on UTC boundaries, so it cannot run first when the background work starts. " +
                    "Only a job on an interval can.");
            }

            scheduled.Add(new Scheduled(job));
        }

        return scheduled;
    }

    /// <summary>The names of jobs, as one property of an event.</summary>
    private static string Names(IEnumerable<Scheduled> jobs) => string.Join(", ", jobs.Select(job => job.Name));

    /// <summary>What became of starting a run.</summary>
    private enum Started
    {
        /// <summary>The start is written, and the run goes on in this instance.</summary>
        Here,

        /// <summary>Another instance started a run of the job meanwhile, which stands.</summary>
        Elsewhere,

        /// <summary>The start could not be written. The job stays due.</summary>
        NotYet,
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

        /// <summary>
        /// Whether the job runs first and has not done so, or not to its end, since this instance
        /// took up the work: until then the other jobs wait for it.
        /// </summary>
        public bool RunsFirst { get; set; }

        /// <summary>Whether the run that comes first has started, since this instance took up the work.</summary>
        public bool RanFirst { get; set; }

        /// <summary>
        /// Whether the job was asked whether it is due as the work starts, or is not to be asked,
        /// since this instance took up the work.
        /// </summary>
        public bool AskedAtStart { get; set; } = true;

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
