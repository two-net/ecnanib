# Backups

The database is backed up daily and restores are tested. A migration that drops or rewrites data is marked as destructive and is preceded by a backup (NFR-DAT-11).

The application does all three itself. A job backs the database up with `pg_dump`, another restores the latest backup to a database of its own and records how that went, and the start of the application backs the database up before it applies a migration that is marked as destructive.

The code is in `src/Ecnanib/Platform/Persistence`: `DatabaseBackups` with its job, `RestoreTests` with its job and the entity `RestoreTest`, `PostgresTools`, which runs the tools, `DestructiveMigrationAttribute`, and the part of `DatabaseMigrator` that asks for a backup. The rules below are enforced by `DatabaseBackupTests`, `RestoreTestTests`, `MigrationBackupTests`, and `DestructiveMigrationTests` in `tests/Ecnanib.Tests/Persistence`.

## What is backed up, and when

| Backup | When | Its name ends in |
|---|---|---|
| The scheduled one | Every `Backup:IntervalHours`, which is 24 in the tracked file, by the job `database-backup` | `-scheduled` |
| Before a destructive migration | As the application starts, when a pending migration is marked as destructive | `-before-<migration>` |
| For a restore test | When a restore is to be tested and there is no backup yet | `-restore-test` |

| Rule | Detail |
|---|---|
| **A backup is the whole database.** | Every table with its rows, and the functions and triggers that the migrations made, as `pg_dump` writes them in its custom format. `pg_restore` restores it, with or without the application. |
| **The database is backed up daily, or more often.** | `Backup:IntervalHours` is from 1 to 24: a file can ask for more backups, and not for fewer. |
| **The job is on an interval, and not at a time of the day.** | A backup that was missed while the application was down is made up for at the next start, and a database that was never backed up is backed up at once. See [background-work.md](background-work.md). |
| **One backup is taken at a time.** | Whoever asks for the latest backup waits for the one that is under way. |
| **A backup does not stop the application.** | `pg_dump` reads the database in a snapshot and keeps no writer waiting. A cycle and a page go on while it runs. |

## The files

The backups are in the folder of `Backup:Directory`, which is `backups` under the folder that the application runs from in the tracked file. A backup is two files of one name:

```
ecnanib-20261127T031500Z-scheduled.dump
ecnanib-20261127T031500Z-scheduled.json
```

The name is the database, the time in UTC at which the backup was taken, and why. Two backups of one second differ by a number: `-scheduled-2`.

The `.dump` is what `pg_dump` wrote. The `.json` is the manifest, which says what the backup holds:

```json
{
  "takenAt": "2026-11-27T03:15:00+00:00",
  "reason": "scheduled",
  "database": "ecnanib",
  "migration": "20261127062107_AddRestoreTests",
  "tables": {
    "__EFMigrationsHistory": 50,
    "agents": 12,
    "users": 4
  }
}
```

| Rule | Why |
|---|---|
| **What the manifest counts is what the backup holds.** | The rows are counted in a snapshot of the database, and `pg_dump` is told to read that same snapshot, with `--snapshot`. A row that is written while the backup is taken is in both, or in neither. So a restore can be held to the manifest row for row. |
| **Every table of the schema is counted,** the one that keeps the migrations among them. | The tables are listed from the catalog of the database, as their sizes are: `Platform` names no table of a component, and a table that nobody thought of is counted too. |
| **A backup exists once its manifest does.** | The tool writes to `<name>.dump.partial`, which is given its name when the tool has ended well, and the manifest is written last. A backup that was cut short, by a stop or by a full disk, leaves nothing that is taken for a backup. |
| **The files are for the account of the application alone.** | A backup holds everything that the database holds: the email addresses, the password hashes, and the keys of users in their encrypted form. The folder is made with mode 700 and each file with 600. A folder that is there already keeps the mode that it has. |
| **A backup holds what was deleted since it was taken.** | An account that its user deleted, and the rows that a retention job deleted, are in the backups from before, for as long as those are kept: `Backup:KeepDays`, and as long again wherever the folder was copied to. See "That nothing is left" in [accounts.md](accounts.md). |
| **Nothing copies a backup elsewhere.** | A backup on the machine of the database does not outlive that machine. Copying the folder to another machine is the deployment's: see "Not built yet". |

## The tools

The application runs two client tools of PostgreSQL, `pg_dump` and `pg_restore`, which must be installed where the application runs. `Backup:PgDump` and `Backup:PgRestore` name them: a name that is found on the path of the process, as in the tracked file, or a full path.

| Where | The tools |
|---|---|
| Debian and Ubuntu | `apt install postgresql-client-18`, from the repository of PostgreSQL. |
| macOS | `brew install libpq`. Homebrew does not put it on the path: add its `bin` folder, or name the two tools by their full paths in the configuration file. |
| Tests | The tools of the PostgreSQL container that the tests start, run inside it. The tests need Docker with its command, and no PostgreSQL on the machine. See "Testing". |

**The tools are at least as new as the server.** `pg_dump` refuses a server of a later major version than its own. The server is PostgreSQL 18, so the tools are of 18 or later.

`PostgresTools` runs them, and is the one type of the application that starts a process, which a test pins.

| Rule | Why |
|---|---|
| **Nothing secret is on a command line.** | Every account of the machine can read the command line of a process. The tool is told the server and who asks in the variables of its own process, which the client library of PostgreSQL reads: `PGHOST`, `PGPORT`, `PGUSER`, `PGPASSWORD`, `PGPASSFILE`, `PGSSLMODE`, and `PGSSLROOTCERT`, from the connection string of the configuration file. The database is an argument, and is no secret. |
| **The tool goes by the configuration file alone.** | A `PG` variable that the process of the application carries is not passed on. |
| **What a tool reads and writes goes through the application.** | A backup is what `pg_dump` wrote to its standard output, and `pg_restore` reads it from its standard input. So a tool is given no path, and need not run where the files are. |
| **A tool that fails says why.** | A tool that could not be started, or that ended with another code than 0, fails what asked for it, with what the tool wrote to its standard error. |
| **A stop ends the tool.** | When the application stops, or stops being the instance that runs the background work, the tool is ended with whatever it started. |

What else the connection string says, such as a client certificate or a time limit, is not handed to the tools.

**Without the tools, nothing else of the application is affected.** The job `database-backup` fails as it is first due, and then every day, and the log says that the tool could not be started. The one exception is a start on a database that is behind a destructive migration: see "At startup".

## How long a backup is kept

After each scheduled backup, the job deletes the backups that were taken longer ago than `Backup:KeepDays`, which is 14 in the tracked file.

| Rule | Detail |
|---|---|
| **A day is 24 hours, counted back from the moment of the run.** | A backup that is exactly as old as backups are kept stays, and goes when it is older. |
| **A backup goes by its age alone,** whatever it was taken for. | The backup of before a migration is kept as long as a scheduled one. |
| **The latest backup stays, however old it is.** | An application that was away for longer than backups are kept still has one until the next is taken. |
| **The manifest is deleted first.** | What is left when the deleting is cut short is no backup. |
| **What a backup that was cut short left is deleted after a day:** a `.partial` file, and a `.dump` that never got its manifest. | It is left for a day, in case another instance is still writing it. |

## Testing a restore

The job `restore-test` tests a restore every `Backup:RestoreTestDays`, which is 7 in the tracked file. One test:

1. Takes the latest backup. When there is none yet, it takes one itself, so that the first start of an application does not test nothing.
2. Makes a scratch database on the same server, named after the database of the application with `_restore_test` behind it: `ecnanib_restore_test`. One that an earlier test left is dropped first.
3. Restores the backup to it, with `pg_restore --no-owner --no-privileges --exit-on-error`.
4. Reads what the scratch database holds, and holds it to the manifest: the migration that it is at, its tables, and the rows of each table.
5. Drops the scratch database, however the test went.
6. Records the test, as a row of `restore_tests`.

**The role of the application needs the right to create a database,** for the scratch database alone:

```sql
ALTER ROLE ecnanib CREATEDB;
```

Without it the test fails, and is recorded as not restored. The database of the application is still the deployment's to create: see [persistence.md](persistence.md).

| Rule | Why |
|---|---|
| **The database of the application is only read.** | A test restores to a database of its own, and writes one row to `restore_tests`. |
| **The one database that is ever dropped is the scratch database.** | Its name is worked out by the server, from the name of the database that the application is connected to. A name that the server would cut short, one of more than 63 bytes, fails the test: the name that was cut could be another database's. Nobody who is connected to a database is put out to drop it. |
| **A test is of the backup, and not of the database as it is now.** | What was written since the backup was taken is not in the manifest, and does not fail the test. |
| **A test that fails fails the run of the job.** | The scheduler logs the error once, with what differs or what the tool said, and the operator status screen shows the latest run of `restore-test` as failed. See "The status of the jobs" in [background-work.md](background-work.md). |
| **A test that was stopped is no test.** | It records nothing, and drops the scratch database. The job is due at once when the background work starts again. |
| **A test takes room.** | While it runs, the server holds the database twice. |

### The record

`restore_tests` has a row for every test that ended:

| Column | Meaning |
|---|---|
| `backup` | The backup that was restored, by the name of its file. |
| `backup_taken_at` | When that backup was taken. |
| `started_at`, `finished_at` | When the test started and ended. |
| `result` | `Succeeded` or `Failed`. |
| `failure` | Why it failed: `NotRestored` or `Differs`. Empty for a test that succeeded. |
| `tables`, `rows` | How many tables the restored database had, and how many rows they held together. Zero for a backup that was not restored. |

| Failure | Meaning |
|---|---|
| `NotRestored` | The scratch database could not be made, or `pg_restore` ended with an error. |
| `Differs` | The backup was restored, and the database that came of it is not what the manifest says: it is at another migration, lacks a table, has a table that the backup did not have, or holds other rows. |

A row holds no text of an error and nothing that a tool wrote: those are in the log (NFR-DAT-7). The rows belong to the service and name no user, so the table is listed in `OwnershipRules.cs`: see [ownership.md](ownership.md). No job deletes them: a test of a week is a row of a week.

## Destructive migrations

A migration that drops or rewrites data carries `[DestructiveMigration]` on its class, in the file that is read and edited, and not in the designer file:

```csharp
/// <inheritdoc />
[DestructiveMigration]
public partial class AddSignIn : Migration
```

**`DestructiveMigrationTests` fails for a migration that drops or rewrites data and is not marked.** The rule is `DestructiveRules.cs` in the tests, which reads the operations of every migration:

| A migration is destructive when it | As |
|---|---|
| Drops a table, a column, or a schema | `DropTable`, `DropColumn`, `DropSchema` |
| Deletes or updates rows, seeded ones included | `DeleteData`, `UpdateData` |
| Changes the type of a column | `AlterColumn` with another type. A column that only becomes required holds what it held. |
| Sends a statement of its own that does one of these | `Sql` that begins with `UPDATE`, `DELETE`, `TRUNCATE`, `DROP TABLE`, or `DROP SCHEMA`, an `ALTER TABLE` with `DROP COLUMN` or with a new `TYPE`, a `WITH` that goes on to `UPDATE` or `DELETE`, and an `INSERT` with `ON CONFLICT DO UPDATE` |

| Rule | Detail |
|---|---|
| **A statement that fills a column which the same migration added counts.** | The rule cannot tell it from one that writes over what was stored. Most of the migrations that are marked today are of this kind. |
| **The rule reads the words that a statement begins with.** | It does not see what a function does that a statement calls. Whoever writes such a migration marks it: the mark can be on any migration. |
| **The marked migrations are pinned.** | `DestructiveMigrationTests` lists them, so that a mark that is taken away shows in a review. A new destructive migration is added to that list. |
| **Marking a migration changes nothing that it does.** | The mark is an attribute. The migrations that were there before the rule were marked where the rule found them, and are otherwise as they were applied. |

### At startup

The application applies the pending migrations as it starts, under the migration lock: see "At startup" in [persistence.md](persistence.md). When one of them is marked, it backs the database up first:

1. It takes the lock, and reads which migrations are pending.
2. When a pending migration is marked, and the database has had a migration applied before, it takes a backup, named after the first marked migration: `ecnanib-20261201T080000Z-before-20261201075500_DropLegacyKeys.dump`.
3. It applies the pending migrations, all of them, in one transaction.

| Rule | Why |
|---|---|
| **When the backup fails, no migration is applied, and the application does not start.** | A destructive migration does not run without its backup. The error is logged once, as `Critical`, and the exit code is 1, as for a migration that fails. Fix the cause and start again. |
| **One backup is taken, before the first of the pending migrations.** | The migrations of one start are one transaction. The backup is of the database as the earlier version left it. |
| **A database that nothing was applied to is not backed up.** | It holds nothing that a migration could drop or rewrite. So a new deployment, and a test on a new database, start without the tools. |
| **Pending migrations of which none is marked are applied without a backup.** | |
| **The backup is not tested before the migration.** | A test restores the whole database, and would hold up the start for as long again. The schedule tests the restores. |
| **A start that applies a marked migration takes longer,** by one backup. | The instances that wait for the migration lock wait for it too. |

In development, a database of `docker compose` that is behind a marked migration needs the tools as well. Without them, install them, or start from an empty database: `docker compose down -v` and `docker compose up -d`.

## The jobs

| Job | Schedule | What a run does |
|---|---|---|
| `database-backup` | Every `Backup:IntervalHours` | Backs the database up, and then deletes the backups that are older than `Backup:KeepDays`, but for the latest. |
| `restore-test` | Every `Backup:RestoreTestDays` days | Restores the latest backup to a scratch database, holds it to the manifest, records the test, and fails when the test failed. |

Both are jobs of `Platform`, the first that it has: they name no table of a component. Neither asks whether the global halt is on.

## The log

`DatabaseBackups` logs every backup, whatever it was taken for, under `Ecnanib.Platform.Persistence.DatabaseBackups`:

```
The database was backed up to {Backup}: {Tables} tables, {Rows} rows, {Bytes} bytes
```

The jobs and the start log under their own areas, `Ecnanib.Platform.Persistence.DatabaseBackupJob`, `Ecnanib.Platform.Persistence.RestoreTests`, and `Ecnanib.Platform.Persistence.DatabaseMigrator`:

```
{Backups} backups were deleted, which were kept no longer
The backup {Backup} was restored to a database of its own, which holds what the backup says: {Tables} tables, {Rows} rows
Backing up the database before the migration {Migration}, which is marked as destructive
```

A run that failed is logged by the scheduler, with the error: see "The log" in [background-work.md](background-work.md). The error of a restore test that failed says what differs, or what `pg_restore` said:

```
The restore of the backup ecnanib-20261127T031500Z-scheduled.dump was tested, and failed.
 ---> The database that came of the backup is not what the backup says: the table users holds 3 rows in place of 4.
```

An event names files, tables, and counts, and no user.

## Restoring by hand

A backup is restored with `pg_restore`, to a database that is empty:

```
createdb ecnanib_restored
pg_restore --no-owner --no-privileges --exit-on-error --dbname=ecnanib_restored backups/ecnanib-20261127T031500Z-scheduled.dump
```

Then name that database in `ConnectionStrings:EcnanibDbContext` and start the application. A backup that is at an earlier migration than the application is brought to the model as the application starts, like any database that is behind. The master keys that encrypted the stored secrets of the backup must be in `Security:MasterKeys`: see "Changing the master key" in [security.md](security.md).

After a restore, the exchange is still the source of truth for what the agents hold: the reconciliation corrects the positions and the orders that the backup has from before. See "Reconciling" in [orders.md](orders.md).

**A restore brings back what was deleted since the backup was taken.** An account that its user deleted since is there again, with its keys. The restored database does not say which accounts those are: the audit events of an account go with it. The log does, by their ids, for as long as its files are kept: `User {UserId} deleted their account`. Whoever restores a backup deletes those accounts again.

## Testing

**The tests run the real tools.** `ServerTools`, beside `PostgresServer`, writes two scripts that run `pg_dump` and `pg_restore` inside the container of the test server, with `docker exec`. `SettingsJson.OnDatabase` names them in every file that is pointed at a database of the test server, unless the test named a tool of its own. So the tests need Docker with its command, and nothing else. The scripts are of the shell, so these tests run on Linux and macOS.

**No application of a test backs up or tests a restore on a schedule.** Both jobs are due as the background work starts, and each starts a tool. `EcnanibApplicationFactory` keeps them under a key each, and `BackupApplication` runs them, at a moment of the test's choosing and by a clock that the test moves: `BackUpAsync` and `TestRestoreAsync`.

**An application of a test that starts on a database which is behind a marked migration backs it up,** as outside the tests: the tests of earlier migrations, such as `EmailVerificationTests`, do.

`DatabaseBackupTests` has the job with its name and its schedule, the files with their modes, the manifest against the database, a tool that is not there, a tool that fails, what a tool is told, a run that is stopped, a backup that is exactly as old as backups are kept beside one that is a second older, and what a backup that was cut short left. `RestoreTestTests` has a test that succeeds with its row, a backup that is half of what the tool wrote, a manifest that says one row more, a scratch database that was left, and a test that is stopped. `MigrationBackupTests` starts the application on a database that is behind a marked migration, on a new one, on one that is behind migrations of which none is marked, and with a tool that is not there. `DestructiveMigrationTests` holds the migrations to the rule, and the rule to operations that it must find and must not.

## Not built yet

- Copying the backups to another machine. A backup beside its database is lost with the machine. A deployment copies the folder of `Backup:Directory` elsewhere, with a tool of its own.
- Encrypting a backup. The files are for the account of the application alone, and hold what the database holds.
- A part of the operator status screen for the backups and for the tests of `restore_tests`. The screen shows both jobs with their latest runs.
- Telling an operator that a backup or a restore test failed, other than by the log and the status screen.
- A backup on demand, and a restore from the application.
- Recovery to a moment between two backups. A backup is the database as it was when it was taken.
- Deleting again, after a restore, the accounts that were deleted since the backup was taken. The log names them, and whoever restores does it by hand.
- Testing a restore on another server than the one of the database.
- Deleting the old rows of `restore_tests`.
- Settings for the day after which a leftover file is deleted, and for the name of the scratch database.
- Handing the tools what else a connection string says, such as a client certificate.
