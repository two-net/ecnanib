# Background work

Scheduled work, such as a catalog sync, the decision cycles, reconciliation, and the retention jobs, runs inside the application, on one scheduler. The scheduler keeps each job's latest run in the database, so a schedule goes on after a restart. It runs jobs only while it holds a database lock, so two instances never run them at the same time (NFR-ARC-4).

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

| Rule | Why |
|---|---|
| The name is unique and never changes. | It is the key of the job's state. Two jobs with one name stop the application from starting. A job under a new name has never run, and the old row stays behind. |
| The job takes only what lives as long as the application. | It is created once. It creates a database context inside each run, like every component does. |
| The job ends soon when its token is canceled, by letting `OperationCanceledException` out. | The token is canceled when the application stops, and when the instance stops being the one that runs the background work. The scheduler waits for the run to end before it lets go of the lock. |
| A run leaves its data consistent wherever it ends, and its work can be done again. | A run can be cut short by a stop or a failure, and the next run finds what is left to do. |
| A job that wants to retry does so inside its run. | An error that leaves the run ends it. The scheduler does not try again before the job is next due. |

A run is not told what a previous run did. A job that needs to know keeps that in tables of its own.

## Schedules

There are two kinds. They differ in what becomes of the time that passed while the application was down.

| Schedule | When the job is due | After a restart |
|---|---|---|
| `BackgroundJobSchedule.Every(interval)` | One interval after the start of its previous run. A job that has never run is due at once. | It goes on from the previous run, which is in the database. If the interval has not passed, the job waits for the rest of it. If it passed while the application was down, the job runs once, however many intervals were missed. |
| `BackgroundJobSchedule.AtUtcBoundaries(period)` | At every multiple of the period since midnight, in UTC: with 15 minutes, at 00:00, 00:15, 00:30. The period must divide a day into equal parts. | The job waits for the next boundary. Boundaries that passed are skipped and never made up for (FR-CYC-8). |

The run is told when it was due, in `BackgroundJobRun.ScheduledFor`. For a job on boundaries that is the boundary itself, such as the close of a candle.

Jobs do not wait for each other: each runs when it is due. A job never runs twice at the same time. If a run is still going when the next one is due, the next one waits until it ends, on an interval, or is skipped, on boundaries. Either is logged as a warning.

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
| Was running when the process ended. | A start with no end | It is reported once, as a warning, when the background work starts again. The job is not run at once, but when it is next due: a job that brings the process down must not do so at every start. |

## The lock

The instance that runs the background work holds a PostgreSQL advisory lock for as long as it does, under the key `BackgroundJobs.LockKey`. This is another lock than the one that guards migrations, which is held only while the application starts. It is taken even when no job is registered.

- An instance that finds the lock taken logs that it stands by, and asks again every second.
- The lock belongs to a database session that the scheduler keeps to itself, outside the application's connection pool. Ending that session releases the lock, whatever state the application is in.
- The holder checks every second that its session still holds the lock. If the session has ended or does not answer within five seconds, it stops its jobs, waits until they have ended, and stands by again. The stopped runs are recorded as `Stopped`.
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

## The log

The scheduler logs under the area `Ecnanib.Platform.BackgroundWork.BackgroundJobScheduler`. Each event about a job carries its name as `Job`.

| Level | Event |
|---|---|
| `Information` | `This instance runs the background work, with 3 jobs` |
| `Information` | `Another instance runs the background work. This one stands by, and takes over when that one stops` |
| `Information` | `Background job catalog-sync finished in 2310.4 ms` |
| `Information` | `Background job catalog-sync was stopped after 812.0 ms, before it was done` |
| `Error` | `Background job catalog-sync failed after 95.3 ms. It runs again when it is next due`, with the error |
| `Warning` | A run that never finished, a run that is held back because the one before it is still going, a boundary that was skipped because its run could not start in time, a start or an outcome that could not be written, and a lock that was lost. |
| `Error` | `The background work cannot go on. This instance tries again every second until it can`, with the error. Logged once for as long as it lasts. |

What a job logs itself is up to the job. It opens its own correlation scope for what it works on, as [logging.md](logging.md) describes.

## Testing a job

A test registers a job the way a component does, on a database of its own, because on a shared database another test's application may hold the lock:

```csharp
var database = PostgresServer.CreateDatabase();
using var application = ApplicationWithJobs.On(database, clock, job);
```

The scheduler takes the time from the `TimeProvider` service. A test replaces it with `TestClock` and sets the time, so a job that is due in a day runs within a second of the test saying that a day has passed.
