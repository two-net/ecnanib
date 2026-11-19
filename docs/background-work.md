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

The jobs that the application has so far:

| Job | Component | Schedule | What a run does |
|---|---|---|---|
| `key-validation` | Credentials | Every hour, on the hour | Asks the provider about each stored key that was last checked a day ago or longer (FR-KEY-3). See "Validating a key" in [credentials.md](credentials.md). |
| `catalog-sync` | Catalog | Every `CatalogSync:IntervalHours`, and when the work starts with an empty catalog | Syncs the catalog of every provider that the configuration file holds a catalog key for (FR-CAT-2). See [catalog.md](catalog.md). |
| `clock-watch` | Exchange Gateway | Every `Binance:ClockCheckSeconds`, and whenever the work starts | Reads Binance's clocks, which the clock of the service is held against (FR-BIN-5). What it reads is kept in memory, so a start has no reading. See "The clock" in [exchange.md](exchange.md). |
| `decision-cycles` | Decision Engine | Every quarter hour, on the quarter hour | Starts a cycle for each running agent on a timeframe of which a candle closed then, and lets the cycles wait in a queue for their turns (FR-CYC-1, FR-CYC-11). Before that it has Agents resume the agents that their daily loss limit halted on an earlier UTC day (FR-RSK-8), and each cycle evaluates the loss limits of its agent before its team is called. A run is over before the next quarter hour. See [cycles.md](cycles.md). |
| `order-settling` | Decision Engine | Every minute, and when the work starts with something to settle | Settles the orders of the agents that have something to settle with the exchange: an order whose outcome is unknown is asked about, an open position without a stop-loss is protected or closed at market, and a stop-loss that rests is asked about once in a while (FR-BIN-6, FR-RSK-7). Most runs find no agent. See "Settling" in [orders.md](orders.md). |
| `position-watch` | Decision Engine | Every `Binance:ReconcileMinutes`, and when the work starts with a position to watch | Checks the margin of every open position of Futures, and tells its user when it approaches liquidation, and reads what a position received and paid as funding when that is due (FR-BIN-F4, FR-BIN-F5). It only reads the exchange. A run with no position of Futures asks nobody. See "Watching the positions of Futures" in [orders.md](orders.md). |
| `reconciliation` | Decision Engine | Every `Binance:ReconcileMinutes`, and when the work starts with an agent that holds something | Reconciles every agent that holds an open position or an order that is not over with the exchange, which is the source of truth: asks about its orders, holds its position against what the account of its user holds, corrects the records, and tells its user what did not match (FR-BIN-7, FR-BIN-8). Each agent that is reconciled has its loss limits evaluated then: one that runs and has reached its daily loss limit or its drawdown limit is halted, and its position is closed (FR-RSK-8). After a start, an agent that holds something from before gets no cycle until a run has reconciled it (NFR-REL-3). See "Reconciling" in [orders.md](orders.md). |
| `account-feed` | Decision Engine | Every minute, and when the work starts with an account to follow | Has the gateway follow the account event feed of every account in which an agent holds something, and then listens for 40 seconds: an agent is reconciled at once when its account says a change (FR-BIN-7), and its loss limits are evaluated with it (FR-RSK-8). A run with no account to follow asks nobody, and is over at once. See "The account event feed" in [orders.md](orders.md). |

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

A job on an interval can have work when the background work starts, although its interval has not passed: the catalog is synced at startup when it is empty (FR-CAT-2), Binance's clocks are read at every start (FR-BIN-5), and the orders are settled at a start that finds something to settle (NFR-REL-3). Such a job implements `IDueAtStart` beside `IBackgroundJob`:

```csharp
public Task<bool> IsDueAtStartAsync(CancellationToken cancellationToken) => sync.IsEmptyAsync(cancellationToken);
```

| Rule | Why |
|---|---|
| The scheduler asks once each time the instance takes up the work, before any job starts. | That is at the start of the application, and again after the lock was lost and taken back. It asks under the lock, so an instance that stands by does not ask. |
| A job that says yes runs at once, and its interval goes on from that run. | The run is the job's latest run, like any other. |
| A job that is due anyway is not asked. | One that has never run, one whose interval has passed, and one whose latest run was stopped. |
| **A job whose latest run never finished is not asked.** | It runs when it is next due, whatever it would say: a job that brings the process down must not do so at every start. |
| An error that leaves the question is logged once, as `Error`, and counts as no. | The question is the job's own. It does not keep the other jobs from running. |
| Only a job on an interval can be asked. | A run on boundaries is for its boundary, and the start of the work is none. The application does not start with a job on boundaries that implements `IDueAtStart`. |

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
| Was running when the process ended. | A start with no end | It is reported once, as a warning, when the background work starts again. The job is not run at once, but when it is next due: a job that brings the process down must not do so at every start. That holds for a job that would say it is due when the work starts, which is then not asked. |

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
| `Information` | `This instance runs the background work, with 8 jobs` |
| `Information` | `Another instance runs the background work. This one stands by, and takes over when that one stops` |
| `Information` | `Background job catalog-sync finished in 2310.4 ms` |
| `Information` | `Background job catalog-sync was stopped after 812.0 ms, before it was done` |
| `Error` | `Background job catalog-sync failed after 95.3 ms. It runs again when it is next due`, with the error |
| `Warning` | A run that never finished, a run that is held back because the one before it is still going, a boundary that was skipped because its run could not start in time, a start or an outcome that could not be written, and a lock that was lost. |
| `Error` | `Background job catalog-sync could not say whether it is due now that the background work starts. It runs when it is next due, at ...`, with the error |
| `Error` | `The background work cannot go on. This instance tries again every second until it can`, with the error. Logged once for as long as it lasts. |

What a job logs itself is up to the job. It opens its own correlation scope for what it works on, as [logging.md](logging.md) describes.

## Testing a job

A test registers a job the way a component does, on a database of its own, because on a shared database another test's application may hold the lock:

```csharp
var database = PostgresServer.CreateDatabase();
using var application = ApplicationWithJobs.On(database, clock, job);
```

The scheduler takes the time from the `TimeProvider` service. A test replaces it with `TestClock` and sets the time, so a job that is due in a day runs within a second of the test saying that a day has passed.

Every application of a test has the application's own jobs too, but for six. `clock-watch` is due whenever the work starts, and calls Binance without being asked: in a test of something else that would be a call to a host that is down, or one more call to a Binance of the test's, at a moment that the test does not choose. So `EcnanibApplicationFactory` leaves that job out, unless the test says that its application watches Binance's clock, as `ClockWatchTests` does. `decision-cycles` is due at every quarter hour, and asks the exchange for candles when an agent runs then. Many tests have agents that run, and move a clock over a quarter hour for a reason of their own, so the factory leaves that job out as well, unless the test says that its application runs cycles, as `CyclesApplication` does: see "Testing" in [cycles.md](cycles.md). `order-settling` runs every minute, for the agents whose orders or positions have something to settle, and asks the exchange about them. Many tests give a user orders and positions for a reason of their own, so the factory leaves that job out of the schedule too, unless the test says that its application settles orders. It keeps the job under a key, so that a test of what a run does runs it itself, with no run of the schedule beside it: `AccountsApplication.SettleOrdersAsync`. `position-watch` is due as the work starts when there is a position of Futures, and then every few minutes, and asks the exchange about each. The factory leaves it out of the schedule of every application, and keeps it under a key as well: a test of what a run does runs it with `AccountsApplication.WatchPositionsAsync`. `reconciliation` is due as the work starts when an agent holds anything, and then every few minutes, and asks the exchange about every order that is not over, and about what each account holds. And a cycle waits for an agent that holds something from before the start until that job has reconciled it. Many tests give a user orders and positions for a reason of their own, and start an application over them. So the factory leaves the job out of the schedule, and what holds a cycle back for it out of the application, unless the test says that its application reconciles, as `ReconciliationJobTests` does for the start. It keeps the job under a key: `AccountsApplication.ReconcileAgentsAsync` runs it. `account-feed` runs every minute, for most of its minute. The file of the tests names no address of a feed, so it would follow nothing, and it would still be a run of a minute in every application. The factory leaves it out of the schedule of every application, and keeps it under a key as well: a test of what a run does runs it with `AccountsApplication.FollowFeedAsync`. `key-validation` is on boundaries, so it does not run when an application starts, and with no stored key that is due it asks nobody. `catalog-sync` is on an interval, so it runs once when an application first starts on a database. The configuration file of the tests holds no catalog key, so that run asks nobody and writes nothing but the job's own row. A test that compares what the tables hold before and after a request leaves `background_jobs` out, as `AccountsApplication.EverythingStored` does. A test whose clock jumps over more than one hour finds its warning that the hours between were skipped, so a test that counts the scheduler's warnings names its job. To see what a run of a job does at a time of the test's choosing, a test runs the job itself: `AccountsApplication.CheckKeysAsync` does that for `key-validation`, and `CatalogApplication.RunJobAsync` for `catalog-sync`. `AskedJob` is a job that is asked whether it is due when the work starts, and answers as the test says.
