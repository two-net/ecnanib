# Architecture

Ecnanib is a modular monolith: one deployable project, one process and one database (CON-08). This document turns the logical architecture in [SRS 2.2](../SRS.md#22-logical-architecture) into code boundaries, as NFR-MNT-01 requires.

All modules share one assembly, so `internal` doesn't separate them, and the compiler won't stop a module from reaching into another. **The tests in `tests/Ecnanib.Tests/Architecture` are the only guard on these boundaries.** They run in CI, so CI needs to stay green.

## Units

| SRS 2.2 | Code |
|---|---|
| Web UI | `src/Ecnanib/Components/` |
| API | `src/Ecnanib/Endpoints/`, added when needed. See [The API](#the-api) |
| Identity & Preferences | `Modules/Identity` |
| Credentials | `Modules/Credentials` |
| Model Catalog & Sync | `Modules/Catalog` |
| Agents | `Modules/Agents` |
| Scheduler | `Modules/Scheduler` |
| Decision Cycle | `Modules/DecisionCycle` |
| Market Data | `Modules/MarketData` |
| AI Council | `Modules/AiCouncil` and `Modules/AiAdapters` |
| Risk Engine | `Modules/RiskEngine` |
| Execution & Reconciliation | `Modules/Execution` and `Modules/ExchangeAdapter` |
| Trading Experience | `Modules/Experience` |
| Notifications | `Modules/Notifications` |
| Administration | `Modules/Administration` |
| Structured Logging, Audit Trail, Configuration, Persistence | `Platform/`, one subfolder per concern |

Trade Approval (3.11) belongs to `DecisionCycle`, because an approval hangs off a cycle. Retention (3.18) is one purge job per data owner.

### Where the code differs from SRS 2.2

- **The adapters are separate units.** Credential validation calls the exchange and AI providers (FR-KEY-04, FR-KEY-05), while Execution and AI Council need Credentials (FR-KEY-09). Inside those modules, the adapters would create a cycle.
- **`Platform/` also holds Jobs, Secrets and Email**, which 2.2 doesn't place. Their callers sit below Scheduler, Credentials and Notifications. `Modules/Scheduler` is candle scheduling only (#30); the general job runner (#6) goes in `Platform/Jobs`.
- **`Kernel/` is added** for shared vocabulary.

## Rules

1. A module's public surface is its `Contracts` namespace, plus one `<Module>Module` entry class with an `Add<Module>()` method. Everything else is `internal`.
2. A unit may use only the `Contracts` of units listed for it in [`ModuleMap.cs`](../tests/Ecnanib.Tests/Architecture/ModuleMap.cs). That map is the single source of truth for dependencies, and it must stay free of cycles.
3. `Program.cs` is the composition root. It calls every `Add<Module>()` explicitly, and is the only code allowed to use an entry class. There's no module scanning.
4. The Risk Engine depends on `Kernel` only. It takes explicit inputs and returns results, which keeps it provably deterministic (FR-RISK-01). It defines its own intent type, so Appendix B types never reach it and AI output can't bypass it (NFR-SEC-08). This rule has its own test that doesn't read the map, so editing the map can't weaken it.
5. `Kernel` holds only vocabulary that the SRS fixes, that at least two modules use, and that has no behaviour, such as `Market`, `Timeframe` and typed IDs. Moving a type there to escape a cycle is the wrong fix: use a port or event instead.
6. Razor components stay in `Components/`, because pages such as agent detail span several modules (FR-UI-01).

The Web UI may use neither adapter nor `Platform`, so it can't reach secrets or place orders directly (2.4, FR-ADM-01, FR-KEY-09). `Endpoints/` counts as Web UI, so it reaches `Platform` only through framework abstractions, as the health endpoints do with the checks `Platform` registers. That's also why the [HTTP security code](#web-security) lives in `Endpoints/Security`: pages need its nonce and endpoints need its policy names.

### Adding a dependency

Use the other module's `Contracts` in your code. If the boundary tests fail, add the edge to `ModuleMap.cs` in the same change, and say which requirement needs it. If that creates a cycle, use an inversion instead.

### Adding a module

Create `Modules/<Module>/<Module>Module.cs`, add the module to `ModuleMap.cs`, and call `Add<Module>()` from `Program.cs`. The tests fail if you skip any of these steps.

## Inversions

Some needs run in both directions, and a reference can't solve that. Separating contracts from implementations would only move the cycle from compile time to run time, so each pair needs a fixed direction. The lower module declares a port or in-process event in its `Contracts`, and the higher module implements or subscribes to it. Events run in process, with no broker (CON-08).

| Pair | Need | Direction |
|---|---|---|
| Agents, Execution | Stop, kill switch, open-exposure checks (FR-AGT-03, FR-AGT-07, FR-AGT-08, FR-AGT-13, EXT-BIN-07) | Port declared by Agents, implemented by Execution |
| Credentials, Agents | Pause agents on credential failure or deletion (FR-KEY-10, FR-KEY-11) | Event from Credentials |
| Credentials, Catalog | Sync when a credential is added or rotated (FR-CAT-03) | Event from Credentials |
| Catalog, Agents | Model deprecation and retirement (FR-CAT-04) | Event from Catalog |
| DecisionCycle, Experience | Attribution, metrics and reflections (FR-EXP-01, FR-EXP-02, FR-EXP-04) | Event from DecisionCycle, plus a reflection port it declares |
| Execution, Experience | Review after a trade closes (FR-EXP-04) | Trade-closed event from Execution |
| AiCouncil, Experience | Reflections in prompts (FR-PRM-01, FR-EXP-04) | DecisionCycle passes them in as runtime context |
| Agents, RiskEngine | Auto-pause on loss limits (FR-RISK-08) | The engine returns a result and its caller acts on it |
| Identity, data owners | Account deletion and export (FR-AUTH-09, FR-DATA-03) | Port declared by Identity, implemented by each owner |
| DecisionCycle, Administration | Emergency pause (FR-ADM-05) | DecisionCycle owns the state; Administration only commands it |
| Modules, Web UI | Live updates (FR-UI-05, FR-ROLE-08) | Events that the Web UI subscribes to |

In-process events are lost on a crash, so each one needs a recovery path, such as periodic revalidation or reconciliation.

## The API

"API" in SRS 2.2 is not a separate module. Pages run on Blazor Server and reach services directly, and the SRS defines no public HTTP API or consumer of one. Module `Contracts` are the application's API, and `Endpoints/` holds the HTTP that really is needed, such as sign-in cookies (NFR-SEC-06), health (NFR-OBS-01) and exports (FR-DATA-03).

**After page load, a Blazor circuit doesn't pass through HTTP middleware.** Access rules therefore can't live there: ownership checks (NFR-SEC-04) belong behind module contracts. [Correlation IDs](#correlation-ids) and [rate limiting](#circuits) each have a plan for circuit traffic, and audit fields such as IP address still need one (#11). Request logging doesn't: FR-LOG-06 covers HTTP, so a circuit logs one `/_blazor` request, and activity inside it is logged where it happens, behind module contracts.

## Persistence

`EcnanibDbContext` in `Platform/Contracts` is the one context, with one linear migration chain, because the SRS has one database and one migration version (FR-ADM-04). The code-first model owns the schema, so nobody changes it by hand (DATA-R-05).

- **Modules own their entities.** A module keeps its entities and their `IEntityTypeConfiguration` internal, and reaches them with `Set<T>()`, so the boundary tests cover them. The context finds configurations with `ApplyConfigurationsFromAssembly`. This is the one exception to "no module scanning" in rule 3: contributing the model through DI would leave `dotnet ef` with an empty model at design time, and it would generate drops.
- **Reference another module's rows by ID only**, with no navigations. Whether a foreign key constraint can cross modules isn't decided: `HasOne<OtherModuleEntity>()` fails the boundary tests, so the first issue that needs one decides.
- **Contexts are short-lived.** A Blazor circuit keeps its scope for as long as the user stays connected, so the context isn't in DI, and a service that injects it fails at startup. Inject `IDbContextFactory<EcnanibDbContext>`, and dispose each context when the operation ends.
- **The caller owns the context, which keeps writes atomic.** Anything that must commit with a caller's change, such as an audit record (#11), takes the caller's context, adds to it and doesn't save. One `SaveChangesAsync` then commits both (FR-AGT-04, NFR-REL-04). Across several saves, use one transaction on that context. Never hold a transaction open across an exchange call. Leave `EnableRetryOnFailure` off: it rejects transactions started this way.
- **Money and time follow SRS 6.2.** Store amounts as `decimal` next to their asset or currency code, and timestamps as `DateTime` or `DateTimeOffset` in UTC. `SchemaRulesTests` rejects binary floating point and timestamps without a time zone on any mapped column. It can't check that an amount has a code column, so check that in review.
- **Seeders add reference data (NFR-DEP-02).** Implement `IReferenceDataSeeder` and register it in `Add<Module>()`. Seeders run in registration order on every start, so they add only what's missing, and they don't save.

### Startup

`DatabaseStartup` is a hosted service that runs before the web server opens a port. It applies pending migrations, then runs the seeders. Any failure is logged and stops the process with a non-zero exit code (NFR-DEP-03). There's no "starting" state to report, so a health probe during a long migration gets connection refused rather than a 503.

- A failed migration rolls back. Earlier ones stay committed, and the next start resumes from there.
- A failed seeder saves nothing. The migrations stay applied, and the next start seeds again.
- A database with a migration this build doesn't know is refused, because an old build must not run on a newer schema.

Each migration commits separately, so seeding holds neither the migration lock nor a transaction after migrations were applied. That's safe with one instance (ASM-06). Running several instances would need a rethink.

### Adding a migration

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/Ecnanib --output-dir Platform/Persistence/Migrations
```

Then, in the generated `<timestamp>_<Name>.cs`, change the class to `internal` and delete `Down`. The tests fail if you skip either step. `dotnet ef` generates both, but the first breaks rule 1, and `Migration.Down` throws unless a migration overrides it, so deleting it makes rollback impossible.

Migrations are forward-only (NFR-MNT-05). Never edit or delete a released migration: fix forward with a new one.

### Before a release

Test pending migrations against a copy of the production schema (NFR-MNT-05). This is a manual step until there's a release process. Dump the schema and the migration history, because a schema-only dump leaves the history table empty, and the new build would then try to reapply every migration. Restore the dump into a scratch database, then start the new build against it:

```sh
pg_dump --schema-only --no-owner "$PRODUCTION" > schema.sql
pg_dump --data-only --table='"__EFMigrationsHistory"' "$PRODUCTION" >> schema.sql
psql "$SCRATCH" --file=schema.sql
```

The application should start and log the migrations it applied. Don't run this against production.

## Logging

Modules log through `ILogger<T>` and never reference Serilog, so they can't get around anything below. `Platform/Logging` holds the whole pipeline:

```text
ILogger<T>
  -> the framework's LoggerFactory    Serilog's provider is the only provider
  -> ReloadableLogger                 levels, overrides, enrichers, destructuring policy
  -> Async worker                     callers only enqueue
  -> SanitizingSink                   the one gate
  -> Console | File | Database
```

**Every sink sits behind `SanitizingSink`.** Add a sink in `LoggingPipeline.WriteToConfiguredSinks` and nowhere else. `AddLogging` clears the framework's providers because they'd see events unmasked, and a test proves Serilog's is the only one left.

### Writing log events

- Use a constant template with named properties. CA2254 is a build error under `src/`, so an interpolated message doesn't compile.
- Never log a request or response body, or a prompt (FR-LOG-07).
- Set the ambient fields with a scope, using the names in `LogProperties`, except for the correlation ID: use [`ICorrelationContext`](#correlation-ids), because a scope of your own would leave `CurrentId` unset. Scopes flow with the async call, so code further down inherits them, including the adapters, which can't reference `Platform`:

```csharp
using (logger.BeginScope(new Dictionary<string, object?> { [LogProperties.AgentId] = agent.Id }))
{
    await RunCycleAsync(cancellationToken);
}
```

A category is a logger name, and an override matches it by dotted prefix, so `Microsoft.AspNetCore` covers everything under it. SRS 7.3 names three categories that aren't code namespaces. Create those loggers by name with `ILoggerFactory.CreateLogger`: `Http` (in use), `Exchange` and `AiCouncil`.

### Masking

`SanitizingSink` rebuilds each event after enrichment, so it also sees scopes. It masks by property name at any depth and scrubs all text, including the message template and the exception. If it throws, the event is dropped, so it fails closed. `SecretRules` holds the rules, and `CanaryScanTests` is the automated scan AC-03 asks for, which every new rule should extend.

Text is matched by name (`password=`), by shape (`Bearer`, `sk-`, 64 alphanumerics, JWTs) and by exact value. Exact values come from the configuration: any setting in a section whose full key contains a sensitive word, and the password in each connection string. A new secret setting is picked up without code, as long as its key says what it is.

**What it can't do.** A secret with no marker, under a generic name, that isn't in the configuration can't be recognised. `{Value}` holding a decrypted user key is the case that matters, so wrap decrypted secrets in a type that doesn't render its value. The same goes for an `HttpRequestMessage` logged without `@`: it renders through `ToString()`, and a header with an unfamiliar name stays readable. With `@`, `SensitiveTypesPolicy` never takes the request apart.

**Level floors override the configuration, on purpose.** Some framework events can't be scanned, so `SanitizingSink.Floors` drops them whatever the configured level says. This is the one exception to FR-LOG-03:

| Category | Floor | Why |
|---|---|---|
| `Microsoft.AspNetCore.SignalR` | Information | At Debug it logs each hub invocation with its arguments, which is how Blazor form input travels |
| `Microsoft.AspNetCore.Components.Server` | Information | Logs JS interop arguments, and every URI a circuit navigates to, at Debug |
| `Microsoft.AspNetCore.Hosting.Diagnostics` | Warning | Logs raw paths and query strings. The `Http` events replace it |
| `Microsoft.AspNetCore.Routing`, `Microsoft.AspNetCore.Components.Routing` | Information | Log the raw path at Debug |
| `Microsoft.AspNetCore.HttpsPolicy` | Information | Logs the whole redirect URL at Debug |
| `Microsoft.AspNetCore.StaticAssets`, `Microsoft.AspNetCore.StaticFiles` | Information | Log the raw path at Debug |
| `Microsoft.AspNetCore.HttpOverrides` | Information | Logs a raw `X-Forwarded-For` entry at Debug, which a client chooses |
| `Microsoft.AspNetCore.WebSockets` | Information | Logs a raw `Origin` at Debug, which a client chooses |

The list comes from the framework's own log templates, for the features this application uses, so it needs rechecking in two cases. After an ASP.NET Core upgrade, look for new events that carry raw request content. When adding a framework feature, check its events first: `UseHttpLogging` and MVC, for example, log bodies and action arguments, and they aren't on this list because neither is in use. `RequestPath`, which ASP.NET adds to every event in a request, is removed for the same reason.

Two outputs sit outside the sinks and get the same text rules: Serilog's `SelfLog` on stderr, which reports failing sinks, and an exception that escapes `app.Run()`.

### Hot reload

A change to the `Logging` section applies without a restart, for levels, overrides and sinks alike (FR-LOG-03, CFG-04). `ReloadableLogger.Reload` disposes the old logger before it builds the new one, so if building threw, logging would stop until a restart. **`LoggingOptions.Read` must therefore reject anything that could make `LoggingPipeline.Configure` throw.**

- Startup and reload share the same validation. An invalid value or unknown key stops startup, and on reload it's refused: the previous settings stay, and an Error names the setting.
- Deleting the file is refused the same way, because `Logging:MinimumLevel` is required. Logging never falls back to defaults nobody configured.
- Malformed JSON raises no change signal, so the previous settings stay, with no log line.
- The logger is never frozen, so every event takes its lock. That's why the Async worker sits directly behind it, and why masking runs on the worker.
- A reload flushes the old sinks under that lock, so with the database down it can hold callers for up to a connection timeout. Reloads are manual and rare, so this is accepted.

### The database sink

`log_events` is an ordinary entity, so the code-first model owns it. `DatabaseLogSink` builds its own context options with no logger factory, because EF logging its writes would feed the next batch. It starts once `DatabaseStartup` has migrated, since it would only fail before that. Purging is #50's job, which the index on `timestamp` is there for.

## Correlation IDs

`ICorrelationContext` in `Platform/Contracts` holds the current ID (NFR-OBS-02). `Begin()` starts a new one, which flows with the async call. Log events carry it as `LogProperties.CorrelationId`, so it reaches the file sink and `log_events.correlation_id`, which is indexed.

| Work | What calls `Begin()` |
|---|---|
| HTTP request | `CorrelationMiddleware`, which also returns the ID as `X-Correlation-ID` |
| Action on a circuit | `CorrelationCircuitHandler`, once per inbound activity |
| Job run | `JobRunner`, once per run, so `job_runs.correlation_id` matches its logs |
| Cycle | The code that runs it |

- **IDs are always generated here.** An inbound `X-Correlation-ID` is ignored: a client-chosen value could forge links between audit records, and a long one could fail a batch of log rows on the index. `TraceId` isn't used either, because a `traceparent` header sets it.
- **`UseCorrelation` comes first in the pipeline.** Request logging needs the ID, and the exception handler and status pages run everything after them again, which would give a request two IDs.
- **The header is set in `Response.OnStarting`**, because the exception handler clears headers, and a failure is when the ID matters most. The callback uses a captured ID, because a bodyless response can start after the scope has ended.
- **A circuit gets one ID per action, not per connection.** Without the handler, circuit logs would still carry an ID, but it would be the `/_blazor` request's, shared by everything until the user disconnects. An action links to its own logs and audit records, and nothing links actions to each other.
- **The adapters don't need it.** They can't reference `Platform`, and they inherit the scope, so events under the `Exchange` and `AiCouncil` categories are linked already. An `AiCall` row links through its cycle.
- **The ID doesn't cross a queue.** It follows the async call, so work picked up from a queue or timer must call `Begin()` itself. `Begin()` always creates a new ID for the same reason: a timer started during a request captures that request's ID.

The tests run the circuit handler directly, because opening a real circuit needs a browser or Blazor's internal protocol. Add an end-to-end test with the first audited action.

## Health

`/health/live` and `/health/ready` are public, so they answer with fixed values only (NFR-OBS-01):

| Route | Runs | Answers |
|---|---|---|
| `/health/live` | No checks | Always 200 `{"status":"Healthy"}` |
| `/health/ready` | Every registered check | 200 when healthy, otherwise 503 |

```json
{"status":"Unhealthy","database":"connected","migrations":"pending"}
```

`database` is `connected` or `unreachable`. `migrations` is `current`, `pending`, `ahead` or `unknown`, where `ahead` means the database has a migration this build doesn't include, which `DatabaseStartup` would refuse. The application is healthy only when they're `connected` and `current`.

- **Liveness runs no checks.** A database outage must not get the process restarted: a start needs the database, so it would crash-loop.
- **Only `status` and each check's `Data` reach the response.** Masking guards log sinks, not responses, and the framework copies an exception's message into `Description`, so `HealthEndpoints` must never write `Description` or `Exception`. Those reach the framework's log event instead, which is where migration names and connection errors belong. Version and migration IDs are for the system health page (#49).
- **`DatabaseHealthCheck` runs one probe at a time, whoever asks.** The endpoint is public, and it shares a connection pool with cycles and the log sink, so a burst of callers must not become a burst of connections. It must stay a singleton, because `AddCheck<T>` otherwise creates an instance per run.
- **Its deadlines don't depend on cancellation.** The probe cancels itself after 3 seconds, which reports how far it got. Each caller stops waiting after 4 seconds, because Npgsql's cancellation can itself wait on a server that has stopped answering. A stuck probe stays in flight, and later callers join it. It ignores the caller's token, so one client leaving doesn't cancel it for the others.
- **`SELECT 1` runs before the migration history is read**, because that read treats a missing database or table as no migrations applied. Pending and unknown migrations are both computed here, because `GetPendingMigrationsAsync` ignores unknown rows.
- **Probes go through the normal pipeline**, so each has an `Http` event and a correlation ID, as FR-LOG-06 and NFR-OBS-02 require. That's one Information event per probe, and during an outage several Errors per probe. Outages are rare and an error per probe is accurate, so this is accepted.

The framework's health events hold the check's name, status and description, and any exception, so they carry nothing from the request and need no [level floor](#masking).

## Jobs

`Platform/Jobs` runs background work inside the process, because CON-08 rules out a job server. `job_runs` holds the history and the schedules, so a restart loses neither.

There are two ways in, and one way through. Every run gets its own correlation ID, a row, an error boundary and a place in the shutdown drain.

- **Work on a schedule:** implement `IRecurringJob` and register it as a singleton in `Add<Module>()`, like a seeder. Each job gets its own loop, so it never overlaps itself and can't hold up another.
- **Any other work:** call `IJobRunner.RunAsync`, which makes one recorded run now. It never throws for a job's failure, so a discarded task is safe. It's for work a recurring job can't express, such as a run for one credential.

```csharp
internal sealed class CatalogSyncJob(ICatalogSync sync) : IRecurringJob
{
    public string Name => "catalog-sync";

    public JobSchedule Schedule { get; } = JobSchedule.Every(TimeSpan.FromHours(12));

    public TimeSpan Timeout { get; } = TimeSpan.FromMinutes(30);

    public async Task RunAsync(JobContext context, CancellationToken cancellationToken) =>
        context.Details = $"{await sync.RunAsync(cancellationToken)} models";
}
```

A job can inject `IDbContextFactory<EcnanibDbContext>`, like anything else. It's a singleton, so it can't inject a scoped service.

### The two signals

**A run has two signals, so that the token it naturally passes along is the safe one.** With a single token, shutdown would cancel an exchange call as soon as it began, which NFR-REL-04 forbids.

| Signal | Fires | Use |
|---|---|---|
| `cancellationToken` | At the run's timeout, or when the drain window ends | Pass it anywhere, including to exchange calls |
| `context.Stopping` | When shutdown begins, and whenever `cancellationToken` fires | Check it between steps, and start nothing new once it has fired. Pass it only to work that's safe to abandon, such as an AI call or a purge batch. Never to an exchange call |

**Returning means the run finished,** so a run that stops early must throw. `context.Stopping.ThrowIfCancellationRequested()` records it as `Interrupted`.

### Shutdown

The clock starts at `ApplicationStopping`, which SIGTERM fires. The numbers come from `JobRunner.ShutdownTimeout`, which `AddPlatform` gives the host in place of its 30-second default. The drain window is two-thirds of it.

| Time | What happens |
|---|---|
| T+0 | `Stopping` fires. Loops stop, and `RunAsync` returns `NotStarted`. The web server begins to stop |
| T+0 to T+30 | Runs finish, and each records its outcome |
| T+30 | `cancellationToken` fires for anything still running, which ends as `Interrupted` |
| T+33 | A run that hasn't responded is recorded anyway, with an Error, and its task is left running |
| T+45 | `StopAsync` logs an Error naming any runs still going, and returns |

- **The clock doesn't start in `StopAsync`.** Services stop one at a time, and the web server goes first with the same token, so it could spend all of it. For the same reason, `StopAsync` ignores the token it's given.
- **The host doesn't enforce its own timeout.** It awaits each service, so an unbounded wait here would hold the process until a supervisor killed it.
- **`StopAsync` never throws.** The host rethrows from `app.Run()`, so a throw would turn a clean stop into a non-zero exit.
- **`StopAsync` returns one shared task.** A test host calls it twice at once, and a second caller returning early would dispose the services mid-drain.
- **The final write uses no job token.** It would probably have fired, and then the record of a finished run would be lost. Each attempt has its own limit, enforced with `WaitAsync`, because Npgsql's cancellation can wait on a silent server. It keeps trying until the time runs out.
- **Only one thing is left to a crash.** A SIGKILL or a power cut behaves the same as a run that outlives T+45, and the next start closes the row.

**The runner supplies the time and the signals, but it can't finish NFR-REL-04 by itself.** "No trade left half-recorded" also needs the write order in Execution (#34): see the [constraints](#constraints-for-later-issues).

### Schedules

- **`job_runs` is the schedule's memory.** A loop reads its job's latest `started_at` once, when it starts, and tracks the rest in memory. A timer would never fire if the process restarted more often than its interval.
- **Any start counts, whatever its outcome.** A failed job waits for its next tick, so one that fails instantly can't spin. An interrupted job waits too, so one that kills the process can't crash-loop it. An interrupted daily job therefore waits a day. A job that can't accept that retries within itself.
- **Missed ticks collapse into one run.** There's no backfill.
- **Nothing runs before `ApplicationStarted`,** so a failed start runs nothing, and any hosted service's `StartAsync` finishes before the first scheduled run. `RunAsync` works by then.
- **A candle schedule belongs to #30.** `JobSchedule` is abstract so that `Modules/Scheduler` can derive one.
- **Queued work isn't durable.** There's no queue. Anything `RunAsync` would have done is recovered by a schedule, as [Inversions](#inversions) requires of events.

`Running` rows at start belong to a process that's gone, because there's one instance (ASM-06). `StartAsync` marks them `Interrupted` before anything runs, and a failure there stops startup. Their `finished_at` stays null, because a sweep time would overstate how long they ran. Running several instances would need a rethink.

### Failures

| Failure | What contains it |
|---|---|
| A job throws | The run's boundary. It's `Failed`, with an Error, and the loop carries on |
| A job hangs | Its own loop. It stalls only itself, and the timeout gives `TimedOut` |
| A job ignores its token or blocks a thread | Each run is on its own task. 3 seconds after its token fires the outcome is recorded with an Error, so a stall is never silent. The job keeps its place until its task ends, because two concurrent reconcilers are worse than a stalled one (NFR-REL-03) |
| The database is down when a run starts | The work isn't invoked, because a run nobody recorded must not trade. It's `NotStarted`, and a loop retries within 30 seconds |
| The database is down when a run ends | The write retries until it lands, and the job keeps its place meanwhile. An unclosed row would otherwise read as interrupted for good |

- **An `OperationCanceledException` only counts as cancellation if one of the runner's tokens fired,** so a job's own `HttpClient` timeout is `Failed`.
- **The runner isolates runs, not agents.** A failure in one agent reaches only the others in the same run if the job lets it. NFR-REL-05 needs per-item handling in anything that loops over agents or credentials, or one `RunAsync` per item.
- **`details` never holds exception text.** This table has no `SanitizingSink`, and #49 will show it to administrators. Scrubbing isn't enough: it can't recognise an unmarked secret, and a provider's error can echo a prompt, which FR-ADM-01 keeps from them. A failure stores the exception's type name, and the text goes to the log under the same correlation ID. `context.Details` goes through `SecretRules.Scrub`. Keep it to counts and IDs, because account deletion doesn't reach this table.

## Web security

`Endpoints/Security` holds the HTTP edge: NFR-SEC-01, NFR-SEC-03, NFR-SEC-06 and NFR-SEC-07. Nothing in it needs `Platform`, and it sits in the Web UI unit because `App.razor` needs the nonce and endpoints need the rate limit policy names.

### Pipeline order

Anything outside the exception handler runs once per request, so it has to survive the handler clearing the response. Anything inside runs again when the handler or status pages re-execute, so it has to be safe to repeat. Most of the order follows from that, so **a change here needs the tests in `tests/Ecnanib.Tests/Security`**.

| Step | Why it's here |
|---|---|
| `UseCorrelation`, `UseRequestLogging` | [First](#correlation-ids), so they see everything. Neither reads the scheme or address |
| `UseForwardedHeaders` | Before anything reads the scheme or address. Outside re-execute, because it consumes the headers it reads |
| `SecurityHeadersMiddleware` | Outside re-execute, so a request has one nonce. Its headers are assigned in `OnStarting`, because the handler clears headers and `OnStarting` survives that |
| `UseCookiePolicy` | Before anything can set a cookie |
| `UseWebSockets` | Checks `Origin` on a WebSocket before it reaches the hub |
| `UseExceptionHandler` | Doesn't run in Development |
| `UseHsts` | Inside the handler on purpose. It sets the header directly, so outside it the error page would lose the header |
| `UseAuthentication` (#9) | Goes here. The limiter needs the account, and the handler has to catch a failed session lookup |
| `UseRateLimiter` | Outside status pages, so a 404 costs one permit, and a 429 with no body doesn't render the 404 page |
| `UseStatusCodePagesWithReExecute` | |
| HTTPS redirect, except `/health` | |
| `UseAuthorization` (#9), `UseAntiforgery` | Inside status pages, so a 401 or 403 renders one. Antiforgery tokens bind to the identity, so it goes after authentication |

### HTTPS

`App:PublicBaseUrl` is required outside Development and must be `https`. It sets the HTTPS port and the origin below, and nothing else reads the scheme. Without a port, `UseHttpsRedirection` quietly serves plain HTTP and logs one warning.

TLS can end at a reverse proxy (NFR-SEC-01). `X-Forwarded-For` and `X-Forwarded-Proto` are honoured from loopback and from `Security:TrustedProxies`, and ignored from anyone else, because a client can send either. `X-Forwarded-Host` isn't honoured at all. `ForwardLimit` is null, so a chain of proxies is walked back to the first address that isn't trusted.

- **A proxy that isn't trusted fails loudly.** The application sees plain HTTP and redirects, so the browser gets a redirect loop rather than silently losing HSTS and sharing one rate limit among all users.
- **Health is the one exception to the redirect.** Behind a proxy, a probe reaches the application directly, and a 307 would count as success under `curl -f` without running the check. It answers fixed values only.
- **HSTS has no `includeSubDomains`,** which could lock an operator's unrelated sites to HTTPS for a year. There's no preload either: that's the operator's decision.

### Headers

`SecurityHeadersMiddleware` sets these on every response that reaches it, including static assets, 404, 429, redirects and the error page:

```text
Content-Security-Policy: default-src 'self'; script-src 'self' 'nonce-{n}'; style-src 'self'; img-src 'self' data:;
                         connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'
X-Frame-Options: DENY
X-Content-Type-Options: nosniff
Referrer-Policy: same-origin
```

- **`script-src`:** `'self'` covers `blazor.web.js` and the reconnect module. The nonce is for the inline import map `<ImportMap />` renders. An inline script of your own needs `nonce="@SecurityHeadersMiddleware.NonceOf(HttpContext)"`, which only works in `App.razor`, because an interactive component has no `HttpContext`. `strict-dynamic` isn't an option: it turns off `'self'`.
- **`connect-src 'self'`** covers the SignalR WebSocket: under CSP 3 `'self'` matches `wss:` on the same origin. Development adds `ws://localhost:*` and `wss://localhost:*`, for `dotnet watch`.
- **`style-src 'self'`** blocks `<style>` blocks and `style=` attributes. CSS isolation and `.style.x = ...` from a script still work. See [the constraint](#constraints-for-later-issues) for `Virtualize`.
- **`img-src data:`** is for the image in `app.css`. `base-uri 'self'` rather than `'none'`, because `App.razor` has a `<base>`.
- **Headers are assigned, not appended.** That replaces the `frame-ancestors 'self'` policy interactive rendering adds, and the `X-Frame-Options: SAMEORIGIN` antiforgery adds. A browser enforces every policy it receives, so two would be harmless but misleading.
- **`same-origin`** stops a `token=` link leaking to another site. `no-referrer` would also do that, but it makes a browser send `Origin: null` on a form POST.
- **The nonce is in `HttpContext.Items`, not a scoped service,** because the error and status pages run in a new scope.

The Development exception page has inline styles, so this policy strips its styling. It's still readable, and this is accepted.

The tests stop at "the HTML doesn't contradict the policy", because a browser enforces it. The WebSocket and the import map were checked by hand in Chromium and WebKit, in Development and in a published build over TLS, and an injected inline script was blocked in each. The reconnect modal was checked in the published build. A change to the policy, or to how a page loads scripts, needs that check again until there's a browser test.

### Cookies

`UseCookiePolicy` applies to every cookie, so a later issue can't forget: HttpOnly, at least SameSite=Lax, and Secure outside Development (NFR-SEC-06). Lax is the minimum rather than Strict so that following an email link keeps a session. Antiforgery keeps its own Strict.

**Don't set `Cookie.SecurePolicy = Always` on antiforgery.** It throws on any request that isn't HTTPS, and on a page load that happens inside a callback the framework catches: the page answers 200 and only an Error in the log shows it. The cookie policy adds `Secure` without reading the scheme.

### Rate limiting

The limiter is global, so a new endpoint is covered without doing anything (NFR-SEC-07). It's in memory, because there's one instance (ASM-06, CON-08), so a restart clears it. A fixed one-minute window can let through twice the limit across its edge, which is accepted.

| Traffic | Limit |
|---|---|
| Anything else, including pages and 404s | `RequestsPerMinute`, per IP address and per account. They're chained, so signing in can't escape the limit per address |
| Endpoints with `.RequireRateLimiting(RateLimits.Authentication)` | `AuthenticationRequestsPerMinute` per IP address, on top of the above |
| `/_blazor/negotiate` and any WebSocket upgrade | `RequestsPerMinute`. SignalR accepts a WebSocket that skips negotiation, so counting only negotiation would leave a way around it |
| Other hub requests | None. Long polling makes one per message on a connection that's already admitted, so a limit would end live sessions |
| The error page | None. The request was counted already, and a limit here would replace the page with an empty 429 |
| `/health/*` | None. A limit must not fail a probe, and `DatabaseHealthCheck` runs one probe at a time anyway |
| Static assets | None. One page load fetches several, and they cost almost nothing |

- **A page load costs about 4 permits,** so 300 is 75 page loads a minute from one address, which leaves room for an office behind one IP. Navigating inside a circuit costs nothing.
- **An address is grouped by what one subscriber holds.** IPv6 is cut to /64, or a subscriber could rotate through endless buckets, and IPv4-mapped addresses become IPv4. A null address (a Unix socket) shares one bucket.
- **An account is `ClaimTypes.NameIdentifier`.** A signed-in user without it throws rather than falling back, because a claim that didn't match would otherwise leave every account limited only by address, and nothing would show it.
- **A rejection is a 429 with `Retry-After` and no log line of its own.** The `Http` event records it at Information. The default of 503 would log an Error per request, and a Warning would still turn a flood into database rows.
- **It limits rate, not concurrency.** It doesn't cap how many circuits one address holds open.

The sign-in account is in the request body, and a partitioner can't read that. The account limit on sign-in is therefore FR-AUTH-04's lockout (#9), which also survives a restart.

### Circuits

Negotiating and connecting are HTTP, so they're limited like anything else. After that, **hub traffic never reaches the limiter**, and that's fine for NFR-SEC-07, which covers authentication and API endpoints, because both stay on HTTP: only HTTP can set a cookie, and [the API](#the-api) is `Endpoints/`.

The gap is a secret checked on a circuit, such as a current password (#10) or a TOTP code for a sensitive action (#14). Nothing limits those attempts, so each needs a per-account throttle behind its module contract, and a refusal is a typed result rather than a 429. Don't limit inside `CircuitHandler`: inbound activity includes render acknowledgements, and refusing one breaks the circuit.

The WebSocket `Origin` is checked against `App:PublicBaseUrl`, because CORS doesn't cover WebSockets and SameSite doesn't stop a sibling subdomain. The check is skipped when a client sends no `Origin`: only a browser can carry someone else's cookies, and a browser always sends one.

### OWASP

Most of what NFR-SEC-03 protects doesn't exist yet, so this is what each risk relies on, and who finishes it. `BannedApis` scans compiled code, including Razor, lambdas and async methods, and `BannedApiGuardTests` proves it against broken fixtures.

| Risk | What covers it | Test or owner |
|---|---|---|
| Injection | EF Core parameterises. Raw SQL APIs are banned, and the interpolated forms parameterise | `BannedApis` |
| Cross-site scripting | Razor encodes. `MarkupString` is banned, because AI output and user input reach the UI. The CSP has no `unsafe-inline` or `unsafe-eval` | `BannedApis`, `SecurityHeaderTests` |
| Cross-site request forgery | Antiforgery, SameSite and `form-action 'self'`. Every state-changing endpoint must require a token, and `DisableAntiforgery` is banned. The WebSocket `Origin` check covers the hub | `CsrfTests`, `BannedApis`, `WebSocketTests` |
| Server-side request forgery | Only the adapters may use `HttpClient`, and base URLs come only from the configuration file (NFR-SEC-10) | `BannedApis`, then #17, #18 |
| Broken access control | Ownership checks behind module contracts, which HTTP can't enforce on a circuit | #9, #55 |
| Authentication failures | Lockout, password hashing and sessions | #9, #10, #14 |
| Cryptographic failures | HTTPS, HSTS, Secure cookies. Secrets at rest are #12 | `HttpsTests`, `CookieTests`, #12 |
| Security misconfiguration | Startup refuses a missing or `http` public address, and validates these settings. `AllowedHosts` is the operator's to set | `WebSecuritySettingsTests`, #57 |
| Vulnerable components | The dependency scan (NFR-SEC-09) | CI |
| Logging failures | [Logging](#logging) and masking, and the audit trail | `CanaryScanTests`, #11 |

`DatabaseHealthCheck` uses the interpolated form so that the ban has no exceptions.

## Constraints for later issues

- **The UI (#8, #15, #41, #42, #52, #53):** no inline scripts, `<style>` blocks, `style=` attributes or `onclick=` handlers: the [CSP](#headers) blocks them all, and the failure is a console message and a silently broken page. Use classes, isolated CSS and script files. A theme applied before first paint (FR-PREF-03) can be an attribute that `App.razor` renders on `<html>`, plus a media query for System, with no script at all. `Virtualize` renders `style=` attributes, so the first change that uses it adds `style-src-attr 'unsafe-inline'`, which is narrower than loosening `style-src`.
- **Authentication (#9):** call `UseAuthentication` and `UseAuthorization` explicitly, after `UseCorrelation`, in the slots in [Pipeline order](#pipeline-order). Otherwise `WebApplication` inserts them before user middleware, and their events have no correlation ID, and the limiter can't see the account. Use `ClaimTypes.NameIdentifier`. Put `.RequireRateLimiting(RateLimits.Authentication)` on the sign-in and registration endpoints. Post them as forms, or `CsrfTests` fails. Set a fallback authorization policy, so a new page is private by default. Keep `RateLimitTests` honest with one test through the real sign-in: its account tests use a stand-in principal.
- **Sessions and two-factor (#10, #14):** a password or TOTP code checked on a circuit needs its own [per-account throttle](#circuits). The HTTP limiter can't see it.
- **Audit (#11):** must sit below every module that writes to it, and take the caller's context. See [Persistence](#persistence). Store `ICorrelationContext.CurrentId`, which is null outside a request, circuit action, cycle or job. `RemoteIpAddress` is the client's address once `UseForwardedHeaders` has run, but a circuit has no request to read it from, so FR-AUD-02 needs it captured when the circuit starts.
- **Sign-in and email links (#9, #13):** put tokens in the query string as `token=`, never in the path, so the text rules can see them. Set `LogProperties.UserId` in a scope. Build links from `App:PublicBaseUrl`, never from `Host`, which a client chooses. Throttle reset requests per address too, because the limiter only sees the sender's IP.
- **Secrets (#12):** must sit below Identity and Credentials, and keep secret access separate from credential metadata (#20). Wrap decrypted secrets in a type that doesn't render its value: see [Masking](#masking). Antiforgery and session cookies are protected by Data Protection keys, which sit unencrypted in the user's profile by default: decide where they live.
- **Exchange adapter (#17):** separate read and trade surfaces, so only Execution can place orders (AC-01), and share one request-weight budget (EXT-BIN-04, FR-EXE-09). Log FR-LOG-08 events under the `Exchange` category. Order-placing calls must enforce their own timeout of about 10 seconds and stop honouring a caller's token once the send has begun, or they won't fit the [drain window](#shutdown): `HttpClient`'s default is 100 seconds. Base URLs come from the configuration file only (NFR-SEC-10), and clients don't follow redirects, or a response could send a signed request somewhere else.
- **AI adapters (#18):** no `AiProvider` enum. Providers are data, and an enum would break NFR-MNT-02. Log FR-LOG-07 events under the `AiCouncil` category. Base URLs and redirects follow the same rule as the exchange adapter.
- **The first real job:** a job with no history is due at once, so every application start in every test will run it. `AppFactory` will need to remove real jobs by default.
- **Catalog sync (#21):** use a separate job name for on-demand runs, so they don't move the schedule, and guard against one overlapping the scheduled run.
- **Risk Engine (#27):** see rule 4.
- **Decision cycles (#30, #31):** call `ICorrelationContext.Begin()` around each cycle, and set `LogProperties.AgentId`, `CycleId` and `Environment` in a scope. Decide between one `job_runs` row per tick and one per cycle, which costs about 19,000 rows a day at 200 agents on 15m. `RunAsync` stays pending while hung work keeps its place, so a concurrency limit must bound its wait.
- **Order execution (#34):** pass `cancellationToken`, not `Stopping`, to exchange calls. Persist the intent before sending, or a cut send can't be recovered (FR-EXE-05, FR-EXE-08). Once an order is placed, save with a deadline of its own, or the end of the drain window could cancel the write.
- **Reconciliation (#35):** run the startup pass from a hosted service's `StartAsync`, through `IJobRunner`, so it finishes before any scheduled cycle (FR-EXE-08).
- **Retention (#50):** purge `log_events` by `timestamp`, using `Retention:DatabaseLogDays`, which FR-LOG-02 and FR-DATA-01 require. `job_runs` needs purging too, and the SRS sets no period. Keep each job's newest row, or it runs early once.
- **Deployment (#57):** a supervisor's stop timeout must be at least 60 seconds: 45 for the host, plus the final log flush. systemd's default of 90 is fine, and Docker's 10 and Kubernetes' 30 are too short. A SIGKILL behaves like a crash. A reverse proxy must send `X-Forwarded-For` and `X-Forwarded-Proto`, pass WebSocket upgrades, and be in `Security:TrustedProxies` unless it's on loopback. Set `AllowedHosts` to the public host, plus whatever host a local probe uses.

Other interfaces are defined by the issue that needs them. So far there's `ISystemInfo` in `Modules/Administration/Contracts`, which is part of FR-ADM-04 for #49 to build on, and the persistence, logging, correlation and job contracts in `Platform/Contracts`.

## What the boundary tests can't see

The tests scan compiled code, including method bodies, lambdas, async methods, iterators, local functions, generics, attributes and catch clauses. Razor components compile to the same code, so they're covered too. `ModuleBoundaryGuardTests` proves each of these against deliberately broken fixtures.

These leave no reference to scan, so **check them in review**:

- Constants and enum values from another module, which the compiler inlines
- `nameof` on another module's type
- A forbidden type used only as the generic argument of a static field access, such as `Holder<Forbidden>.Value`
- Reflection, or calling something by name in a string
- SQL against another module's tables
