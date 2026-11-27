# Retention

Table growth is controlled by indexes and by scheduled retention jobs that delete in small batches. Default retention: role transcripts 90 days; decision records 365 days; notifications 90 days; audit events 730 days. Orders, fills, positions, and lessons are kept indefinitely. Equity snapshots are thinned to one per day after 90 days (NFR-DAT-5). No table is partitioned, by any mechanism (NFR-DAT-4).

Decision records and transcripts grow with every cycle (risk R7 of the specification). Four jobs keep the tables that grow from growing without end: each deletes the rows of its component that are past their time, every hour.

The code is `AgedRows` in `src/Ecnanib/Platform/Persistence`, and a class with its job in each component that owns such rows: `RecordRetention` and `EquityThinning` in `src/Ecnanib/DecisionEngine`, `NotificationRetention` in `src/Ecnanib/Notifications`, and `AuditRetention` in `src/Ecnanib/Accounts`. The rules below are enforced by the tests in `tests/Ecnanib.Tests/Retention`.

## What is deleted, and when

| What | Table | Kept for | Counted from | Job |
|---|---|---|---|---|
| What a role answered in a cycle: a role transcript | `role_outputs` | `Retention:TranscriptDays`, 90 days in the tracked file | When the call was made, `called_at` | `record-retention` |
| The record of a cycle: a decision record | `decision_cycles`, and what hangs on it | `Retention:DecisionDays`, 365 days | The close of its candle, `closed_at` | `record-retention` |
| A notification | `notifications` | `Retention:NotificationDays`, 90 days | When it was raised, `created_at` | `notification-retention` |
| An event of the audit trail | `audit_events` | `Retention:AuditDays`, 730 days | When it happened, `occurred_at` | `audit-retention` |
| The equity of an agent over time, but for one row of a day | `equity_snapshots` | 90 days, which is `EquityThinning.AfterDays` and no setting | The UTC day of the row, `at` | `equity-thinning` |

| Rule | Detail |
|---|---|
| **A row goes by its age alone.** | A notification goes read or not, an event of an operator as that of a user, and the cycle of an agent in any state, in Demo and in Live, scheduled or run as a test. |
| **A day is 24 hours, counted back from the moment of the run.** | A row that is exactly as old as its kind is kept stays, and goes when it is older. The jobs run every hour, so a row goes within an hour of that. |
| **The days are read from the configuration file, at startup.** | Each is a whole number from 1 to 36500. See [configuration.md](configuration.md). |
| **A row that went is gone.** | Nothing is moved to another table or to a file first. A user who wants their decisions for longer downloads them: see [export.md](export.md). |

### What a role answered

A role transcript is a row of `role_outputs`: one call of a role, with what the model answered and its rationale. **The whole row goes,** and with it how long the call took, the tokens that its provider counted, and what the validation made of the answer.

| What reads the calls | What it has after they went |
|---|---|
| The timeline of a cycle, and the row of the cycle in the decisions file | The cycle with its data package, its checks of the Risk Guard, its orders, and its outcome, and no call. The file leaves the cells of the roles empty. |
| The AI usage of a user (FR-RPT-3) | Nothing less with the tracked file: the longest time that it adds up is today and the 89 days before, and a call is kept for 90. **A file that keeps the calls for fewer days shortens what the usage can add up,** and the page does not say so. See "AI usage" in [team.md](team.md). |
| What writes the lesson of a position, which reads the plan and the review of the cycle that opened it | A lesson without them, for a position that was open for longer than the calls are kept. See "Writing a lesson" in [experience.md](experience.md). |

### The record of a cycle

A decision record is a row of `decision_cycles`. **What hangs on it goes with it, by its keys, in the same statement:**

| Table | What it holds |
|---|---|
| `role_outputs` | The calls that are left. So the calls are deleted first, and **a file that keeps the calls for longer than the cycles keeps no call for longer than its cycle.** |
| `risk_checks`, and `risk_check_rules` with them | What the Risk Guard made of the plan, with the outcome of every rule. |
| `analyst_calls` | The scored calls of the analysts, which Experience keeps. |
| `shadow_trades` | The shadow trade of a vetoed plan, which Experience keeps. |

So **the strength scores of all time are of the cycles that are kept**: a score counts the calls and the shadow trades that are stored, and the oldest of them are as old as `Retention:DecisionDays`. The track records of trades are of positions, and reach back as far as the positions do. See [experience.md](experience.md).

## What is kept

**Orders, fills, positions, and lessons are kept indefinitely.** No job deletes from `orders`, `fills`, `positions`, or `lessons`, nor from what hangs on a position: `position_results`, `position_roles`, and `fundings`. They go with their agent when it is deleted, and with the account: see [ownership.md](ownership.md).

None of them hangs on a cycle. An order names the cycle that placed it by the id alone, with no key to it, and **goes on naming it when the cycle is gone.** So the `entry_cycle_id` of a trade in the trades file can name a decision that the decisions file no longer has: see [export.md](export.md).

One row of each day of the equity of an agent is kept indefinitely as well: see "Thinning the equity over time".

## Small batches

`AgedRows.DeleteAsync` deletes for all four jobs. A job says which rows of its own are past their time, oldest first, and `AgedRows` deletes them `AgedRows.BatchSize` at a time, which is 500:

```sql
DELETE FROM notifications AS n
WHERE n.id IN (
    SELECT n0.id
    FROM notifications AS n0
    WHERE n0.created_at < @before
    ORDER BY n0.created_at
    LIMIT @p)
```

| Rule | Why |
|---|---|
| **A batch is a statement of its own, in a transaction of its own.** | It holds its locks for as long as 500 rows take, which is milliseconds, and no cycle and no page waits for more. What it deleted stays deleted when a later batch fails. |
| **A run goes on until a batch is not full.** | A run that stopped at a count would fall behind a table that grows faster than the count, and nobody would be told. The first run on a database that was never cleaned takes as many batches as that needs. |
| **A run can be stopped between two batches, and done again.** | The next run finds what is left: a row is past its time or it is not, whatever a run before it did. |
| **The oldest rows go first.** | So a run that was stopped has left the newer ones, and each batch reads the start of its index. |
| **What goes with a row by its keys is not counted.** | A batch of 500 cycles deletes the checks, the calls, and the rows of Experience of those cycles with them, in its one statement. |
| **`AgedRows` is in `Platform`, and names no table.** | The platform must not name a component's types: a component hands it a query over its own entity. |

A deleted cycle and a deleted notification are said to the pages that follow, as every change of those tables is: an open page of their user asks for itself again. See [live-updates.md](live-updates.md).

## The indexes

A job has no owner to ask by: it reads the rows of every user by their time. So each table has an index that starts with the time, which the job reads the start of, once for every batch (NFR-DAT-5):

| Table | Index | Reads |
|---|---|---|
| `role_outputs` | `ix_role_outputs_called_at` | The calls that are older than they are kept, oldest first. |
| `decision_cycles` | `ix_decision_cycles_closed_at` | The cycles that are older than they are kept, oldest first. |
| `notifications` | `ix_notifications_created_at_all` | The notifications that are older than they are kept, oldest first. It has a name of its own, because `ix_notifications_created_at` is the index of the ones that wait for an email. |
| `audit_events` | `ix_audit_events_occurred_at` | The events that are older than they are kept, oldest first. |
| `equity_snapshots` | `ix_equity_snapshots_at` | The rows of a day, of every agent, and the oldest row of all. |

What goes with a cycle is found by the indexes that those tables had: each starts with `cycle_id`, and that of `risk_check_rules` with `check_id`. The migration `AddRetentionIndexes` adds the five indexes, and changes no row.

## Thinning the equity over time

The equity of an agent that holds a position is recorded once in a quarter of an hour, which is 96 rows for a day: see "The equity of an agent over time" in [orders.md](orders.md). `EquityThinning` leaves one of them for a day of longer ago than 90 days.

| Rule | Detail |
|---|---|
| **The row that is kept is the latest of its UTC day.** | It is the equity that the day ended with, which is the value that the curve holds until the next row. |
| **A day keeps a row for each agent, in each mode, and in each asset.** | What an agent did in Demo is never combined with what it does in Live (FR-BIN-11), and an equity in one asset is no equity in another. |
| **Only a whole day is thinned.** | A day is thinned when it began more than 90 days before the day of the run, so every row that goes is older than 90 days. No row is written into such a day any more, so a day that was thinned stays thinned. |
| **A day is a batch.** | The job deletes the rows of one day at a time, through `AgedRows`: every row that a later row of the same day follows, for the same agent in the same mode and asset. A day with more than 500 such rows is several statements. |
| **The job remembers how far it came, in memory.** | The first run of an instance goes through every day from the oldest row on, and a later run through the days that have come of age since: one day in 24 runs. Nothing depends on what it remembers but how much a run reads. An instance that starts anew goes through every day once more, each of which is a statement that reads the one row of each agent, and deletes what a run that was cut short left. |

**The curve of an agent keeps its shape, and loses what happened within a day of long ago.** The lowest and the highest equity that the screen of an agent says, and the largest drawdown of the recorded equity, are worked out from the rows that are stored: a low within a day that has been thinned is in none of them any more. The loss limits are not worked out from these rows: the peak that the drawdown limit is counted from is kept in `equity_marks`. See [agent-screen.md](agent-screen.md) and [risk-guard.md](risk-guard.md).

## The jobs

| Job | Component | What a run does |
|---|---|---|
| `record-retention` | Decision Engine | Deletes the calls of roles that are older than `Retention:TranscriptDays`, and then the cycles that are older than `Retention:DecisionDays`. One job does both, in that order: the two would otherwise delete the same rows at the same time. |
| `equity-thinning` | Decision Engine | Thins the days of the equity over time that have come of age. |
| `notification-retention` | Notifications | Deletes the notifications that are older than `Retention:NotificationDays`. |
| `audit-retention` | Accounts | Deletes the events of the audit trail that are older than `Retention:AuditDays`. It records nothing in the trail: the trail says what a user or an operator did, and no one did this. |

Each job is in the component that owns its rows, since `Platform` names no component's types. Each runs every hour, on an interval and not at a time of the day: a run that was missed while the application was down is made up for with one run, a run that failed is done again within the hour, and what an hour brings past its time is little. At midnight UTC, when a job of a day would run, the cycles of every timeframe start. See [background-work.md](background-work.md).

No job asks whether the global halt is on: a halt stops what opens a position, and nothing else. See [risk-guard.md](risk-guard.md).

The operator status screen shows each job with its latest run, and the sizes of the tables, which is how an operator sees that the jobs keep up (FR-OPS-2): see "The sizes of the tables" in [persistence.md](persistence.md). A size is what the table takes on the disk, and deleting rows does not hand the disk back: the database uses the room again for the rows that come.

## The log

Each job logs under its own area: `Ecnanib.DecisionEngine.RecordRetentionJob`, `Ecnanib.DecisionEngine.EquityThinningJob`, `Ecnanib.Notifications.NotificationRetentionJob`, and `Ecnanib.Accounts.AuditRetentionJob`.

```
{Calls} calls of roles and {Cycles} records of cycles were deleted, which were kept no longer
{Snapshots} rows of the equity of agents over time were deleted, of days that keep one row each
{Notifications} notifications were deleted, which were kept no longer
{Events} events of the audit trail were deleted, which were kept no longer
```

A run that deleted something says how much, as information, and a run that deleted nothing says nothing. An event holds counts, and names no user and no agent. A run that failed is logged by the scheduler, with the error: see "The log" in [background-work.md](background-work.md).

## Testing

`RecordRetentionTests`, `EquityThinningTests`, `NotificationRetentionTests`, and `AuditRetentionTests` each run their job themselves, at a moment of their choosing, on a database of their own: no test application runs a retention job on a schedule, because many tests store rows of long ago for a reason of their own. `EcnanibApplicationFactory` keeps the four jobs under a key each, and `AccountsApplication` runs them: `DeleteAgedRecordsAsync`, `ThinEquityAsync`, `DeleteAgedNotificationsAsync`, and `DeleteAgedAuditEventsAsync`.

Each class has the job with its name and its schedule, a row that is exactly as old as its kind is kept beside one that is a second older, the rows of two users, a run that finds nothing, what the job logs, more rows than a batch holds, and the index in the model and in the database. The tests of the batches read the statements that the mapper logged: each has a limit, and there are as many as the rows need.

`RecordRetentionTests` stores whole cycles with `GiveCycleAsync`, which leaves a row in every table that hangs on a cycle and in every table that is kept indefinitely, and counts both before and after. `EquityThinningTests` has the days on both sides of the 90 days, an agent in two modes and two assets, the days that come of age as the clock goes on, and an instance that starts anew.

That no table is partitioned is held by `ModelTests.No_migration_partitions_a_table` and `SingleStoreTests.No_table_is_partitioned`: see [persistence.md](persistence.md).

## Not built yet

- Saying on the timeline of a cycle that what its roles answered was deleted. A cycle of longer ago than the calls are kept shows no call, and nothing in their place. The lists of the cycles reach back 50 cycles, so few such timelines are reached.
- Saying on the page of the AI usage that it adds up less than its time, with a file that keeps the calls for fewer than 90 days.
- A setting for the 90 days after which the equity over time is thinned, for the size of a batch, and for the time between two runs. The specification names the four settings of `Retention`, and these are in the code.
- Deleting what else is left behind: the sessions and the waiting sign-ins of users who never come back, and the accounts that were never verified. See "Not built yet" in [accounts.md](accounts.md).
- Deleting from the other tables that grow with time: the changes of the state of an agent, `agent_state_changes`, and the runs of the catalog sync, `catalog_sync_runs`. The specification gives neither a time.
- Keeping the audit events of operator actions for longer than those of users.
- Handing the room of deleted rows back to the disk. The database uses it again, and an operator who wants it back rewrites the table.
- A pause between two batches. A run deletes as fast as the database takes it, a batch at a time.
