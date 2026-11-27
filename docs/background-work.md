# Background work

Scheduled work, such as a catalog sync, the decision cycles, reconciliation, the retention jobs, and the backups of the database, runs inside the application, on one scheduler. The scheduler keeps each job's latest run in the database, so a schedule goes on after a restart. It runs jobs only while it holds a database lock, so two instances never run them at the same time (NFR-ARC-4).

The plumbing is in `src/Ecnanib/Platform/BackgroundWork`. The rules below are enforced by tests in `tests/Ecnanib.Tests/BackgroundWork`.

## One running instance

**Version 1 targets a single running instance:** one application process and one PostgreSQL database (NFR-ARC-4). Nothing is built to share work between instances.

What must not run twice takes a database lock, so a second instance that is started anyway, by mistake or for a moment during a deployment, is safe:

| What | With a second instance |
|---|---|
| Migrations | It waits until the first one has migrated, finds nothing pending, and starts. See [persistence.md](persistence.md). |
| Background work | It stands by and runs no job. When the instance that runs the work stops, it takes over within about a second. |
| Requests | Both serve the UI and the API. |

A second instance is not a way to carry more load. Only one of them runs the background work, and what a component keeps in memory is not shared between them.

## Writing a job

A job implements `IBackgroundJob`: a name, a schedule, and the work of one run.

```csharp
internal sealed class CatalogSyncJob(CatalogSyncSettings settings, IDbContextFactory<EcnanibDbContext> contexts)
    : IBackgroundJob
{
    public string Name => "catalog-sync";

    public BackgroundJobSchedule Schedule { get; } =
        BackgroundJobSchedule.Every(TimeSpan.FromHours(settings.IntervalHours));

    public async Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The work of one run.
    }
}
```

The component registers it in its `Add<Name>()`:

```csharp
services.AddBackgroundJob<CatalogSyncJob>();
```

That is all. The platform's scheduler runs every registered job; a component starts no hosted service of its own for scheduled work, because that would run on every instance, without the lock.

**What serves the pages that an instance has open is no job.** A page that follows what changes is served by the instance that its connection is to, so each running instance listens to what the database says, with `LiveListener` in `Platform`, and reads the prices that its pages follow, with `IExchangePriceWatch` of the Exchange Gateway. Both begin with the first page that follows, need no lock of the background work, and keep nothing in the database. See [live-updates.md](live-updates.md).

**What a user asks for is no job.** A test cycle of an agent runs inside the request that asked for it, on whichever instance serves the request, and returns when it has ended: it places no order, and what it stores is its own record, so it needs no lock of the background work (FR-CYC-12). See "A test cycle" in [cycles.md](cycles.md).

The jobs that the application has so far:

| Job | Component | Schedule | What a run does |
|---|---|---|---|
| `key-validation` | Credentials | Every hour, on the hour | Asks the provider about each stored key that was last checked a day ago or longer (FR-KEY-3). See "Validating a key" in [credentials.md](credentials.md). |
| `catalog-sync` | Catalog | Every `CatalogSync:IntervalHours`, and when the work starts with an empty catalog | Syncs the catalog of every provider that the configuration file holds a catalog key for (FR-CAT-2). See [catalog.md](catalog.md). |
| `clock-watch` | Exchange Gateway | Every `Binance:ClockCheckSeconds`, and whenever the work starts | Reads Binance's clocks, which the clock of the service is held against (FR-BIN-5). What it reads is kept in memory, so a start has no reading. See "The clock" in [exchange.md](exchange.md). |
| `decision-cycles` | Decision Engine | Every quarter hour, on the quarter hour | Starts a cycle for each running agent on a timeframe of which a candle closed then, and lets the cycles wait in a queue for their turns (FR-CYC-1, FR-CYC-11). Before that it has Agents resume the agents that their daily loss limit halted on an earlier UTC day (FR-RSK-8), and each cycle evaluates the loss limits of its agent before its team is called. A run is over before the next quarter hour. See [cycles.md](cycles.md). |
| `order-settling` | Decision Engine | Every minute, first as the work starts, and once more as the application stops | Values the fees of the executions that were stored without a value (FR-BIN-10), and settles the orders of the agents that have something to settle with the exchange: an order whose outcome is unknown is asked about, an open position without a stop-loss is protected or closed at market, and a stop-loss that rests is asked about once in a while (FR-BIN-6, FR-RSK-7). Most runs find no agent. It runs first, so a restart settles before anything is scheduled (NFR-REL-3), and it does last what a stopped cycle left, with every entry that rests taken off the exchange (NFR-REL-5). See "Settling" in [orders.md](orders.md), and "Starting" and "Stopping" in [reliability.md](reliability.md). |
| `position-watch` | Decision Engine | Every `Binance:ReconcileMinutes`, and when the work starts with a position to watch | Checks the margin of every open position of Futures, and tells its user when it approaches liquidation, and reads what a position received and paid as funding when that is due (FR-BIN-F4, FR-BIN-F5). It only reads the exchange. A run with no position of Futures asks nobody. See "Watching the positions of Futures" in [orders.md](orders.md). |
| `position-results` | Decision Engine | Every minute, and when the work starts with a closed position that has no result | Records the result of every closed position of which all is stored: its net profit or loss, its result in R, its fees, its funding, and how long it was held (FR-EXP-1, FR-BIN-10). Then it has the loss limits of the agents of those positions evaluated, which records their equity. It asks no exchange, and most runs find no position. See "The results of a position" in [orders.md](orders.md). |
| `reconciliation` | Decision Engine | Every `Binance:ReconcileMinutes`, and first as the work starts, after `order-settling` | Reconciles every agent that holds an open position or an order that is not over with the exchange, which is the source of truth: asks about its orders, holds its position against what the account of its user holds, corrects the records, and tells its user what did not match (FR-BIN-7, FR-BIN-8). Each agent that is reconciled has its loss limits evaluated then: one that runs and has reached its daily loss limit or its drawdown limit is halted, and its position is closed (FR-RSK-8). It runs first, so no job starts before the agents were reconciled, and after a start an agent that holds something from before gets no cycle until a run has reconciled it (NFR-REL-3). See "Reconciling" in [orders.md](orders.md), and "Starting" in [reliability.md](reliability.md). |
| `experience-scoring` | Experience | Every 5 minutes | Scores the calls of the analysts whose 4th later candle has closed, and follows the shadow trades of the vetoed plans over the candles that closed after them (FR-EXP-3, FR-EXP-5). It reads candles, which need no key, and places no order. A run with nothing due asks nobody. See "The job" in [experience.md](experience.md). |
| `lesson-writing` | Decision Engine | Every 5 minutes | Has the lesson of every closed position written that has its result and no lesson yet: the model of the agent's Risk Manager is called with the key of the agent's user, once, and once more when the call timed out or the lesson was invalid (FR-EXP-6). One lesson at a time, and none is started after 2 minutes of a run. It places no order and asks no exchange, and a run with nothing due calls no model. See "Writing a lesson" in [experience.md](experience.md). |
| `exchange-outage-alerts` | Agents | Every minute | Tells the users whose agents run where Binance has been out for `Binance:OutageAlertSeconds`, and tells them when it answers again (NFR-REL-4). It asks the gateway what it keeps in memory, reads which agents run only when there is something to tell, and calls no exchange. See "A Binance outage" in [reliability.md](reliability.md). |
| `role-reassignment` | Agents | Every hour | Gives the roles of each agent that opted in, runs, holds no position, and was not reassigned within `Trading:RoleReassignmentDays` days their recommended models by strength, and tells its user of each change (FR-TEAM-10). It reads what is stored, and asks neither the exchange nor a provider. An agent that holds a position is asked about again with the next run. See "Reassigning the roles automatically" in [agents.md](agents.md). |
| `account-feed` | Decision Engine | Every minute, and when the work starts with an account to follow | Has the gateway follow the account event feed of every account in which an agent holds something, and then listens for 40 seconds: an agent is reconciled at once when its account says a change (FR-BIN-7), and its loss limits are evaluated with it (FR-RSK-8). A run with no account to follow asks nobody, and is over at once. See "The account event feed" in [orders.md](orders.md). |
| `notification-email` | Notifications | Every minute | Sends the notifications about critical events by email, to each user who opted in and whose address is verified, in one message for a user (FR-NTF-3). A user who opted out since they were raised is sent nothing. Where the email delivery service does not take a message, the run ends, and what is left is sent with a later run, for a day at the most. Most runs find nothing, and ask the email delivery service nothing. See "Email for critical events" in [notifications.md](notifications.md). |
| `record-retention` | Decision Engine | Every hour | Deletes what the roles answered in the cycles of longer ago than `Retention:TranscriptDays`, and then the records of the cycles of longer ago than `Retention:DecisionDays`, with what hangs on them, in small batches (NFR-DAT-5). It asks neither the exchange nor a provider. See [retention.md](retention.md). |
| `equity-thinning` | Decision Engine | Every hour | Thins the equity of the agents over time: a UTC day of longer ago than 90 days keeps the row that it ended with, for an agent in a mode and an asset (NFR-DAT-5). A day comes of age once in 24 hours, so most runs read nothing. See "Thinning the equity over time" in [retention.md](retention.md). |
| `notification-retention` | Notifications | Every hour | Deletes the notifications of longer ago than `Retention:NotificationDays`, read or not, in small batches (NFR-DAT-5). See [retention.md](retention.md). |
| `audit-retention` | Accounts | Every hour | Deletes the events of the audit trail of longer ago than `Retention:AuditDays`, in small batches (NFR-DAT-5). See [retention.md](retention.md). |
| `database-backup` | Platform | Every `Backup:IntervalHours`, which is a day or less | Backs the database up with `pg_dump`, to the folder of `Backup:Directory`, and deletes the backups of longer ago than `Backup:KeepDays`, but for the latest (NFR-DAT-11). It keeps no writer waiting. See [backups.md](backups.md). |
| `restore-test` | Platform | Every `Backup:RestoreTestDays` days | Restores the latest backup to a scratch database of the same server, holds it to what the backup says of itself, records the test in `restore_tests`, and drops the scratch database (NFR-DAT-11). A test that failed fails the run. See "Testing a restore" in [backups.md](backups.md). |
| `uptime` | Platform | Every minute | Says that the service is up: it lengthens the row of its instance in `up_spans`, or writes a new one after a start or a break, which the availability of a month is measured from (NFR-REL-1). It calls nothing outside the database. See "Availability" in [reliability.md](reliability.md). |

| Rule | Why |
|---|---|
| The name is unique and never changes. | It is the key of the job's state. Two jobs with one name stop the application from starting. A job under a new name has never run, and the old row stays behind. |
| The job takes only what lives as long as the application. | It is created once. It creates a database context inside each run, like every component does. |
| The job ends soon when its token is canceled, by letting `OperationCanceledException` out. | The token is canceled when the application stops, and when the instance stops being the one that runs the background work. The scheduler waits for the run to end before it lets go of the lock. |
| A run leaves its data consistent wherever it ends, and its work can be done again. | A run can be cut short by a stop or a failure, and the next run finds what is left to do. |
| A job that wants to retry does so inside its run. | An error that leaves the run ends it. The scheduler does not try again before the job is next due. |
| A job that is to act as something happens runs for most of the time between two runs. | `decision-cycles` does, with the cycles of a close, and `account-feed`, which listens to the account event feeds. Such a run ends before the next one is due, by the clock, and at once when its token is canceled. It is still a job, and no hosted service: so it runs on the one instance that holds the lock. |

A run is not told what a previous run did. A job that needs to know keeps that in tables of its own.

## Schedules

There are two kinds. They differ in what becomes of the time that passed while the application was down.

| Schedule | When the job is due | After a restart |
|---|---|---|
| `BackgroundJobSchedule.Every(interval)` | One interval after the start of its previous run. A job that has never run is due at once. | It goes on from the previous run, which is in the database. If the interval has not passed, the job waits for the rest of it. If it passed while the application was down, the job runs once, however many intervals were missed. |
| `BackgroundJobSchedule.AtUtcBoundaries(period)` | At every multiple of the period since midnight, in UTC: with 15 minutes, at 00:00, 00:15, 00:30. The period must divide a day into equal parts. | The job waits for the next boundary. Boundaries that passed are skipped and never made up for (FR-CYC-8). |

The run is told when it was due, in `BackgroundJobRun.ScheduledFor`. For a job on boundaries that is the boundary itself, such as the close of a candle.

### A job that is due when the work starts

A job on an interval can have work when the background work starts, although its interval has not passed: the catalog is synced at startup when it is empty (FR-CAT-2), and Binance's clocks are read at every start (FR-BIN-5). Such a job implements `IDueAtStart` beside `IBackgroundJob`:

```csharp
public Task<bool> IsDueAtStartAsync(CancellationToken cancellationToken) => sync.IsEmptyAsync(cancellationToken);
```

| Rule | Why |
|---|---|
| The scheduler asks once each time the instance takes up the work, before any job starts but those that run first. | That is at the start of the application, and again after the lock was lost and taken back. It asks under the lock, so an instance that stands by does not ask. Where jobs run first, it asks once they have ended: what they wrote is what a job answers from. |
| A job that says yes runs at once, and its interval goes on from that run. | The run is the job's latest run, like any other. |
| A job that is due anyway is not asked. | One that has never run, one whose interval has passed, and one whose latest run was stopped. |
| **A job whose latest run never finished is not asked.** | It runs when it is next due, whatever it would say: a job that brings the process down must not do so at every start. |
| An error that leaves the question is logged once, as `Error`, and counts as no. | The question is the job's own. It does not keep the other jobs from running. |
| Only a job on an interval can be asked. | A run on boundaries is for its boundary, and the start of the work is none. The application does not start with a job on boundaries that implements `IDueAtStart`. |

### A job that runs first

On startup the application reconciles every running agent with the exchange, and then resumes scheduling (NFR-REL-3). So some jobs run before the schedule starts: what settles the orders, and then what reconciles the agents. Such a job implements `IRunsFirst` beside `IBackgroundJob`:

```csharp
public interface IRunsFirst
{
    void TakenUp();
}
```

| Rule | Why |
|---|---|
| **The jobs that run first run one after the other, and no other job starts until they have ended.** | Each time the instance takes up the work: at the start of the application, and again after the lock was lost and taken back. They run in the order that they are registered in, each as a run like any other, which is the job's latest run from then on. |
| **A job that runs first runs whatever its row says.** | Also one whose interval has not passed, and one whose latest run never finished, which is said as a warning: a start after a process that was cut off is the one that needs it most. This is the one exception to the rule that such a run is not repeated at once. |
| **The scheduler says first that the work is taken up.** | `TakenUp` is called under the lock, before any job starts. What the job remembers of the work that it did before is void then: another instance may have done the work meanwhile. An error that leaves it is logged, and the job runs first all the same. |
| **A run that fails keeps nothing waiting.** | It is logged as every failed run is, and what comes after it starts. So does a run that another instance started a moment ago, as it gave up the work: that run stands. |
| **The other jobs wait for two minutes at the most.** | `BackgroundJobScheduler.FirstRunsWithin`, by the time of day or by the time that passed, whichever says so first. Then they start beside the runs that still go on, and a job that was still to run first is due at once. It is said once, as a warning, with the jobs that had not ended. |
| **A job that waited runs for when it was due.** | A job on boundaries whose boundary came meanwhile is told that boundary, and judges whether it is too late to act. A boundary that passed together with the one after it is skipped, as always. |
| **Only a job on an interval can run first.** | As for `IDueAtStart`: the application does not start with a job on boundaries that implements it. A job that runs first is not asked whether it is due as the work starts. |

### A job that runs last

Shutdown is graceful, and no entry is left without protection (NFR-REL-5). So a job can have something to do last, as the application stops: what settles the orders finishes what a stopped cycle left. Such a job implements `IRunsLast` beside `IBackgroundJob`:

```csharp
public interface IRunsLast
{
    Task RunLastAsync(CancellationToken cancellationToken);
}
```

| Rule | Why |
|---|---|
| **The work stops as the application begins to stop.** | The scheduler listens for it itself. The host stops its services one after the other, the web server before the scheduler, and the time that the application has to stop in counts from the beginning. |
| **What comes last is done once every run has ended, and before the lock is let go.** | Each job that runs last is called, in the order that the jobs are registered in. What a run left is what is done last, and it is done under the lock, as a run is. |
| **Only when the application stops, and only by the instance that runs the work.** | Not when the lock is lost: the application runs on then, and whichever instance holds the lock goes on with the work. An instance that stands by does nothing last. |
| **It is no run of the job.** | Nothing of it is in the job's row. An error that leaves it is logged once, as an error, and the next job is called. |
| **Its token is canceled 45 seconds before the host waits no longer.** | `BackgroundJobScheduler.EndsBefore` before `Application:ShutdownSeconds` is over, counted from when the application began to stop. A job begins nothing after that, and brings what is under way to its end, so that it ends, and says what it left, before the process does. |
| **When the runs have not ended by then, nothing is done last.** | It is said as a warning, with the runs that still go on. The scheduler waits for them all the same before it lets go of the lock. |
| **When the host waits no longer, the process ends with the work under way.** | It is said once, at the critical level, with the jobs that still run. Their rows say a start with no end. |

See "Starting" and "Stopping" in [reliability.md](reliability.md) for what the two jobs of the Decision Engine do with this.

Apart from the jobs that run first, jobs do not wait for each other: each runs when it is due. A job never runs twice at the same time. If a run is still going when the next one is due, the next one waits until it ends, on an interval, or is skipped, on boundaries. Either is logged as a warning.

The scheduler looks at the clock at least once a second, so a run starts within about a second of being due. It goes by the clock as it finds it, also after the clock was set or the machine slept:

- A boundary that passed meanwhile is skipped once the following one has passed too, as after a restart. A run that starts late, but before the following boundary, is still told the boundary it is for, so the job can judge whether it is too late to act.
- After the clock was set back, a job on an interval waits one interval from then, and no longer.

## The state

The table `background_jobs` has one row for each job that has ever run:

| Column | Meaning |
|---|---|
| `name` | The job. |
| `last_started_at` | When its latest run started. Written before the run begins. |
| `last_finished_at` | When that run ended. Empty while it is running. |
| `last_outcome` | How it ended: `Succeeded`, `Failed`, or `Stopped`. Empty while it is running. |

This is state, not a log: each run overwrites the row. Why a run failed is in the log and never in the table (NFR-DAT-7).

When the database is away as a run ends, how it ended cannot be written. The scheduler keeps it and writes it when the database is back, before it goes by the table again.

What follows a run depends on how it ended:

| The run | In the table | What follows |
|---|---|---|
| Did its work. | `Succeeded` | The job runs when it is next due. |
| Threw an error. | `Failed` | The error is logged once, as `Error`. The job runs when it is next due: a failure that repeats does not become a loop. |
| Was stopped by the application, when it shut down or lost the lock. | `Stopped` | The work is still to do. A job on an interval is due at once when the background work starts again. A job on boundaries waits for the next boundary, as always. |
| Was running when the process ended. | A start with no end | It is reported once, as a warning, when the background work starts again. The job is not run at once, but when it is next due: a job that brings the process down must not do so at every start. That holds for a job that would say it is due when the work starts, which is then not asked. It does not hold for a job that runs first, which runs first all the same. |

## The lock

The instance that runs the background work holds a PostgreSQL advisory lock for as long as it does, under the key `BackgroundJobs.LockKey`. This is another lock than the one that guards migrations, which is held only while the application starts. It is taken even when no job is registered.

- An instance that finds the lock taken logs that it stands by, and asks again every second.
- The lock belongs to a database session that the scheduler keeps to itself, outside the application's connection pool. Ending that session releases the lock, whatever state the application is in.
- The holder checks every second that its session still holds the lock. If the session has ended or does not answer within five seconds, it stops its jobs, waits until they have ended, and stands by again. The stopped runs are recorded as `Stopped`.
- When the application stops, the holder stops its jobs, waits until they have ended, does what is done last, and only then lets go of the lock: see "A job that runs last".
- Starting a run is also guarded by the job's row: the start is written only if the row still shows the run the instance knows of. So even while a lock passes from one instance to another, the same run cannot start on both.

To see which session holds the lock:

```sql
SELECT pid FROM pg_locks
WHERE locktype = 'advisory' AND objsubid = 1
  AND (classid::bigint << 32) + objid::bigint = 4999961376054665815;
```

Like the migration lock, this one belongs to a session, so the connection must be direct, or go through a connection pooler in session mode. Behind a pooler in transaction mode the application logs a warning that the lock is not held by the session that took it, and runs no jobs.

Two limits come with a second instance, which version 1 does not have:

- An instance whose connection to the database is cut without being closed keeps the lock until PostgreSQL notices. The other instance stands by for that long.
- A job that does not end when it is canceled keeps running after its instance has lost the lock.
- A run that one instance is still stopping when the other takes over shows to the other as a run that never finished. The other one does not repeat it at once, but when the job is next due.

## When the database is away

Nothing that goes wrong with the background work stops the application. When the database cannot be reached, the scheduler logs that once, tries again every second, and takes up the work by itself when the database is back. The schedules go on from what the table says.

## The status of the jobs

An operator status screen shows scheduler health (FR-OPS-2). `IBackgroundWorkStatus` says it, in `Platform/BackgroundWork`, from the table above and from the jobs that are registered:

```csharp
public interface IBackgroundWorkStatus
{
    Task<IReadOnlyList<BackgroundJobStatus>> ReadAsync(CancellationToken cancellationToken);
}

public enum BackgroundJobRunState { NoRunYet, NotEnded, Succeeded, Failed, Stopped }

public sealed record BackgroundJobStatus(
    string Name, BackgroundJobSchedule? Schedule, BackgroundJobRunState State,
    DateTimeOffset? LastStartedAt, DateTimeOffset? LastFinishedAt, DateTimeOffset? DueAt, bool Overdue);
```

| Rule | Detail |
|---|---|
| **It is read from the database** | And from nothing that one instance holds in memory. So it says the same on every instance, also on one that stands by and runs no job itself. |
| **Every registered job is listed, by name** | With its schedule: `BackgroundJobSchedule.Period`, and `OnUtcBoundaries`. A job that has not run has no row, and is `NoRunYet`. |
| **A row of a job that is not registered is listed last** | A job of an earlier version, or one that this instance was started without. It has no schedule here, so nothing says when it is due. |
| **A run with no end is `NotEnded`** | It is still going, or the process ended before it did: the row cannot tell the two apart, and neither does the status. |
| **When the next run is due is worked out as the scheduler does** | One period after the start of the latest run, or the boundary that follows it. A run on an interval that the application stopped is due from when it was stopped. |
| **A run is overdue 30 seconds after it was due** | With no start of it in the database. The scheduler looks at the clock every second, and an instance that takes the work over needs a few seconds, so a run that is this late says that no instance runs the background work, that the run before it outlasts its schedule, or that the work has just started and the jobs that run first have not ended yet. The shortest schedule that the configuration file allows is 30 seconds. |
| **A job that has not run is not overdue** | It is due at once, and since when is not known. |

`BackgroundWorkStatusTests` holds it to these, mostly with the lock held by the test, as another instance would hold it: the application then runs nothing, and a row says what the test wrote.

## The log

The scheduler logs under the area `Ecnanib.Platform.BackgroundWork.BackgroundJobScheduler`. Each event about a job carries its name as `Job`.

| Level | Event |
|---|---|
| `Information` | `This instance runs the background work, with 20 jobs` |
| `Information` | `Another instance runs the background work. This one stands by, and takes over when that one stops` |
| `Information` | `Background job catalog-sync finished in 2310.4 ms` |
| `Information` | `Background job catalog-sync was stopped after 812.0 ms, before it was done` |
| `Error` | `Background job catalog-sync failed after 95.3 ms. It runs again when it is next due`, with the error |
| `Warning` | A run that never finished, a run that is held back because the one before it is still going, a boundary that was skipped because its run could not start in time, a start or an outcome that could not be written, and a lock that was lost. |
| `Information` | `The background jobs that run first have ended, so the other jobs start` |
| `Warning` | `The background jobs that run first did not end within 2 minutes of the start of the background work, so the other jobs start beside them. Still to end: reconciliation` |
| `Error` | `Background job reconciliation could not be told that this instance takes up the background work. It runs first all the same`, with the error |
| `Information` | `Background job order-settling did what it does last as the application stops, in 412.6 ms` |
| `Warning` | What is done last did not end before the time to stop in was nearly over, or was not begun because the runs had not ended by then, or because the lock was no longer held. |
| `Error` | `Background job order-settling failed at what it does last as the application stops, after 95.3 ms`, with the error |
| `Critical` | `The application stops before its background work has ended, because the time that it has to stop in is over. Still running: decision-cycles` |
| `Error` | `Background job catalog-sync could not say whether it is due now that the background work starts. It runs when it is next due, at ...`, with the error |
| `Error` | `The background work cannot go on. This instance tries again every second until it can`, with the error. Logged once for as long as it lasts. |

What a job logs itself is up to the job. It opens its own correlation scope for what it works on, as [logging.md](logging.md) describes.

## Testing a job

A test registers a job the way a component does, on a database of its own, because on a shared database another test's application may hold the lock:

```csharp
var database = PostgresServer.CreateDatabase();
using var application = ApplicationWithJobs.On(database, clock, job);
```

The scheduler takes the time from the `TimeProvider` service. A test replaces it with `TestClock` and sets the time, so a job that is due in a day runs within a second of the test saying that a day has passed. The clock stands between two settings. The load test lets it run, with `Run`, so that the time of day passes as time does: see [performance.md](performance.md).

`FirstJob` is a job that runs first, and keeps how often it was told that the work is taken up. `LastJob` is one that has something to do last, and keeps how often it was called for it. `Sequence` keeps what jobs did in the order that they did it, for a test of what comes before what. A test of a stop that takes too long gives its host less time to stop in than the file of the tests does, with `ApplicationWithJobs.On(database, clock, stopsWithin, jobs)`. `BackgroundJobRunsFirstTests` and `BackgroundJobRunsLastTests` hold the scheduler to the two sections above.

Every application of a test has the application's own jobs too, but for nineteen. `clock-watch` is due whenever the work starts, and calls Binance without being asked: in a test of something else that would be a call to a host that is down, or one more call to a Binance of the test's, at a moment that the test does not choose. So `EcnanibApplicationFactory` leaves that job out, unless the test says that its application watches Binance's clock, as `ClockWatchTests` does. `decision-cycles` is due at every quarter hour, and asks the exchange for candles when an agent runs then. Many tests have agents that run, and move a clock over a quarter hour for a reason of their own, so the factory leaves that job out as well, unless the test says that its application runs cycles, as `CyclesApplication` does: see "Testing" in [cycles.md](cycles.md). `order-settling` runs first as the work starts, and then every minute, for the agents whose orders or positions have something to settle, and asks the exchange about them. Many tests give a user orders and positions for a reason of their own, so the factory leaves that job out of the schedule too, unless the test says that its application settles orders. It keeps the job under a key, so that a test of what a run does runs it itself, with no run of the schedule beside it: `AccountsApplication.SettleOrdersAsync`. A job under a key is none that the scheduler knows, so it neither runs first nor does anything last in such an application. `position-watch` is due as the work starts when there is a position of Futures, and then every few minutes, and asks the exchange about each. The factory leaves it out of the schedule of every application, and keeps it under a key as well: a test of what a run does runs it with `AccountsApplication.WatchPositionsAsync`. `position-results` is due as the work starts when a closed position has no result, and then every minute: it writes the result, and has the loss limits of the agent evaluated. Many tests give a user a closed position for a reason of their own, so the factory leaves it out of the schedule of every application too, and keeps it under a key: `AccountsApplication.RecordResultsAsync` runs it. `lesson-writing` is due as the work starts, and then every few minutes: it calls the model of the Risk Manager of an agent for every lesson that is due, with the key of the agent's user. Many tests give an agent a closed position for a reason of their own, which leaves such a lesson, and stand nothing in for the models. So the factory leaves that job out of the schedule of every application as well, and keeps it under a key: `AccountsApplication.WriteLessonsAsync` runs it. `reconciliation` runs first as the work starts, and then every few minutes, and asks the exchange about every order that is not over, and about what each account holds. And a cycle waits for an agent that holds something from before the start until that job has reconciled it. Many tests give a user orders and positions for a reason of their own, and start an application over them. So the factory leaves the job out of the schedule, and what holds a cycle back for it out of the application, unless the test says that its application reconciles, as `ReconciliationJobTests` does for the start. It keeps the job under a key: `AccountsApplication.ReconcileAgentsAsync` runs it. `account-feed` runs every minute, for most of its minute. The file of the tests names no address of a feed, so it would follow nothing, and it would still be a run of a minute in every application. The factory leaves it out of the schedule of every application, and keeps it under a key as well: a test of what a run does runs it with `AccountsApplication.FollowFeedAsync`. `experience-scoring` is due as the work starts, and then every few minutes: it asks the exchange for the candles of every call and every shadow trade that is due, and writes what became of each. Many tests record a cycle for a reason of their own, which leaves such rows, and compare what the tables hold before and after. So the factory leaves it out of the schedule of every application as well, and keeps it under a key: `AccountsApplication.ScoreExperienceAsync` runs it. `role-reassignment` is due as the work starts, and then every hour: it gives a role of an agent that opted in, runs, and holds nothing its recommended model. A test that gives an agent such a setting and scores for a reason of its own would find its team changed at a moment that it does not choose. So the factory leaves it out of the schedule of every application too, and keeps it under a key: `AccountsApplication.ReassignRolesAsync` runs it. `exchange-outage-alerts` is due as the work starts, and then every minute: it raises a notification for each user whose agents run where Binance has been out for long enough. Many tests have their Binance leave calls without an answer for a reason of their own, and count the notifications of a user. So the factory leaves it out of the schedule of every application as well, and keeps it under a key: `AccountsApplication.TellOfOutagesAsync` runs it. `notification-email` is due as the work starts, and then every minute: it hands the email delivery service a message for every user who opted in and has a notification about a critical event. A test that raises one for a reason of its own would have it sent, and marked as sent, at a moment that it does not choose. So the factory leaves it out of the schedule of every application as well, and keeps it under a key: `AccountsApplication.SendNotificationEmailsAsync` runs it. `record-retention`, `equity-thinning`, `notification-retention`, and `audit-retention` are due as the work starts, and then every hour: each deletes the rows of its tables that are older than the configuration file keeps them, by the clock of the application. Many tests store rows of long ago for a reason of their own, or set their clock far ahead, and compare what the tables hold before and after. So the factory leaves the four out of the schedule of every application, and keeps each under a key: `AccountsApplication.DeleteAgedRecordsAsync`, `ThinEquityAsync`, `DeleteAgedNotificationsAsync`, and `DeleteAgedAuditEventsAsync` run them. See "Testing" in [retention.md](retention.md). `database-backup` and `restore-test` are due as the work starts, and then after hours and days: each starts a client tool of PostgreSQL, the second makes a database and drops it, and both write files. In a test of something else that is work that nobody asked for, in every application. So the factory leaves the two out of the schedule of every application, and keeps each under a key: `BackupApplication.BackUpAsync` and `TestRestoreAsync` run them. See "Testing" in [backups.md](backups.md). `key-validation` is on boundaries, so it does not run when an application starts, and with no stored key that is due it asks nobody. `catalog-sync` is on an interval, so it runs once when an application first starts on a database. The configuration file of the tests holds no catalog key, so that run asks nobody and writes nothing but the job's own row. A test that compares what the tables hold before and after a request leaves `background_jobs` out, as `AccountsApplication.EverythingStored` does. `uptime` would write a row of `up_spans` as every application starts, and lengthen it every minute: a table that such a comparison does not leave out, and that a test of the availability fills itself. So the factory leaves that job out of the schedule of every application as well, and keeps it under a key: `AvailabilityApplication.BeatAsync` runs it. A test whose clock jumps over more than one hour finds its warning that the hours between were skipped, so a test that counts the scheduler's warnings names its job. To see what a run of a job does at a time of the test's choosing, a test runs the job itself: `AccountsApplication.CheckKeysAsync` does that for `key-validation`, and `CatalogApplication.RunJobAsync` for `catalog-sync`. `AskedJob` is a job that is asked whether it is due when the work starts, and answers as the test says.
