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
5. `Kernel` holds only vocabulary that the SRS fixes, that at least two modules use, and that has no behaviour, such as `AuditAction`, `Market`, `Timeframe` and typed IDs. Moving a type there to escape a cycle is the wrong fix: use a port or event instead. `SecretValue` is the one exception. Its only behaviour is not showing its value, and it's here so the adapters, which can't use Platform, can carry a secret to where it's used. See [Secrets](#secrets).
6. Razor components stay in `Components/`, because pages such as agent detail span several modules (FR-UI-01).

The Web UI may use neither adapter nor `Platform`, so it can't reach secrets or place orders directly (2.4, FR-ADM-01, FR-KEY-09). Administration may use `Platform`, so `BannedApis` keeps `ISecretStore` to Identity and Credentials. `Endpoints/` counts as Web UI, so it reaches `Platform` only through framework abstractions, as the health endpoints do with the checks `Platform` registers. That's also why the [HTTP security code](#web-security) lives in `Endpoints/Security`: pages need its nonce and endpoints need its policy names.

### Adding a dependency

Use the other module's `Contracts` in your code. If the boundary tests fail, add the edge to `ModuleMap.cs` in the same change, and say which requirement needs it. If that creates a cycle, use an inversion instead.

### Adding a module

Create `Modules/<Module>/<Module>Module.cs`, add the module to `ModuleMap.cs`, and call `Add<Module>()` from `Program.cs`. The tests fail if you skip any of these steps.

## Inversions

Some needs run in both directions, and a reference can't solve that. Separating contracts from implementations would only move the cycle from compile time to run time, so each pair needs a fixed direction. The lower module declares a port or in-process event in its `Contracts`, and the higher module implements or subscribes to it. Events run in process, with no broker (CON-08).

| Pair | Need | Direction |
|---|---|---|
| Agents, Execution | Stop, kill switch, open-exposure checks (FR-AGT-03, FR-AGT-07, FR-AGT-08, FR-AGT-13, EXT-BIN-07) | Port declared by Agents, `IAgentExposure`, implemented by Execution |
| Credentials, Agents | Pause agents on credential failure or deletion (FR-KEY-10, FR-KEY-11) | Event from Credentials, `ICredentialObserver`. See [Pausing](#pausing-on-a-credential) |
| Credentials, Catalog | Sync when a credential is added or rotated (FR-CAT-03) | Event from Credentials, `ICredentialObserver`. See [Triggers](#triggers) |
| Catalog, Agents | Model deprecation and retirement (FR-CAT-04) | Event from Catalog, `IModelStatusObserver`. See [Acting on retired models](#acting-on-retired-models) |
| Agents, its history's owners | Deleting an agent with its cycles, trades and reflections | Port declared by Agents, `IAgentHistory`, implemented by DecisionCycle, and later Execution and Experience |
| DecisionCycle, Experience | Attribution, metrics and reflections (FR-EXP-01, FR-EXP-02, FR-EXP-04) | Event from DecisionCycle, plus a reflection port it declares |
| Execution, Experience | Review after a trade closes (FR-EXP-04) | Trade-closed event from Execution |
| AiCouncil, Experience | Reflections in prompts (FR-PRM-01, FR-EXP-04) | DecisionCycle passes them in as `PromptReflection`s in the runtime context. See [Prompts](#prompts) |
| Agents, RiskEngine | Auto-pause on loss limits (FR-RISK-08) | The engine returns a `LossLimitBreach`, and its caller, the decision cycle, passes it to `IAgents.PauseForLossLimitAsync`. See [Loss limits](#loss-limits) |
| Identity, data owners | Account deletion and export (FR-AUTH-09, FR-DATA-03) | Port declared by Identity, implemented by each owner |
| DecisionCycle, Administration | Emergency pause (FR-ADM-05) | DecisionCycle owns the state; Administration only commands it |
| Modules, Web UI | Live updates (FR-UI-05, FR-ROLE-08) | Events that the Web UI subscribes to |

In-process events are lost on a crash, so each one needs a recovery path, such as periodic revalidation or reconciliation.

## The API

"API" in SRS 2.2 is not a separate module. Pages run on Blazor Server and reach services directly, and the SRS defines no public HTTP API or consumer of one. Module `Contracts` are the application's API, and `Endpoints/` holds the HTTP that really is needed, such as sign-out, health (NFR-OBS-01) and exports (FR-DATA-03). Forms that take a secret are static pages, which are HTTP too: see [Forms that take a secret](#forms-that-take-a-secret).

**After page load, a Blazor circuit doesn't pass through HTTP middleware.** Access rules therefore can't live there: ownership checks (NFR-SEC-04) belong behind module contracts. [Correlation IDs](#correlation-ids), [rate limiting](#circuits) and the [audit trail's client details](#where-the-client-comes-from) each have a plan for circuit traffic. Request logging doesn't: FR-LOG-06 covers HTTP, so a circuit logs one `/_blazor` request, and activity inside it is logged where it happens, behind module contracts.

## Persistence

`EcnanibDbContext` in `Platform/Contracts` is the one context, with one linear migration chain, because the SRS has one database and one migration version (FR-ADM-04). The code-first model owns the schema, so nobody changes it by hand (DATA-R-05).

- **Modules own their entities.** A module keeps its entities and their `IEntityTypeConfiguration` internal, and reaches them with `Set<T>()`, so the boundary tests cover them. The context finds configurations with `ApplyConfigurationsFromAssembly`. This is the one exception to "no module scanning" in rule 3: contributing the model through DI would leave `dotnet ef` with an empty model at design time, and it would generate drops.
- **Reference another module's rows by ID only**, with no navigations. Whether a foreign key constraint can cross modules isn't decided: `HasOne<OtherModuleEntity>()` fails the boundary tests, so the first issue that needs one decides.
- **Contexts are short-lived.** A Blazor circuit keeps its scope for as long as the user stays connected, so the context isn't in DI, and a service that injects it fails at startup. Inject `IDbContextFactory<EcnanibDbContext>`, and dispose each context when the operation ends.
- **The caller owns the context, which keeps writes atomic.** Anything that must commit with a caller's change, such as an [audit record](#audit), takes the caller's context, adds to it and doesn't save. One `SaveChangesAsync` then commits both (FR-AGT-04, NFR-REL-04). Across several saves, use one transaction on that context. Never hold a transaction open across an exchange call. Leave `EnableRetryOnFailure` off: it rejects transactions started this way.
- **Money and time follow SRS 6.2.** Store amounts as `decimal` next to their asset or currency code, and timestamps as `DateTime` or `DateTimeOffset` in UTC. `SchemaRulesTests` rejects binary floating point and timestamps without a time zone on any mapped column. It can't check that an amount has a code column, so check that in review.
- **Seeders add reference data (NFR-DEP-02).** Implement `IReferenceDataSeeder` and register it in `Add<Module>()`. Seeders run in registration order on every start, so they add only what's missing, and they don't save.

### Startup

`DatabaseStartup` is a hosted service that works in `StartingAsync`, which the host runs before any hosted service's `StartAsync`. So the database is ready for every one of them, whatever order they were registered in, including the framework's [Data Protection](#data-protection-keys) service, and before the web server opens a port. It applies pending migrations, then runs the seeders. Any failure is logged and stops the process with a non-zero exit code (NFR-DEP-03). There's no "starting" state to report, so a health probe during a long migration gets connection refused rather than a 503.

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

A category is a logger name, and an override matches it by dotted prefix, so `Microsoft.AspNetCore` covers everything under it. SRS 7.3 names three categories that aren't code namespaces. Create those loggers by name with `ILoggerFactory.CreateLogger`: `Http`, `Exchange` and `AiCouncil`.

### Masking

`SanitizingSink` rebuilds each event after enrichment, so it also sees scopes. It masks by property name at any depth and scrubs all text, including the message template and the exception. If it throws, the event is dropped, so it fails closed. `SecretRules` holds the rules, and `CanaryScanTests` is the automated scan AC-03 asks for, which every new rule should extend.

Text is matched by name (`password=`), by shape (`Bearer`, `sk-`, 64 alphanumerics, JWTs) and by exact value. Exact values come from the configuration: any setting in a section whose full key contains a sensitive word, and the password in each connection string. A new secret setting is picked up without code, as long as its key says what it is.

**What it can't do.** A secret with no marker, under a generic name, that isn't in the configuration can't be recognised. `{Value}` holding a decrypted user key is the case that matters, which is why a decrypted secret is a [`SecretValue`](#using-a-secret), which never renders its value. The same goes for an `HttpRequestMessage` logged without `@`: it renders through `ToString()`, and a header with an unfamiliar name stays readable. With `@`, `SensitiveTypesPolicy` never takes the request apart.

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
| `Microsoft.AspNetCore.Http.RequestDelegateFactory` | Information | Logs a raw form or query value at Debug when an endpoint can't bind it |

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
| Cycle | `JobRunner`, because each cycle is a run of its own. See [Scheduler](#scheduler) |

- **IDs are always generated here.** An inbound `X-Correlation-ID` is ignored: a client-chosen value could forge links between audit records, and a long one could fail a batch of log rows on the index. `TraceId` isn't used either, because a `traceparent` header sets it.
- **`UseCorrelation` comes first in the pipeline.** Request logging needs the ID, and the exception handler and status pages run everything after them again, which would give a request two IDs.
- **The header is set in `Response.OnStarting`**, because the exception handler clears headers, and a failure is when the ID matters most. The callback uses a captured ID, because a bodyless response can start after the scope has ended.
- **A circuit gets one ID per action, not per connection.** Without the handler, circuit logs would still carry an ID, but it would be the `/_blazor` request's, shared by everything until the user disconnects. An action links to its own logs and audit records, and nothing links actions to each other.
- **The adapters don't need it.** They can't reference `Platform`, and they inherit the scope, so events under the `Exchange` and `AiCouncil` categories are linked already. An `AiCall` row links through its cycle.
- **The ID doesn't cross a queue.** It follows the async call, so work picked up from a queue or timer must call `Begin()` itself. `Begin()` always creates a new ID for the same reason: a timer started during a request captures that request's ID.

`ClientOriginTests` proves end to end that each action on a circuit gets its own ID, through `CircuitClient` and a test `[JSInvokable]`. **The framework only attaches a circuit's handlers on its first `UpdateRootComponents`**, because in a Blazor Web App the root components arrive after the circuit starts. A browser always sends one, but a test client has to as well, or no handler sees anything: `CircuitClient.OpenCircuitAsync` does that.

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
internal sealed class CatalogSyncJob(CatalogSync sync, CatalogSettings settings) : IRecurringJob
{
    public string Name => "catalog-sync";

    public JobSchedule Schedule { get; } = JobSchedule.Every(settings.SyncInterval);

    public TimeSpan Timeout => CatalogSync.FullTimeout;

    public Task RunAsync(JobContext context, CancellationToken cancellationToken) =>
        sync.SyncEveryoneAsync(CatalogSyncTrigger.Schedule, context, cancellationToken);
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
- **A module can derive its own `JobSchedule`.** `CandleSchedule` in `Modules/Scheduler` does, for candle closes. See [Scheduler](#scheduler).
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

## Audit

`Platform/Audit` holds the audit trail: who did what, from where (FR-AUD-01, FR-AUD-02). `audit_events` is separate from `log_events`, has no `SanitizingSink`, and is append-only.

### Writing a record

The module that owns an action records it. **`IAuditTrail.Add` adds to the caller's context and doesn't save**, so the caller's save commits the record with the change it describes. Across several statements, put both in one transaction, so that neither can exist without the other (FR-AGT-04, NFR-REL-04):

```csharp
await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

await context.Set<Session>().Where(session => session.Id == sessionId).ExecuteDeleteAsync(cancellationToken);

audit.Add(context, AuditEntry.ByUser(AuditAction.SessionRevoked, userId) with { TargetType = "Session", TargetId = sessionId.ToString() });
await context.SaveChangesAsync(cancellationToken);
await transaction.CommitAsync(cancellationToken);
```

- **Actions are `AuditAction` values in `Kernel`.** The SRS fixes the list, Platform stores them, and the Web UI gives each one its text, which it couldn't do through Platform. An issue that adds an action adds its text in `ActivityText` too, and `ActivityTests` fails until it does. Records store the name, so never rename a value.
- **`UserId` is the account a record is about**, which decides whose history shows it. An administrator acting on someone sets that person. It's null for a platform-wide action.
- **The actor is a `User`, an `Administrator`, the `System` or a `Visitor`.** FR-AUD-02 names the first three. A failed sign-in proves nothing about who sent it, so it's a visitor's, against the account it named.
- **The trail stamps the rest:** the time, `ICorrelationContext.CurrentId`, and the [client's](#where-the-client-comes-from) address and user agent.
- **`Changes` is the before/after summary,** as field names and values. A field whose name looks like a secret's has its values masked, and every other value goes through `SecretRules.Scrub`. That's a backstop with the same [limits](#masking) as the logs' masking, so never put a secret there.

### Where the client comes from

`ClientOriginContext` holds the address and user agent, and flows with the async call like the correlation ID. Both are stored as `ClientText` writes them, as sessions are, so the two always agree.

| Work | What sets it |
|---|---|
| HTTP request | `UseClientOrigin`, straight after `UseForwardedHeaders`, so the address is the client's |
| Action on a circuit | `ClientOriginHubFilter`, from the hub connection's request, around every hub call. The hub call starts the circuit's work, which carries it along |
| Job run | Nothing: `JobRunner` clears it, because a run acts for nobody, even when a request started it |

- **A circuit's address is its connection's.** That's the WebSocket's request, or long polling's first one. It can differ from the address the page loaded from, or the one the session signed in from.
- **SignalR runs a connection's calls in the execution context of the request that opened it,** so the middleware's value would reach them without the filter. Nothing documents that, though, and `GetHttpContext` is the API for it, so the filter doesn't rely on it.
- **With no peer address, as behind a Unix socket or on the test host, `X-Forwarded-For` is trusted.** That's the framework's rule: no address means a local proxy.

### Append-only

- **The contract only adds and reads,** and the entity is internal to `Platform/Audit`, so the boundary tests stop any other unit reaching it through EF.
- **The table itself refuses `UPDATE` and `TRUNCATE`,** through a trigger the `AddAuditEvents` migration installs. The boundary tests can't see raw SQL, which Identity uses a lot. The trigger fires per statement, so even an update that matches nothing is refused.
- **`DELETE` stays allowed,** for retention (#50).
- **There's no foreign key to `users`,** because records outlive the account (FR-AUTH-09).

### What's recorded so far

| Action | Where | How |
|---|---|---|
| `SignedIn` | `Accounts.SignInAsync`, or `Accounts.CompleteSignInAsync` with two-factor on | In the transaction that inserts the session, which is its target. A right password alone records nothing when a code must follow |
| `SignInFailed` | `Accounts.SignInAsync`, `Accounts.CompleteSignInAsync` | By a visitor. Saved on its own once the outcome is known, because the attempt was charged before the hash ran. A wrong two-factor code is one too. The reason is in the log, under the same correlation ID. An address with no account, or a malformed one, is recorded with no user and without the address, which could be a password typed into the wrong field |
| `SignedOut`, `SessionRevoked` | `Sessions` | The row goes and the record arrives in one transaction, then the connections close. The session is the target |
| `PasswordChanged` | `Accounts.ChangePasswordAsync` | In the transaction that changes the hash and ends the other sessions. A refused change isn't recorded, though its guess counts towards the lockout |
| `AdministratorConfirmed`, `AdministratorConfirmationFailed` | `Administrators.ConfirmAsync` | The first in the transaction that verifies the address |
| `MasterKeyRotated` | `SecretStartup` | By the system, with no user, in the transaction that records the new active key. `Changes` holds the old and new key IDs. See [Rotating the master key](#rotating-the-master-key) |
| `EmailVerified` | `EmailVerification.VerifyAsync`, `PasswordResets.ResetAsync` | In the transaction that sets `email_verified_at`, and only when it was null, so an address is verified once |
| `PasswordResetRequested` | `PasswordResets.RequestAsync` | By a visitor, against the account, in the transaction that issues the link. A throttled request and an unknown address record nothing |
| `PasswordReset` | `PasswordResets.ResetAsync` | In the transaction that uses the link, replaces the hash and ends every session |
| `PasswordResetLinkIssued` | `PasswordResets.IssueAsync` | By an administrator, with the user as `UserId`, in the transaction that issues the link |
| `TwoFactorEnabled` | `TwoFactor.EnableAsync` | In the transaction that confirms the authenticator, issues recovery codes and ends the other sessions. Starting a setup records nothing, because nothing changes until a code confirms it |
| `TwoFactorDisabled` | `TwoFactor.DisableAsync` | In the transaction that deletes the authenticator, its secret and the recovery codes |
| `RecoveryCodesRegenerated` | `TwoFactor.RegenerateRecoveryCodesAsync` | In the transaction that replaces the codes |
| `RecoveryCodeUsed` | `TwoFactor.SpendAsync` | In the transaction that spends the code, at sign-in or in a check for a sensitive action. A refused check isn't recorded, though a wrong authenticator code counts towards that check's throttle |
| `CredentialAdded` | `Credentials.AddAiKeyAsync`, `Credentials.AddBinanceKeyAsync` | In the save that stores the key's secrets and its row, which is the target. `Changes` has the provider, environment, label and status. A refused key records nothing, because nothing was stored |
| `CredentialValidated` | `CredentialChecks.CheckAsync`, on demand and from `credential-revalidation` | In the transaction that writes the result, only when the provider answered. By the user on demand, and by the system on the schedule. `Changes` has the status before and after |
| `CredentialRotated` | `Credentials.RotateAiKeyAsync`, `Credentials.RotateBinanceKeyAsync` | In the transaction that swaps the secrets and deletes the old ones. `Changes` has the key's last four characters and the status, before and after |
| `CredentialDeleted` | `Credentials.DeleteAsync` | In the transaction that deletes the row and its secrets. `Changes` has what the credential was |
| `AgentCreated` | `Agents.CreateAsync`, `Agents.CloneAsync` | In the save that inserts the agent and its roles. `Changes` has every setting but the description and custom instructions, and a clone's `ClonedFrom` |
| `AgentUpdated` | `Agents.UpdateAsync` | In the save that changes the settings, with only the fields that changed, before and after. Not recorded when nothing did |
| `AgentExecutionModeChanged` | `Agents.UpdateAsync` | In the same save, as well as `AgentUpdated`, because FR-AUD-01 names it |
| `AgentActivated`, `AgentPaused`, `AgentStopped`, `AgentErrored`, `AgentArchived` | `Agents`, and `AgentPrerequisites`, `ModelLifecycle` and `Agents.PauseForLossLimitAsync` for a pause by the system, and `Agents.EnterErrorAsync` for Error | In the transaction of the conditional update that changes the status. `Changes` has the status before and after, and the reason. A pause because of a credential, a retired model or a loss limit is by the system, with the owner as `UserId` (FR-AGT-04), and a loss limit's has `PausedUntil` when it has a period. So is the resume when that period ends, `LossLimitEnded` |
| `AgentDeleted` | `Agents.DeleteAsync` | In the transaction that deletes the archived agent. `Changes` has its name, market, environment and pair |
| `CatalogModelEdited` | `CatalogAdministration.SaveModelAsync` | By an administrator, with no user, in the transaction that writes `model_overrides`. The target is the model, as `provider/model` when that fits 128 characters. `Changes` has the provider and model, and each field that changed, before and after, with a null for inherited. Not recorded when nothing changed |
| `RoleDefaultChanged` | `CatalogAdministration.SaveRoleDefaultsAsync` | By an administrator, with no user, one per role that changed, in the transaction that saves them all. The role is the target, and `Changes` has the provider and model, before and after |
| `CatalogSyncRequested` | `CatalogAdministration.SyncAllAsync` | By an administrator, with no user, when the sync starts. Not when one is already running |

**Visitors write `SignInFailed` rows,** one per attempt, whether or not the address has an account. FR-AUD-01 asks for them, and the sign-in [rate limit](#rate-limiting) bounds how fast they arrive.

### A user's history

`/account/activity` shows the records whose `UserId` is the user's, newest first and 50 at a time (FR-AUD-03). The Web UI can't use Platform, so it reads them through `IAccountActivity` in Identity. That leaves out actor IDs and correlation IDs, which are for administrators (FR-ADM-06), so an administrator's action shows only as "By an administrator".

Each record shows its `Changes` as a field's name beside its value, or "from x to y" when it changed. `ActivityText.FieldOf` gives each field its text, and a field without text isn't shown, so an issue that records a summary a user sees gives its fields text there. `CredentialTextTests` checks that every credential field has some. Values are shown as recorded, as text, except a credential's status, environment and last four characters, which have words. `MasterKeyRotated` has `Changes` too, but it's platform-wide, so it's on nobody's page.

"Show older activity" moves focus to the first record it adds, so focus isn't lost when the button goes after the last page.

## Secrets

`Platform/Secrets` encrypts secrets at rest (FR-KEY-07): AI and exchange keys ([Credentials](#credentials)), [TOTP keys](#two-factor), and the [Data Protection keys](#data-protection-keys). Each is one row in `secrets`, and the row that owns it, such as a credential, keeps its ID.

### The master keys

`Security:EncryptionKeys` lists the keys, each with an `Id`, a `Key` of 32 random bytes in base64, and `Active`. Exactly one is active: it encrypts every new secret, and the others only decrypt. Startup is refused, before any migration, when a key is missing, malformed, repeated, not random, or the sample from SRS 7.3 (NFR-SEC-05, CFG-03).

- **Messages name an entry as `Security:EncryptionKeys, entry 0`**, not by its path. These messages reach stderr through the masking rules, which hide whatever follows `EncryptionKeys:`.
- **A key ID is a label.** It's stored with each secret, and logs, audit records and the health page show it, so `SecretRules` doesn't mask it as a configured value. The keys themselves are masked by value.
- **The configuration file must be closed to everyone else.** Outside Development, startup is refused if group or others have any access to a settings file that was loaded, and the message names it. Development is exempt, because a checkout is normally readable by everyone. So is Windows, because ACLs aren't a file mode: #57 covers it.

### Sealing

AES-256-GCM with a random 12-byte nonce. `ciphertext` holds `[version 1][nonce][16-byte tag][ciphertext]`, and `key_id` says which key sealed it (FR-KEY-07). Random nonces are safe far beyond the number of secrets one key will seal.

- **The associated data is the row's ID**, so a ciphertext copied onto another row doesn't decrypt there. Someone who can write to the table still can't move one user's key onto another user's credential.
- **Plaintext is only held in pinned buffers**, which are cleared after use, until it's handed over as a `SecretValue`.
- **A failure names the secret's ID and key ID**, and nothing else.

### Using a secret

Identity and Credentials use `ISecretStore` in `Platform/Contracts`, and nothing else may. `BannedApis` enforces that, because Administration may use Platform, and administrators must never see a decrypted secret (FR-ADM-01).

| Call | What it does |
|---|---|
| `Add` | Seals with the active key and adds to the caller's context without saving, like an [audit record](#writing-a-record), so the secret commits with the row that refers to it |
| `RemoveAsync` | Deletes straight away, so call it inside the caller's transaction |
| `RevealAsync` | Decrypts into a `SecretValue`. Use it, then dispose it. Never cache it (FR-KEY-09) |

**A `SecretValue` never shows its value.** `ToString()` and the debugger show `***`, logging masks it with or without `@`, and serializing it to JSON throws, so it can't reach an export, a decision trace or a notification. `Dispose()` clears it. `Reveal()` returns a string, which stays in memory until it's collected, so prefer `RevealUtf8()` wherever bytes will do.

**An owner deletes its own secrets.** There's no foreign key either way, so nothing else knows when a secret stops being used, including when an account is deleted.

### Rotating the master key

FR-KEY-08, from the operator's side:

1. Add the new key with `Active` set to true, set the old key's `Active` to false, keep both, and restart.
2. Startup records the new key in `master_key_activations` and audits `MasterKeyRotated`, by the system, in one transaction. Restarting with the same keys records nothing more, and the first key isn't a rotation.
3. Once the application has started, a run named `secret-reencryption-on-start` re-encrypts every secret that an older key seals, 100 to a transaction. Older keys keep decrypting meanwhile. The `secret-reencryption` job does the same every hour, in case that run didn't finish, and only one runs at a time.
4. When the log says every secret uses the new key, or `IMasterKeyRotation` says `Complete`, remove the old key and restart.

- **Startup won't drop a key that's in use.** If `secrets` holds a key ID that isn't configured, startup is refused, naming the key and how many secrets use it.
- **A secret that won't decrypt is skipped** with a Warning naming its ID, and stays on its key, so that key can't be removed. The rest carry on, and the run's details count it as unreadable.
- **Progress is `IMasterKeyRotation`:** the active key, when it became active, and how many secrets each key seals. The health page (#49) will show it. Until then, it's in the log and in `job_runs.details`.
- **An owner can replace a secret mid-batch.** `key_id` is a concurrency token, so that batch saves nothing, and the next one finds whatever is left.

### Data Protection keys

ASP.NET Data Protection protects the session cookie, antiforgery tokens and a circuit's state. Its key ring is in `data_protection_keys`, and each key's secret part is a row in `secrets`, so the master key encrypts it and rotation covers it. A deployment is still the application, its configuration file and the database (NFR-DEP-01), and a restart or a new host keeps everyone signed in.

- **The framework loads the key ring in its own `StartAsync`**, and the Razor components register it before `AddPlatform`. That's why `DatabaseStartup` migrates in `StartingAsync`.
- **Each key's XML names `SecretStoreXmlDecryptor` by type, including the assembly version.** Renaming or moving that class, or lowering the version, would make every key unreadable and sign everyone out. `DataProtectionKeyTests` pins the name.
- **The application name is fixed as `Ecnanib`.** The default is the content root's path, so moving the application would have meant new keys.
- **The key's secret is saved before its key ring row.** A failure between the two leaves a secret that nothing refers to, which is encrypted and harmless.

## Email

`Platform/Email` sends mail through the SMTP server in the `Email` section (EXT-EML-01). Accounts and [Notifications](#notifications) send through it. With `Email:Enabled` false, nothing is sent and `IEmailSender.Send` throws, so a caller checks first: Identity through `IEmailVerification.IsEnabled` and `IPasswordResets.Mode`, and Notifications through `IEmailVerification.IsEnabled`.

- **Sending is a job run.** `Send` hands the message to `IJobRunner` as a `send-email` run and returns at once. The time a request takes can't show whether it sent anything, which matters for registration and resets, and each send gets a `job_runs` row and its own correlation ID.
- **Nothing stores a message,** so one a crash cuts off is lost. Everything sent so far is something the user can ask for again, and a durable outbox would hold live tokens in the database.
- **TLS is required, never opportunistic.** `UseTls` means TLS from the first byte on port 465 and required STARTTLS on any other port, never MailKit's `Auto` or `StartTlsWhenAvailable`, which carry on in clear when STARTTLS isn't offered. `UseTls=false` is refused outside Development unless the server is on loopback.
- **Neither the address nor the body reaches a log.** A server's refusal usually repeats the address, and the job runner logs a failed run's exception, so `SmtpCommandException` is replaced by an exception that holds only its codes. `details` stays empty.
- **Messages are plain text,** so nothing in one is read as markup. `EmailMessage` is a class whose `ToString` prints nothing, because its body can hold a token.
- **Only `Platform/Email` may use MailKit's `SmtpClient`,** and nothing may use `System.Net.Mail.SmtpClient`, which can't require STARTTLS. `BannedApis` enforces both.
- **The settings are checked only when email is on.** `EmailStartup` takes them, so a bad value stops startup before any migration runs, and it logs whether email is on and through which server.

`SmtpEmailSenderTests` sends to a real server, which `MailpitFixture` runs in a container. Other tests replace `IEmailSender` with `CapturedEmail` and follow the links in what it keeps.

## Notifications

`Modules/Notifications` holds a user's notifications (FR-NTF-01) and emails the ones they chose (FR-NTF-02, FR-PREF-06). Each feature raises its own through `INotifications`. In-app is always on, so a notification is a row in `notifications` whatever the email settings, and email follows in the background.

### Raising one

| Call | When |
|---|---|
| `Add(context, notice)` | Alongside a change, such as a trade. It adds to the caller's context and doesn't save, like an [audit record](#writing-a-record), so the notification commits with the change or neither does |
| `RaiseAsync(notice)` | A raiser with nothing else to commit |
| `RaiseForAdministratorsAsync(notice)` | `CatalogSyncFailed` and `ExchangeRateLimitBan`, one row for each account that's an administrator now. Someone who becomes one later doesn't get earlier ones |

**A `Notice` checks itself when it's built,** so a bad value throws in the raiser rather than failing the save of the change it's about:

- **The type** is a `NotificationType` from `Kernel`, stored by name, so never rename one. A user's notice can't take an administrator's type, and an `AdministratorNotice` can't take a user's.
- **The link** is a path on this site, such as `/agents/42`, of letters, digits, hyphens and underscores between slashes, at most 256 characters, with no query or fragment (FR-NTF-03). So it can't leave the site, carry a token, or hold `..` or `%`. Email puts `App:PublicBaseUrl` in front of it.
- **The subject** is what the user would recognise it by, such as an agent's name or a key's label. It's user text, so pages only show it as text. Control characters, including the NUL that PostgreSQL refuses, and half surrogate pairs are removed, and it's cut to 120 characters. It's then scrubbed with the [masking rules](#masking) through `ISecretScrubber`, as a backstop: it can't recognise a secret with no marker, so never put one there.
- **`Demo`** labels it wherever it's shown (FR-UI-07).

`payload` is JSON holding the subject and `Demo`. A feature that needs more adds a nullable property to `NotificationPayload`, which older rows still read, and its text, with no migration.

**There's no foreign key to `users`.** The boundary tests refuse `HasOne<User>()` from another module, and account deletion reaches this table through the port Identity declares (#51). Until then, a notification for a user that doesn't exist is stored, and nobody can see it.

### Reading them

`/notifications` lists a user's notifications, newest first and 50 at a time, like the [activity page](#a-users-history). Every call takes the user's ID and is scoped to it (NFR-SEC-04), so marking another user's notification does nothing.

- **Following a notification's link marks it read** on the way. The link navigates as any link does, inside the circuit or, for a static page, as a full page load, and its click reaches the circuit first. Opening it in another tab marks it too. There's no GET that marks anything.
- **"Mark all as read" stops at the newest one shown,** so one that arrived while the page was open stays unread.
- **The menu shows the unread count** to signed-in users, as words in the link, capped at 99+. `Layout/UnreadCount` reads it when it renders: on the prerender and again when the circuit starts, on every navigation inside a circuit, and when the list says something was read. That's a query by a partial index each time.
  - **The circuit reads again rather than taking the prerender's count with `[PersistentState]`.** A full page load from a notification's link can be prerendered before the click that marked it read commits, and the second read corrects it.
  - **The list signals through `UnreadNotifications`, a scoped service,** so a circuit has one whatever the layout does. A page with its own `@layout`, such as Not Found, mounts a new menu, and a cascaded value from the old layout wouldn't reach it.
  - **Only the latest read counts.** A read a navigation started can finish after the one the list's signal started, so `UnreadCount` numbers them and drops a result that a newer read has overtaken.
  - **A failed count keeps the last one** and logs a Warning, because a throw from an event handler ends the circuit.
- **A new notification doesn't reach a page that's open** until the next navigation. Live updates are #41's.

### Notification email

`NotificationEmailJob` (`notification-email`) runs every minute with email on, and never with it off, so it adds no rows to `job_runs` then. It sends each user at most one email per run, listing everything new, which is the throttle: one event can fire for many agents, and a storm becomes one email.

- **Only verified addresses.** Anyone can register an address that isn't theirs, so an unverified one could be a stranger's inbox. With email on, verifying is always possible, and the preferences page says so. A notification raised before the user verified isn't sent later.
- **Only the types the user chose.** Until they choose, the defaults are the ones that need them to act: approval pending, the three pauses, credential invalid, model retired and kill switch executed, plus both administrator types for administrators (`EmailNotificationChoices.Defaults`). The choices are in `user_preferences.email_notifications`, as names, where null means never chosen and empty means none.
- **`email_due` is set when it's raised,** from whether email is on. Turning email on later doesn't send old notifications.
- **Rows are cleared before the message goes,** in one transaction per user, then `IEmailSender.Send` hands it over. A failed write therefore sends nothing rather than the same email every minute. `emailed_at` means handed to the sender, which records its own `send-email` run, not delivered.
- **50 users per run,** because each message is a `send-email` run with its own SMTP connection. The rest wait for the next minute, oldest first. One message lists 20 notifications at most, and counts the rest with a link to the list.
- **Older than 24 hours isn't emailed,** so after an outage yesterday's news stays in the app rather than arriving as a burst.
- **Shutdown** stops it between users, through `context.Stopping`, so a run interrupted half way leaves the rest due.
- **It never logs an address or a subject.** It logs a count under the user's ID. `details` holds counts only.
- **Text is in `Modules/Notifications/Emails.resx`,** plain text, with times in UTC like the account emails. It's a second file named `Emails.resx`, so `Ecnanib.csproj` gives its generated class its own file name. The Web UI gives each type its own words in `Strings.resx`, through `NotificationText`.

`IEmailRecipients` in Identity answers who gets what. It hands out other users' addresses, so `BannedApis` allows it only in Identity and Notifications.

## Exchange adapter

`Modules/ExchangeAdapter` is the only way to Binance: Spot and USDⓈ-M Futures, each in Live and Demo (CON-12, EXT-BIN-01, NFR-MNT-03). It may use `Kernel` only, so keys arrive as `SecretValue`s and bans leave through a port. Every call goes through `BinanceTransport`, which reserves request weight, signs, sends, logs once, and updates the server's limits from the response.

| Contract | What | Who uses it |
|---|---|---|
| `IExchangeMarketData` | Public data, with no keys: exchange information, candles, prices, the top of the book, Futures funding and the funding intervals Binance adjusted | Market Data, Credentials, Execution |
| `IExchangeAccount` | Signed reads: the account, a Live key's restrictions, an order by its client order ID, a symbol's Futures positions, the Futures account's settings, a symbol's margin type and leverage, and its leverage brackets | Credentials, Execution |
| `IExchangeTrading` | Placing and cancelling orders, and setting a Futures symbol's margin type and leverage | **Execution only** (AC-01). The map lets four units use the adapter, so `BannedApis` enforces this |
| `IExchangeUsage` | Each server's weight, limits, backoff, ban and clock offset | Administration, for the health page (#49) |
| `IExchangeBanObserver` | A port the adapter calls when Binance bans this server's address | Execution implements it |

### Servers

There are five, each with its own base URL, weight budget, clock offset and bans, because Binance counts and bans an address per server: Spot Live, Spot Demo, Futures Live, Futures Demo, and Spot market data, which serves public Spot Live calls (EXT-BIN-02). Public Demo data comes from the Demo servers, because Demo prices aren't Live ones (EXT-BIN-08).

- **Base URLs come only from the `Binance` section** (NFR-SEC-10), with no defaults in code. Each is a server's address and nothing more, over https outside Development. `appsettings.json` ships Binance's official ones.
- **The named client `Binance` follows no redirects**, or a response could send a signed request somewhere else, and keeps no cookies. `RemoveAllLoggers()` drops `IHttpClientFactory`'s own logging, which would write each full URI, signature included.
- **Every request other than a GET has an empty body.** .NET's handler sends a request with no body again, up to three times, when its connection drops before an answer, and the caller sees only the last attempt. For an order, that's a second order. A body, even an empty one, stops it. `RetryTests` proves both on a real socket, so a change in .NET would show.

### Signing and the clock

- **The signature covers exactly what's sent.** Values are percent-encoded with `Uri.EscapeDataString`, which Spot requires before signing since 2026-01-15, then `timestamp` and `recvWindow` follow, then the signature, last. Every parameter goes in the query, POST included.
- **HMAC** is lower-case hex HMAC-SHA256, over the secret revealed as bytes. **Ed25519** imports the PKCS#8 PEM through NSec, signs, and sends percent-encoded base64. Binance documents Ed25519 for Spot only, and its Futures pages name HMAC and RSA, so [Credentials](#validating-a-key) counts Futures as enabled for a key only once a signed Futures request with it has succeeded.
- **A key's restrictions come from the wallet API**, `GET /sapi/v1/account/apiRestrictions`, which Binance documents on `api.binance.com` only, so `GetApiRestrictionsAsync` refuses a Demo key. It's sent to Spot Live and reserves weight there. Binance counts SAPI weight apart and reports it in `X-SAPI-USED-IP-WEIGHT-1M`, which the adapter doesn't read, so the reservation is a slight overestimate and the response doesn't move the budget.
- **`ExchangeKey` holds the secrets, and its caller disposes them.** The API key is revealed into the `X-MBX-APIKEY` header and the secret into the signer, and nothing keeps either.
- **The offset is Binance's time minus the midpoint of a `time` request's round trip** (FR-EXE-10). A signed request syncs it when it's missing or older than 10 minutes, one sync per server at a time. A -1021 resyncs it and sends the request once more: Binance refused the first, so this is safe even for an order.
- **A failed routine sync keeps the previous offset**, with a Warning, and isn't tried again for 30 seconds, so a failed time request can't stop a protective order. Clocks drift slowly, and a -1021 would catch real drift. With no offset yet, the request isn't sent, and fails as `Unavailable`. When the resync after a -1021 fails, the request is still `Rejected`, because Binance refused it.
- **`Binance:RecvWindowMs` is at most 10000**, though Binance allows 60000, because an order with an unknown outcome stays open for its receive window. See [Outcomes](#outcomes).

### Request weight and priority

This is the request-weight model NFR-PERF-04 asks for. #56 measures it.

- **Binance reports used weight on every response**, in `X-MBX-USED-WEIGHT-1M`. The adapter keeps the latest figure for the current minute and adds the weight of requests in flight. `BinanceEndpoint` gives each endpoint's weight. Those are estimates, never below 1, because Binance charges a failed request even where a successful one is free. The header is what counts.
- **The limit comes from exchange information's `rateLimits`**, and until that loads, from Binance's published defaults: 6000 a minute on Spot, 2400 on Futures.
- **Normal requests stop at `limit × (100 − Binance:WeightHeadroomPercent) / 100`. Protective ones may use the headroom** (FR-EXE-09). Placing or moving a stop, closing a position, and the reads those depend on are protective. A clock sync takes the priority of the request that needed it.
- **Nothing waits.** A refused request throws `Throttled` with `RetryAfter`, and the caller decides what to do, because a cycle has a deadline.
- **HTTP 429** backs the whole server off until `Retry-After`, or until the minute ends. **-1015**, too many orders, backs off only that key.
- **Order counts** come from `X-MBX-ORDER-COUNT-*`, per key, against the `ORDERS` limits, with the same headroom rule. Binance counts orders per account, but the adapter only knows keys, so two keys on one account are counted apart.

### Bans

- **HTTP 418 halts only the server that sent it**, until the time in Binance's "banned until" message, or `Retry-After`, or two minutes. Binance's own time comes first, because it's the same for every request in flight. That's how EXT-BIN-04's "halt all exchange requests" is read: Binance bans an address per server, so a ban on Spot Demo leaves Futures Live free to protect positions. Everything to the banned server stops, protective requests included, because Binance would refuse them.
- **Observers hear once per ban.** Requests in flight together each get a 418, a second or so apart, so a ban less than 10 seconds longer than the current one is the same ban. `ExchangeBanNotifier` in Execution raises `ExchangeRateLimitBan` for administrators in an `exchange-ban-notification` run, linking to `/admin/health`, which #49 adds.
- **A ban isn't kept across a restart.** The first request afterwards gets a 418 again, which is reported again. That's the recovery path [Inversions](#inversions) asks of an event.

### Outcomes

| Response | `Failure` | `MayHaveExecuted` |
|---|---|---|
| 4XX with Binance's code | `Rejected`, with `Code` | No |
| 429, or refused before sending | `Throttled`, with `RetryAfter` | No |
| 418, or the server is banned | `Banned`, with `RetryAfter` | No |
| The clock couldn't be synced, so it wasn't sent | `Unavailable` | No |
| 5XX, 408, -1000, -1006, -1007, a duplicate client order ID, a timeout, a broken connection, anything else that goes wrong once it's sent, any other status such as a redirect, or a successful order whose body this version can't read | `Unknown`, with `SettlesAt` for an order | Yes |

- **Every 5XX is unknown**, as EXT-BIN-05 says, even the 503s that Binance's Futures pages call certain failures. So are -1000, -1006 and -1007, which say Binance doesn't know either, whatever status they come with. So is a client order ID already in use (Futures -4116, Spot -2010 "Duplicate order sent."), which proves an order with it exists. The query by client order ID settles each (FR-EXE-05).
- **An unknown order can still be accepted until its receive window has passed.** Binance checks the timestamp when a request arrives, so one held up on the way can arrive after the adapter gave up on it. `SettlesAt` is the timestamp plus the receive window, plus 5 seconds for clock error and Binance's queues, by this server's clock.
- **`GetOrderAsync` returns null only for -2013**, Binance's "Order does not exist", and only after `SettlesAt` when this process sent the order with an unknown outcome. Before then it throws `Unknown` with the same `SettlesAt`. Null is what allows sending again, so nothing else returns it. The memory is per process, so Execution persists `SettlesAt` with the order to hold across a restart. The algo service's code for a missing order isn't documented, so a missing conditional order may throw `Rejected` instead, which is the safe direction.
- **Messages hold the server, the path, and Binance's code and message**, never a URL, query, key or signature.

### Timeouts and cancellation

- **Reads** give up after 15 seconds, and follow the caller's token throughout.
- **Placing and cancelling** check the caller's token only until the send begins. After that, only the adapter's own 10 seconds applies, and running out is `Unknown`. That fits the [drain window](#shutdown), so a shutdown can't cut an order off half way (NFR-REL-04). The client's own timeout is off.

### Futures conditional orders

Since 2025-12-09, Binance takes Futures `STOP_MARKET` and `TAKE_PROFIT_MARKET` only through its algo order service: `POST /fapi/v1/algoOrder` with `algoType=CONDITIONAL`, `triggerPrice` and `clientAlgoId`. `/fapi/v1/order` refuses them with -4120. So `OrderType.StopMarket` and `TakeProfitMarket` go there on Futures, and are read and cancelled there by client ID, which is why an `OrderReference` carries the type. Their statuses add `Triggered` and `Finished`, and `TriggeredOrderId` is the order a triggered one became. On Spot they're `STOP_LOSS` and `TAKE_PROFIT` on the ordinary endpoint.

The algo endpoint's own page couldn't be read, so its parameters come from Binance's official generated connectors. Check them against Futures Demo before #34 relies on them.

### Futures account settings

EXT-BIN-07 and FR-RISK-09 need the Futures account's settings, and change a symbol's. The weights and codes come from Binance's USDⓈ-M pages:

| Call | Endpoint | Weight | What |
|---|---|---|---|
| `GetFuturesAccountConfigAsync` | `GET /fapi/v1/accountConfig`, signed | 5 | Whether the account may trade, Hedge Mode (`dualSidePosition`) and Multi-Assets Mode, in one call. The single-purpose `positionSide/dual` and `multiAssetsMargin` cost 30 each |
| `GetFuturesSymbolConfigAsync` | `GET /fapi/v1/symbolConfig`, signed | 5 | A symbol's margin type and leverage on the account. Null when Binance lists nothing for it |
| `GetLeverageBracketsAsync` | `GET /fapi/v1/leverageBracket`, signed | 1 | A symbol's brackets, which Binance can adjust per account (`notionalCoef`), lowest first |
| `GetFundingIntervalsAsync` | `GET /fapi/v1/fundingInfo`, public | 1 | Only the symbols whose funding Binance adjusted. Any other is on 8 hours (`IExchangeMarketData.DefaultFundingIntervalHours`). Binance gives it weight 0, under a limit of its own of 500 per 5 minutes, shared with the funding history, which the adapter doesn't track: reserving 1 overestimates, and [Market Data](#sharing) asks once per environment and close |
| `SetMarginTypeAsync` | `POST /fapi/v1/marginType`, signed | 1 | -4046 means it's already that type, -4047 and -4048 that open orders or a position stop the change, and -4168 that Multi-Assets Mode allows cross margin only |
| `SetLeverageAsync` | `POST /fapi/v1/leverage`, signed | 1 | -4028 means the leverage isn't valid, -4209 that it's above the symbol's maximum, and -4161 that an isolated position's leverage can't be lowered while it's open |

- **The two changes aren't trades.** They place no order and are safe to send again, so they keep the caller's token and the read timeout, and a 5XX is an unknown outcome that the next read settles. They're on `IExchangeTrading` anyway, because they change the account, which only Execution may do.
- **Binance documents the brackets' answer as an array of symbols or one symbol**, without saying which comes when one symbol is asked for, so both are read. It doesn't say whether `notionalCap` and `notionalFloor` already include `notionalCoef`. They're used as sent.
- **Not checked against Futures Demo yet:** whether Demo serves `accountConfig`, `symbolConfig` and `fundingInfo`, the brackets' shape with `symbol`, and `notionalCoef`. Check them before #34 sends the first Futures entry.

### Exchange information

- **It's held in memory per market and environment**, loaded on first use, and refreshed once it's older than `Binance:ExchangeInfoRefreshHours` (EXT-BIN-06). It's lazy rather than a job, because the adapter can't use Platform. Market Data keeps a copy in `exchange_symbols`: see [Exchange symbols](#exchange-symbols).
- **A failed refresh keeps the last copy**, with a Warning, and waits a minute before trying again. With no copy, it throws.
- **A filter rejection can expire it early (FR-RISK-04).** `ExpireExchangeInfo` marks the copy stale, so the next read fetches, unless the copy is under a minute old, so a run of rejections can't spend the weight again and again. A failed refresh's minute still holds. `ExchangeException.IsFilterRejection` names the codes that count, from Binance's error code pages: check them against Demo when orders are first placed (#34).
- **Price bands are kept as four multipliers**, a 0 meaning no bound: Spot's `PERCENT_PRICE_BY_SIDE` by side, Spot's older `PERCENT_PRICE` for both sides when there's no by-side filter, and Futures' `PERCENT_PRICE`, which bounds a buy from above and a sell from below around the mark price.
- **Callers share one refresh, which none of them can cancel.** One that stops waiting gets the last copy if there is one, and the refresh carries on for the next caller. The transport's read timeout bounds it.
- **Spot asks for `showPermissionSets=false`**, which cuts the full body from about 18 MB to 7 MB.
- **Each refresh updates its servers' limits.**

### Market data and positions

- **Candles** come from `klines`, filtered by open time with both ends included. Binance's documentation doesn't say that, but a read-only call to it showed it, and the adapter drops anything outside the range anyway. A call returns at most 1000. Spot charges 2 whatever the limit. Futures charges by the limit: 1 below 100, 2 below 500, 5 up to 1000. The adapter reserves the matching weight.
- **Quotes:** Spot reads the price and the top of the book from one `ticker/24hr`, which costs 2. Futures has no such ticker, so it reads `ticker/bookTicker` (2) and `ticker/price` (1), and its funding from `premiumIndex` (1).
- **Positions:** `GetPositionAsync` reads `positionRisk` v3 for one symbol, not the whole account (NFR-PERF-05), signed, with an estimated weight of 5. Binance sends a liquidation price of 0 when nothing would liquidate the position, which becomes null. It also lists a symbol that has only open orders, with an amount of 0, which is left out.
- **The Futures ticker and position pages couldn't be read** when this was written. Their weights are estimates, and the position's fields come from Binance's published examples. Check both against Futures Demo before #35 relies on them.

### Logging

- **One event per call under `Exchange`** (FR-LOG-08): the server, method, endpoint, status, latency, Binance's used weight and the limit, the client order ID, and Binance's error code. The endpoint is the path, never the query. Successes are Information. Rejections, 429s and refusals before sending are Warnings. Bans, server errors and unknown outcomes are Errors.
- **Events carry the caller's scope**, so a cycle's correlation, agent and cycle IDs reach them.
- **The query, key and signature are never logged.** `ExchangeLoggingTests` checks with canary keys too short for the shape rule. Masking also catches `X-MBX-APIKEY`, `signature=`, 64-character keys and HMAC signatures, and PEM private keys, as a backstop.

### Testing it

`FakeBinance` in `tests/Ecnanib.Tests/Exchange` replaces the named client's handler, and answers each method and path as a test says. `AppFactory` gives every application hosts under `.test`, which never resolve, so no test reaches Binance by accident. `ExchangeHarness` gives each test its own application, clock and fake, because the adapter's state is in memory. `RedirectTests` and `RetryTests` use `LoopbackServer`, a real socket, because redirects and retries happen inside the real handler, which the fake replaces.

## Market data

`Modules/MarketData` gives a decision cycle steps 3 and 4 of FR-CYC-02, and keeps Binance's symbols. It may use Platform and the exchange adapter, and nothing else. It holds no keys, so the liquidation price, which belongs to the account, comes from reconciliation (#35) through `IExchangeAccount.GetPositionAsync`.

| Contract | What | Who uses it |
|---|---|---|
| `IMarketData` | A snapshot for one symbol, timeframe and candle close: two candle series with their indicators, the quote, and Futures funding | DecisionCycle (#31) |
| `IExchangeSymbols` | Binance's symbols and their filters, from `exchange_symbols` when Binance can't be reached, and a refresh after a filter rejection | Agents, the wizard (#40), and the Risk Engine's callers (#31, #34) |
| `Indicators` | The indicator functions, pure and deterministic | Market Data, and anything that needs the same numbers, such as shadow outcomes (#44) |
| `MarketDataException` | A failure in Market Data's terms: `Unavailable`, `Throttled` with `RetryAfter`, or `UnknownSymbol` | Its callers, which can't see the adapter's `ExchangeException` |

### What a snapshot holds

- **The caller passes the candle's close**, which is the cycle's scheduled time (FR-CYC-01). The last candle is the one that closed then, so every candle is final. A later time within the next candle means the same candle, and a close in the future is refused.
- **The higher timeframe follows SRS A.1:** 1H for 15m, 1D for 1H and 4H, and 1W for 1D. Weeks start on Monday at 00:00 UTC, as Binance's do. The 200-day average that A.1 asks of 4H and 1D is the 1D series' EMA 200.
- **Each series is the last 500 closed candles** (`CandleSeries.Window`), enough for EMA 200 with 300 candles to settle. A newer symbol has fewer. When Binance has no candle for a time, as during maintenance, there's a gap, so a caller compares the last candle with `LastOpenTime`.
- **The caller gets the whole window.** `PromptWindows` decides how many candles each role's prompt gets. A trace's input snapshot keeps only each series' latest candle, because the candles each role saw are in its prompt.

### Sharing

FR-CYC-09 asks for one fetch per symbol, timeframe and candle, whatever the number of agents.

| Data | Shared by | Reused while |
|---|---|---|
| Candles | Market, environment, symbol and interval | It's the same candle. Memory keeps one load per series, for its latest candle |
| Quote and funding | Market, environment and symbol | The fetch began at or after the candle's close, and is less than a minute old |
| Futures funding intervals | Environment, for every symbol | The same as the quote. Binance's list covers every symbol it adjusted, so one call serves every Futures symbol closing together. A symbol it doesn't list is on 8 hours |

- **A load runs with no caller's token.** Callers wait with `WaitAsync`, so one that stops waiting can't cancel it for the others, as with [exchange information](#exchange-information). The adapter's read timeout and the database's command timeout bound it.
- **A failed load isn't kept.** The next caller starts another.
- **A load for an older candle**, such as a catch-up cycle's, runs on its own and leaves the latest in place.
- **Loads of one series run one at a time**, so two can't write the same rows. The lock is in process, which is enough with one instance (ASM-06).
- **A shared load logs under the scope of the caller that started it**, so its `Exchange` events carry that cycle's correlation ID.
- **Memory** holds the latest load of each series in use, about 70 KB each.

### The candle cache

`candle_cache` is SRS 6.1's CandleCache. It holds closed candles, keyed by market, environment, symbol, interval and open time. A load:

1. Reads its window from the table.
2. If the last candle and the start are both there, uses them, and Binance isn't asked.
3. If the start is there but newer candles are missing, asks only for those, from the newest cached one on. That's usually one candle, which costs 1 on Futures.
4. Otherwise, asks for the whole window.
5. Writes in one transaction: deletes the fetched range, and inserts what came back, with `fetched_at`.

- **A candle that's still open is never stored.** The range ends at the last closed candle, and anything whose close time hasn't passed is dropped as well.
- **"The start is there"** means the window's first candle is cached. When the last full fetch came back short, it means the first candle Binance had is cached instead. That's kept in memory, so a new symbol or a weekly series isn't fetched whole every time. A restart costs one full fetch per series.
- **A missing candle at the end** is asked for again with the next candle, not on every call.
- **Retention (#50) purges by `fetched_at`.** A daily window spans more than a year, so purging by `open_time` would leave every 1D cycle fetching its whole window. A purged start is fetched again with its window.

### Exchange symbols

`exchange_symbols` is SRS 6.1's ExchangeSymbol. It holds Binance's latest list per market and environment, with each symbol's filters as decimal columns and its order types as `text[]`.

- **It reads through, with no job.** `IExchangeSymbols` asks the adapter, whose copy refreshes itself after `Binance:ExchangeInfoRefreshHours` (EXT-BIN-06). When that copy is newer than the stored one, it replaces the rows in one transaction, deleting the symbols Binance dropped. A job would call Binance at startup, from every test application too.
- **When Binance can't be reached, the table answers**, with its `RefreshedAt`, and there's a Warning. With nothing stored, it throws `Unavailable`.
- **Failing to store doesn't fail the read.** The table is a copy, and the caller already has the data. There's a Warning, and the next read tries again.
- **`RefreshAsync` fetches and stores again after a filter rejection** (FR-RISK-04), through the adapter's early expiry. Execution calls it (#34).

### Indicators

They're computed in `decimal`, so the same candles give the same values on any machine, and they're never rounded. EMA, RSI and ATR follow TA-Lib, and `IndicatorTests` checks them against TA-Lib's output for 300 real candles.

| Indicator | Definition |
|---|---|
| EMA 20, 50 and 200 | Starts at the simple average of the first n closes, then moves by 2 / (n + 1) of each difference |
| RSI 14 | Wilder's: 100 × average gain / (average gain + average loss). The first averages are simple means of the first 14 changes, then each change is smoothed in by 1/14. A flat series is 50, where TA-Lib gives 0 |
| ATR 14 | Wilder's average of the true range, which counts the gap from the previous close |
| Swing highs and lows | Above each of the 3 candles before, and no lower than each of the 3 after, so a flat top counts once. The last 3 aren't confirmed. The newest 5 of each |
| Volume | The last candle's volume, the mean of the last 20, and their ratio |

An indicator without enough candles is null: EMA n needs n candles, RSI and ATR need 15, and volume needs 20.

### Request weight per candle

This adds to the model in [Request weight and priority](#request-weight-and-priority). The cost is per symbol, market, environment and candle, however many agents share it:

| | Spot | Futures |
|---|---|---|
| The agent's timeframe, once cached | 2 | 1 |
| The higher timeframe, once per its own candle | 2 | 1 |
| Price and top of book | 2 | 3 |
| Funding | | 1, plus 1 per environment and close for the intervals, whatever the number of symbols |
| A whole window, on a cold cache or after a purge | 2 | 5 |

So a cycle on a symbol no other agent trades costs about 4 on Spot and 5 on Futures, and more agents on the same symbol cost nothing more. #56 measures it.

### Testing it

`MarketDataApp` in `tests/Ecnanib.Tests/MarketData` gives each test its own application, clock, database and `FakeBinance`. The fake answers candles from `BinanceSamples.Klines` the way Binance does, including a candle that's still open when the range reaches it. A second app on the same database stands in for a restart. The reference candles in `MarketData/Samples` are real BTCUSDT 1h candles, and the expected values in `IndicatorTests` came from TA-Lib 0.8.3, as its comment shows. If you change either, regenerate the other.

## AI adapters

`Modules/AiAdapters` is the only way to Anthropic, Google, OpenAI and xAI (CON-13, EXT-AI-01). It may use `Kernel` only, so keys arrive as `SecretValue`s. Every call goes through `AiTransport`, which waits for a slot on the key's credential, sends to the configured server with a timeout, normalizes the failure, retries, and logs each attempt. An adapter only builds requests and reads answers, so adding a provider takes an adapter and its configuration section, and nothing in the council, risk or execution changes (EXT-AI-05, NFR-MNT-02).

| Contract | What | Who uses it |
|---|---|---|
| `IAiProviders` | The providers that have an adapter, and the models a key can use | Credentials to validate a key, Catalog to sync |
| `IAiCompletions` | Calling a model | **AiCouncil only**, because it spends the user's money (FR-CYC-07). The map lets three units use the adapters, so `BannedApis` enforces this |
| `IAiProviderAdapter` | One provider's request, answer and error formats | Each adapter implements it. It's public so that tests can add a provider. It never sends anything, so another unit that used it still couldn't reach the network |

### Providers and settings

- **A provider is its adapter's `Name`**, such as `OpenAI`. That name is the key for its `AiProviders` section, the catalog's `providers` row and a credential's provider. There's no `AiProvider` enum, because one would break NFR-MNT-02. A name is letters and digits, and two adapters can't share one.
- **Every adapter needs its section**, or startup stops, naming `AiProviders:<Name>:BaseUrl`. A section with no adapter is only a Warning: a misspelt name leaves the real adapter's section missing, and that stops startup anyway.
- **Base URLs come only from the section** (NFR-SEC-10), with no defaults in code. A base URL is a server's address and nothing more, over https outside Development. Adapters own their paths, including the API version, and give them as relative URIs. `AiTransport` puts the base URL in front and refuses anything else before it's sent. It reads the path's original string, because on Unix `"/v1/models"` parses as an absolute file URI. `appsettings.json` ships each provider's official server.
- **`Enabled: false` skips the rest of the section.** A call to a disabled provider fails as `Other` without being sent, with no attempts.
- **The named client `AiProviders` follows no redirects**, keeps no cookies, buffers at most 32 MB, and has `RemoveAllLoggers()`, as the [exchange's](#servers) does.

### The four providers

Each adapter speaks its provider's current API, as its docs stood in November 2026 (#19). The SDKs aren't used, because an adapter only builds requests and reads answers, and `AiTransport` does the sending.

| | Anthropic | Google | OpenAI | xAI |
|---|---|---|---|---|
| API | Messages, `POST /v1/messages` | Gemini, `POST /v1beta/models/{id}:generateContent` | Responses, `POST /v1/responses` | Responses, `POST /v1/responses` |
| Key | `x-api-key`, with `anthropic-version: 2023-06-01` | `x-goog-api-key` | `Authorization: Bearer` | `Authorization: Bearer` |
| Schema | `output_config.format` | `responseJsonSchema`, with a JSON answer | `text.format`, strict | `text.format` |
| Live search | `web_search_20250305`, at most 5 searches a call | `googleSearch` | `web_search` | `web_search` and `x_search`, without inline citations |
| Search and a schema | Search only | Search only | Both | Both |
| Models | `GET /v1/models`, 1000 a page | `GET /v1beta/models`, 1000 a page, `generateContent` models only | `GET /v1/models`, one page | `GET /v1/language-models`, one page, text output only |
| Output tokens | `output_tokens`, which includes thinking | `candidatesTokenCount` + `thoughtsTokenCount` | `output_tokens`, which includes reasoning | `output_tokens` + `reasoning_tokens` |

- **Search beats a schema where a provider can't do both.** Anthropic's web search always carries citations, which its structured output refuses with a 400. Gemini takes both only from Gemini 3, as a preview. So those two send the search tool and no schema, and the instructions still describe the answer. The council validates every answer against its schema anyway (FR-PRM-08).
- **Schemas stay in the subset every provider enforces.** Every object has `additionalProperties: false` and lists every property in `required`, and an optional property is a union with `null`. There's no `minLength`, `maxLength`, `minimum`, `maximum` or `pattern`, because Anthropic refuses them and Gemini drops some. OpenAI's schema is strict, so one outside the subset fails there as `InvalidRequest`. The council's contracts are built from nodes that can express nothing else, and `OutputSchemaTests` checks the result ([Output contracts](#output-contracts)).
- **Images are PNG or JPEG**, because xAI documents no others, and `AiImage` refuses the rest. The model's own earlier turns are text only, because no provider takes an image from the model.
- **Nothing is stored at a provider.** OpenAI and xAI keep a response for 30 days unless the request says `store: false`, and every request does.
- **No thinking, temperature or sampling settings are sent**, so each model uses its own defaults. Several current models refuse them. A reasoning model's thinking counts towards `MaxOutputTokens` everywhere but xAI, so a tight limit can stop it before it writes.
- **A model's details are whatever its provider says, and null otherwise.** Anthropic's list has a `line`, which is the family, and `capabilities`, from which the adapter reads `image_input`, `structured_outputs`, `server_tools.web_search` and `effort`. An epoch `created_at` means Anthropic doesn't know the date. Gemini's has `thinking`, which means the model takes a thinking level. xAI's has `input_modalities`. OpenAI's has nothing beyond the ID. `AdapterJson.OptionalBool` keeps a missing field null rather than false, and the catalog fills the gaps from the configuration ([Metadata](#metadata)).
- **xAI's list has prices too, which aren't read**, because xAI doesn't document their unit. They come from the configuration until it's confirmed.
- **Model lists leave out what doesn't write text.** Where the provider describes a model, the adapter goes by that: Gemini's `supportedGenerationMethods` and xAI's `output_modalities`. Otherwise it goes by name. That covers OpenAI's embedding, speech, Whisper, image, moderation, realtime, audio, transcription and Sora models, and Gemini's TTS, image, live and audio ones. A new family that doesn't write text will be listed until it's added, and an administrator can hide it (FR-CAT-05). OpenAI's fine-tunes are left out too: an `ft:` ID belongs to one account and names its organisation, so it mustn't reach a catalog every user sees (NFR-SEC-04). A key that can use nothing but fine-tunes is therefore `NoModels`.
- **An answer is the model's final text.** For Anthropic, that's the text after the last search or thinking block, which drops a sentence written before a search. For the Responses API, it's the last message item. For Gemini, it leaves out thought parts.
- **Truncated or refused.** These are truncated: Anthropic's `max_tokens` and `pause_turn`, Gemini's `MAX_TOKENS` and `CONTINUATION`, and an `incomplete` response. These are refused: Anthropic's `refusal`; Gemini's safety, recitation and blocklist reasons, and a blocked prompt; a `refusal` part; and `content_filter`.
- **Gemini's `generateContent` is legacy** beside Google's newer Interactions API, but it's fully supported. Moving to Interactions would only take a new `GoogleAdapter`.

### Keys

An `AiKey` holds the provider, the credential ID and a `SecretValue`, and the caller disposes it. The adapter reveals the key into a header as it builds each attempt's request, and nothing keeps it. Never put it in the URI: Gemini accepts `?key=`, and a URI can reach an exception.

### Failures

`AiFailure` holds EXT-AI-04's categories. The adapter's `ReadError` goes first, so a provider that sends an overload as a 400 can say so. Otherwise the status decides:

| Cause | `Failure` | Retried |
|---|---|---|
| 401, 403 | `Authentication` | No |
| 400, 404, 413, 422 | `InvalidRequest` | No |
| 429 | `RateLimit` | Yes |
| 503, 529 | `Overloaded` | Yes |
| 408, 504, or no answer within `TimeoutSeconds` | `Timeout` | Yes |
| A 2XX the adapter reads as refused | `Refusal` | Yes |
| Any other 5XX, or a broken connection | `Other` | Yes |
| A redirect, which is never followed, any other status, or an answer the adapter can't read | `Other` | No |

- **A refusal is retried**, because FR-ROLE-06 lists it, and its tokens count, because they were billed.
- **`Other` is retried only for a 5XX or a broken connection.** An adapter that maps something else to `Other`, such as OpenAI's `insufficient_quota`, gets no retry.
- **Some adapters correct a misleading status.**
  - Gemini sends an invalid key as 400 with the reason `API_KEY_INVALID`, and xAI sends one as 400 that only its message identifies. Both read as `Authentication`, which FR-KEY-04 relies on.
  - Gemini's `RESOURCE_EXHAUSTED`, `UNAVAILABLE` and `DEADLINE_EXCEEDED` decide over their status, and so does Anthropic's `overloaded_error`.
  - OpenAI's 429s for a key with no money left are `Other`: `insufficient_quota`, `credit_balance_exhausted` and the spend and usage limits.
- **The caller cancelling throws `OperationCanceledException`**, which is never retried, and is logged as `Cancelled`.
- **`AiException.Message` never holds the provider's own message**, which can quote the prompt, and whoever catches the exception may log it. That message is `ProviderMessage`, cut to 500 characters, for the decision trace. The provider's error code is kept only when it's a plain token, such as `rate_limit_error`. An answer the adapter can't read reports the exception's type, not its message, which could quote the answer.

### Retries and backoff

- **Up to `MaxRetries` more attempts.** The wait is the provider's `Retry-After` when it gave one. Otherwise it's `RetryBackoffMs`, doubled each time, with equal jitter, capped at 60 seconds.
- **A `Retry-After` over 60 seconds isn't waited for.** The exception carries it, and the council's fallback model is a better use of the cycle's deadline (FR-ROLE-06).
- **`Usage` and `Cost` add up every attempt**, on `AiResponse` and on `AiException`, because each one was billed.

### Concurrency

`MaxConcurrentPerCredential` is a semaphore per provider and credential. A call waits for a slot with the caller's token, holds it for one attempt, and gives it back before any backoff. A gate exists only while something holds or waits for it, so the dictionary doesn't grow with every credential. One credential never waits on another (NFR-REL-05).

### Timeouts and cancellation

Each attempt gets `TimeoutSeconds`, linked to the caller's token, and running out is `Timeout`. Unlike an order, an AI call is safe to abandon, so a job can pass it [`context.Stopping`](#the-two-signals).

### Cost

`AiRequest.Price` is the catalog's price per million tokens, in US dollars. Cost is `decimal` throughout (DATA-R-02), and null without a price. It's an estimate, because providers charge less for cached input and extra for searches. Adapters count reasoning and thinking tokens as output, because that's how providers bill them.

### Logging

- **One event per attempt under `AiCouncil`** (FR-LOG-07), because each attempt is a billed call. It has `Provider`, `Operation`, `Model`, `Role`, `Attempt`, `Outcome`, `StatusCode`, `LatencyMs`, `InputTokens`, `OutputTokens`, `Cost` and `ErrorCode`. `Outcome` is `Success`, an `AiFailure` or `Cancelled`. Success and cancellation are Information, `Other` is an Error, and any other failure is a Warning.
- **Events carry the caller's scope**, so a cycle's correlation, agent and cycle IDs reach them.
- **The prompt, the answer, the key and the provider's message are never logged.** `AiRequest`, `AiMessage`, `AiText`, `AiImage`, `AiResponse`, `AiCompletion` and `AiProviderError` override `ToString` to leave them out, so a `{Request}` logged by mistake leaks nothing. `AiLoggingTests` checks with canaries too short for the shape rule. Token counts survive masking through `SecretRules.IsTokenCount`.

### Testing it

`TestAdapter` in `tests/Ecnanib.Tests/Ai` is a provider with a format of its own, and `FakeAiProvider` speaks it. `AiHarness` gives each test its own application with two such providers, because credential slots are in memory. The redirect test runs against `LoopbackServer`, as the exchange's does, because only the real handler follows redirects.

Each real adapter has tests of its own, which take it from `AddAiAdapters` without starting the application. They check the requests it builds, and how it reads bodies shaped like the provider's documented ones, in `ProviderSamples`. `ProviderPipelineTests` sends all four through the pipeline to the fake.

`LiveProviderTests` calls the real providers at the shipped base URLs. Run it after changing an adapter, or when a provider changes its API. For each provider to check, set `ECNANIB_LIVE_<PROVIDER>_KEY` and `ECNANIB_LIVE_<PROVIDER>_MODEL`, such as `ECNANIB_LIVE_XAI_KEY` and `ECNANIB_LIVE_XAI_MODEL`. Then run `tests/Ecnanib.Tests/bin/Debug/net10.0/Ecnanib.Tests -class Ecnanib.Tests.Ai.LiveProviderTests`. A provider without both variables is skipped, so CI never calls one. A run costs a few cents a provider.

## Prompts

`Modules/AiCouncil` composes every AI call's prompt from ordered layers (SRS 3.7). Every role, market and timeframe combination resolves to a complete prompt (FR-PRM-02), and the same templates and request always give the same prompt.

| Contract | What | Who uses it |
|---|---|---|
| `IPrompts` | `ComposeAsync(request)`, from the stored templates | The council (#31) |
| `PromptComposer` | `Compose(templates, request)` and `Resolve`, which read nothing but their arguments | `IPrompts`, the tests, and #48's preview, which can compose drafts with sample data |
| `PromptRequest`, `PromptContext` | The combination, the custom instructions and the runtime context, with `PromptAccount`, `PromptPosition`, `PromptPendingEntry`, `PromptReflection` and `UntrustedContent` | The decision cycle builds them (#31) |
| `ComposedPrompt` | `Instructions` and `Message` for an `AiRequest`, and the `Templates` used | The council, which records the versions (FR-PRM-03) |
| `PromptTemplate`, `PromptTemplateKey`, `PromptLayer` | A version of a template and the combinations it applies to | #48's editor |
| `PromptWindows` | How many candles each role gets | The council and the tests |

### Layers

| FR-PRM-01 | Where it comes from | Where it goes |
|---|---|---|
| 1. System layer | Templates: `System`, plus the role's `OutputContract` | `AiRequest.Instructions` |
| 2. Role template | Template: `Role` | `AiRequest.Instructions` |
| 3. Market overlay | Template: `Market` | `AiRequest.Instructions` |
| 4. Timeframe overlay | Template: `Timeframe` | `AiRequest.Instructions` |
| 5. Custom instructions | The agent, in `<custom_instructions>` | The first user message |
| 6. Runtime context | The cycle, in `<runtime_context>` | The first user message |

- **The system layer is two templates.** The safety and untrusted-content rules are the same for every role, so they're one `System` template. The output contract (Appendix B) differs, so it's an `OutputContract` template per role. Both are administrators' to edit and never users'.
- **Nothing a user or a website wrote reaches the instructions.** Layers 1 to 4 are administrators' text, and providers keep `Instructions` apart from the conversation. Custom instructions and the runtime context go in the message, in FR-PRM-01's order. That's what stops custom instructions overriding the system layer, the risk limits or the output contract (FR-PRM-05), and the system template says so too.
- **Output contracts are prose.** Anthropic and Gemini get no schema when they search ([The four providers](#the-four-providers)), so the instructions must describe the answer. Each contract lists its schema's fields by the same names, which `PromptContentTests` checks ([Output contracts](#output-contracts)).

### Keys and overrides

A template's key is its layer plus any of role, market and timeframe. A layer's own dimension is required: a role for `Role` and `OutputContract`, a market for `Market`, and a timeframe for `Timeframe`. `PromptTemplateKey` refuses a key without it, and so does a check constraint.

- **For each layer, the most specific matching template wins** (FR-PRM-02). A template matches when every dimension it sets is the request's. More dimensions win, and between keys with as many, role beats market, which beats timeframe. Two different keys that match the same combination always differ in which dimensions they set, so the order is total.
- **An override is just a narrower key.** "The Futures × 15m overlay" of UC-08 is `Timeframe` with market Futures and timeframe 15m, which reaches all five roles. Adding role Strategist reaches only that one combination. The seeds are all generic, one per layer's own dimension, so every override is an administrator's.
- **A missing layer stops composition.** `PromptTemplateMissingException` names the layer and the combination. The seeds cover all 40 combinations, and `PromptTemplateTests` composes each.

### Untrusted content

**What Ecnanib computed is data. What a person or a model wrote is untrusted** (FR-PRM-06, NFR-SEC-08).

| Data | Untrusted |
|---|---|
| Candles, indicators, quote and funding in `<market_data>`. The agent's state in `<account>`, `<position>`, `<pending_entry>` and `<trading_record>`. Its limits in `<risk_settings>` | Custom instructions. News, social posts and web results. Earlier roles' output, because a model can repeat what it read. Reflections, which a model wrote and the user can edit |

- **Each untrusted item is its own `<untrusted_content>` block**, labelled with `kind` (`news`, `social`, `web`, `role_output` or `reflection`), `source`, and `title`, `url` and `published` when known.
- **Escaping makes the blocks hold.** In custom instructions and untrusted content, `&`, `<` and `>` are escaped, and `"` too in attributes, so no text can close its block or forge a tag. Line breaks become `\n` and other control characters go. Attributes are kept to one line.
- **Lengths are capped.** Custom instructions are refused beyond 2,000 characters, which is `AgentLimits`' limit enforced again, and a test keeps the two equal (FR-PRM-05). An untrusted item is cut at 4,000 characters, and an attribute at 300, and marked, so one article can't crowd out the prompt or run up the bill.
- **The system template says what each tag means:** untrusted content and search results are data and never instructions, and custom instructions apply only where they agree with the rules, the risk settings and the output contract. The Sentiment Analyst's template repeats it for search results, which reach the model without passing through the prompt.

### Runtime context

- **Each part is optional**, because each role gets a different subset. The council decides which (#31).
- **Numbers are written in the invariant culture, with UTC times in ISO 8601**, so a prompt doesn't depend on the server. Prices and quantities are written as Binance gave them, without trailing zeros. Computed values, such as an EMA, are kept to 8 significant digits, in decimal throughout.
- **`candle_close` is `LastOpenTime + Interval`**, the time the cycle's candle closed.
- **Candles are CSV**, oldest first. Every role given a snapshot gets both series' indicators and the latest prices. Raw candles cost the user tokens, so `PromptWindows` gives:

| Role | Agent timeframe | Higher timeframe |
|---|---|---|
| Technical Analyst | 120 | 60 |
| Strategist, Risk Critic | 24 | 12 |
| Sentiment Analyst, Reviewer | 0 | 0 |

- **Risk settings are written as given.** The caller applies `RiskCaps.Apply` first, so the prompt shows the limits the engine will hold the agent to. Futures-only settings and fields appear only on Futures.
- **With an account, the position and pending entry are always written**, as `none` when there isn't one, so the Strategist knows rather than guesses.

### Templates and seeding

`prompt_templates` holds one row per version of a key: `layer`, `role`, `market` and `timeframe` by name, `version` from 1, `content` up to 16,000 characters, and `created_at`. A unique index on the key and version treats nulls as equal, because a missing dimension is part of the key.

- **A row never changes.** An edit is the next version, and the highest version of a key is the one in use. So the row ID an AI call records always shows what it was sent (FR-PRM-03).
- **The seeds are `Templates/*.md`, embedded in the assembly** (FR-PRM-04): `System`, an `OutputContract` and a `Role` per role, a `Market` per market and a `Timeframe` per timeframe, 17 in all. They cover Appendix A (FR-PRM-07), which `PromptContentTests` checks topic by topic, with each market listing exactly FR-CYC-03's actions for it.
- **`PromptTemplateSeeder` adds version 1 for a key with no row at all**, so administrators' versions survive a restart. That means a changed seed reaches only a database that has never had its key. To ship a change to an existing database, publish it as a new version (#48).
- **There's no cache.** A cycle composes a handful of prompts from a small table, and a new version applies to the next prompt (UC-08).
- **Nothing shows a prompt's text.** `PromptRequest`, `PromptContext`, `PromptReflection`, `UntrustedContent` and `ComposedPrompt` override `ToString`, as the AI contracts do, because custom instructions are the user's and not even administrators may read them (FR-ADM-01, FR-LOG-07).

## Output contracts

Every role's answer is checked against its contract (Appendix B, FR-PRM-08), and an answer that breaks it is sent back once (FR-CYC-05). It all lives in `Modules/AiCouncil`, reads nothing but its arguments, and never calls a provider itself.

| Contract | What | Who uses it |
|---|---|---|
| `OutputContracts.SchemaFor(role, market)` | The `AiOutputSchema` for `AiRequest.Schema` | `CompleteAsync`, and the tests |
| `OutputContracts.Check(role, answer, context)` | The answer's `RoleOutput`, or every `ContractViolation` | `CompleteAsync`, the tests, and anything that re-reads a stored answer |
| `OutputContracts.CompleteAsync(send, request, context, logger)` | Sends with the schema, checks, and retries once | The council (#31), passing `IAiCompletions.CompleteAsync` as `send` |
| `TechnicalAssessment`, `SentimentAssessment`, `TradeProposal`, `CriticVerdict`, `TradeReflection` | Each role's output, read. The Risk Critic's is `CriticVerdict` because the Risk Engine has a `RiskVerdict` | The council, the decision cycle and the trace |
| `ContractContext` | The market, the agent's maximum risk, the position's side and the proposal being judged. `ContractContext.For(promptRequest)` takes them from the request the prompt was composed from | The council |
| `ContractedAnswer`, `ContractAttempt`, `OutputContractException` | The output and every billed call, or the failure and every billed call | The council and the trace |
| `TradeActions` | FR-CYC-03's actions by market, and their names as the SRS writes them | The schemas, and whoever maps a proposal to an `OrderIntent` |

### Schemas

- **Built from nodes that can express only the subset.** `ContractSchema.cs` has an object (every property required, `additionalProperties: false`), an array, a string with an optional enum, a number, an integer, a boolean, a time and a nullable. A nullable is `anyOf` with `{"type":"null"}`, which Anthropic, Gemini and OpenAI's strict mode all take. A time is a plain string to providers, because not every one takes `format`. The same nodes write the schema and check the answer, so the two can't differ.
- **The names are the templates'.** `PromptContentTests` checks that each `OutputContract` template lists its schema's top-level fields in order and names every nested one, because a provider that searches gets only the prose.
- **The Strategist's actions are the market's**, so a provider that enforces the schema enforces FR-CYC-03 too. The other four schemas are the same on both markets.
- **`close_fraction` isn't in Appendix B.** It's the share FR-CYC-03's partial `CLOSE` and `SELL` need, above 0 and at most 1, or null to close everything, and it maps to `OrderIntent.CloseFraction`.

### Checking an answer

Every violation is collected, so one retry can fix them all. Each has a JSON path, such as `$.signals[2].strength`, and a problem that follows it, such as "must be from 0 to 1". At most 20 are reported.

1. **Parse.** Whitespace and one Markdown code fence around the whole answer are allowed, because a model whose provider doesn't enforce the schema sometimes adds one. Anything else outside the object fails, and so does a truncated answer, which is reported as cut off.
2. **Structure**, against the schema's nodes: types, enums, missing properties, unknown ones and repeated ones.
3. **Read** into the role's record with System.Text.Json, which is strict, so a record that drifts from its schema fails every test that reads a sample. Then apply `ContractRules`:

| Contract | Rules beyond the schema |
|---|---|
| Technical assessment | `confidence` and each signal's `strength` from 0 to 1, levels above 0, signal names not blank, `summary` not blank and at most 600 characters |
| Sentiment assessment | `score` from −1 to 1, `confidence` from 0 to 1, `summary` as above, each source a title and an absolute http or https URL. Times are ISO 8601 with an offset, because one without would be read in the server's zone |
| Trade proposal | An entry needs `side` (long for BUY and OPEN_LONG, short for OPEN_SHORT), `entry_type`, a stop above 0, `risk_percent` above 0 and at most `MaxRiskPercent`, and `invalidation`. A limit entry also needs `entry_price` and `limit_ttl_candles` of at least 1. `close_fraction` is above 0 and at most 1. ADJUST_STOP needs a stop. Prices are above 0, `rationale` isn't blank, and `confidence` is from 0 to 1 |
| Risk verdict | `modify` needs at least one change, and `approve` or `reject` none. Against the proposal, each change reduces risk: a stop strictly tighter for the side, a risk percent strictly lower, and no target beyond the proposal's farthest. The side is the proposal's for an entry, and the position's otherwise. A change to a stop or risk percent the proposal doesn't have is refused |
| Reflection | `setup_summary` and `lesson` aren't blank |

- **Ambiguous, unusable or out of range is a violation. Noise isn't.** A field the action doesn't use, such as a stop on a HOLD, is cleared rather than refused, because a retry costs the user money and the field would be ignored anyway. Clearing it means nothing downstream can read it by mistake. Blank items in lists of text are dropped for the same reason.
- **Prices against the market are the Risk Engine's.** The check doesn't look at whether a stop is on the right side of the price, or a target gives enough reward-to-risk: the engine does that for every intent (rule 4).
- **A violation never quotes the answer.** The answer is a model's, and it could have copied instructions or a secret from a web page. The answer goes back to the model as its own turn, and the log and the retry's text carry only paths and rules. An unknown property is reported at its parent, listing the contract's names, because its own name is the model's.
- **Outputs show no text in `ToString`**, as the AI contracts don't (FR-LOG-07).

### The retry

`CompleteAsync` sends the request with its schema. If the answer breaks the contract, it logs a Warning with the model, role, attempt and the violations' paths, and sends the same request again with two more turns after the composed message: the model's answer, then the violations and a request for one JSON object and nothing else (FR-CYC-05). A blank answer can't be a turn, because providers refuse an empty one, so then the violations join the last user message.

| What happens | Result |
|---|---|
| The first answer conforms | `ContractedAnswer` with one attempt |
| The retry conforms | `ContractedAnswer` with both attempts |
| The retry breaks the contract too | `OutputContractException` with both attempts |
| The retry fails at the provider | `OutputContractException` with the first attempt, and the `AiException` as `RetryFailure` |
| The first call fails at the provider | The `AiException`, unchanged |

Either exception means the role failed, so its fallback model is next (FR-ROLE-06). `Usage` and `Cost` on the answer and on `OutputContractException` add up every billed call, the failed retry's included, because each was billed. The function takes `send` rather than `IAiCompletions`, so it opens no new way to spend a user's money: `BannedApis` still keeps that to AiCouncil.

`LiveProviderTests.Each_roles_answer_conforms_to_its_contract` sends every schema to the real providers, which is the only check that they take them. Run it after changing a schema.

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
| `UseClientOrigin` | After forwarded headers, so the [audit trail](#where-the-client-comes-from) gets the client's address. Outside re-execute, because a re-executed request comes from the same client |
| `UseExceptionHandler` | Doesn't run in Development |
| `UseHsts` | Inside the handler on purpose. It sets the header directly, so outside it the error page would lose the header |
| `UseAuthentication`, through `UseAccountAuthentication` | Goes here. The limiter needs the account, and the handler has to catch a failed session lookup |
| `UseRateLimiter` | Outside status pages, so a 404 costs one permit, and a 429 with no body doesn't render the 404 page |
| `UseStatusCodePagesWithReExecute` | |
| `UseStatusPagesAsGet` | A re-execution keeps the method, so a post an endpoint refused for its token would reach antiforgery twice, and reading a form that has already failed throws. A status page only shows something, so it runs as a GET |
| HTTPS redirect, except `/health` | |
| `UseAuthorization`, `UseAntiforgery` | Inside status pages, so a 403 renders one. Antiforgery tokens bind to the identity, so it goes after authentication |

### HTTPS

`App:PublicBaseUrl` is required outside Development and must be `https`. It sets the HTTPS port and the origin below, and nothing else reads the scheme. Without a port, `UseHttpsRedirection` quietly serves plain HTTP and logs one warning.

TLS can end at a reverse proxy (NFR-SEC-01). `X-Forwarded-For` and `X-Forwarded-Proto` are honoured from loopback and from `Security:TrustedProxies`, and ignored from anyone else, because a client can send either. `X-Forwarded-Host` isn't honoured at all. `ForwardLimit` is null, so a chain of proxies is walked back to the first address that isn't trusted.

- **A proxy that isn't trusted fails loudly.** The application sees plain HTTP and redirects, so the browser gets a redirect loop rather than silently losing HSTS and sharing one rate limit among all users.
- **Health is the one exception to the redirect.** Behind a proxy, a probe reaches the application directly, and a 307 would count as success under `curl -f` without running the check. It answers fixed values only.
- **HSTS has no `includeSubDomains`,** which could lock an operator's unrelated sites to HTTPS for a year. There's no preload either: that's the operator's decision.

### Headers

`SecurityHeadersMiddleware` sets these on every response that reaches it, including static assets, 404, 429, redirects and the error page:

```text
Content-Security-Policy: default-src 'self'; script-src 'self' 'nonce-{n}'; style-src 'self'; img-src 'self';
                         connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'
X-Frame-Options: DENY
X-Content-Type-Options: nosniff
Referrer-Policy: same-origin
```

- **`script-src`:** `'self'` covers `blazor.web.js`, the reconnect module and `shell.js`. The nonce is for the inline import map `<ImportMap />` renders. An inline script of your own needs `nonce="@SecurityHeadersMiddleware.NonceOf(HttpContext)"`, which only works in `App.razor`, because an interactive component has no `HttpContext`. `strict-dynamic` isn't an option: it turns off `'self'`.
- **`connect-src 'self'`** covers the SignalR WebSocket: under CSP 3 `'self'` matches `wss:` on the same origin. Development adds `ws://localhost:*` and `wss://localhost:*`, for `dotnet watch`.
- **`style-src 'self'`** blocks `<style>` blocks and `style=` attributes. CSS isolation and `.style.x = ...` from a script still work. See [the constraint](#constraints-for-later-issues) for `Virtualize`.
- **`default-src 'self'`** also covers fonts, so they have to be self-hosted.
- **`base-uri 'self'`** rather than `'none'`, because `App.razor` has a `<base>`.
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
| POSTs to anything marked `[EnableRateLimiting(RateLimits.Authentication)]` | `AuthenticationRequestsPerMinute` per IP address, on top of the above. A static page takes GET and POST on one endpoint, and showing a form isn't an attempt, so only POSTs count. A rejection has a short body, because a person sent it |
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

The sign-in account is in the request body, and a partitioner can't read that. The account limit on sign-in is therefore FR-AUTH-04's [lockout](#lockout), which also survives a restart.

### Circuits

Negotiating and connecting are HTTP, so they're limited like anything else. After that, **hub traffic never reaches the limiter**, and that's fine for NFR-SEC-07, which covers authentication and API endpoints, because both stay on HTTP: only HTTP can set a cookie, and [the API](#the-api) is `Endpoints/`.

The gap is a secret checked on a circuit, such as a TOTP code for a sensitive action. Nothing at the HTTP edge limits those attempts, so each needs a per-account throttle behind its module contract, and a refusal is a typed result rather than a 429. `ITwoFactor.CheckAsync` has one, with `Throttled` as its refusal: see [Two-factor](#two-factor). Don't limit inside `CircuitHandler`: inbound activity includes render acknowledgements, and refusing one breaks the circuit.

The WebSocket `Origin` is checked against `App:PublicBaseUrl`, because CORS doesn't cover WebSockets and SameSite doesn't stop a sibling subdomain. The check is skipped when a client sends no `Origin`: only a browser can carry someone else's cookies, and a browser always sends one.

### OWASP

Most of what NFR-SEC-03 protects doesn't exist yet, so this is what each risk relies on, and who finishes it. `BannedApis` scans compiled code, including Razor, lambdas and async methods, and `BannedApiGuardTests` proves it against broken fixtures.

| Risk | What covers it | Test or owner |
|---|---|---|
| Injection | EF Core parameterises. Raw SQL APIs are banned, and the interpolated forms parameterise | `BannedApis` |
| Cross-site scripting | Razor encodes. `MarkupString` is banned, because AI output and user input reach the UI. The CSP has no `unsafe-inline` or `unsafe-eval` | `BannedApis`, `SecurityHeaderTests` |
| Cross-site request forgery | Antiforgery, SameSite and `form-action 'self'`. Every state-changing endpoint must require a token, proved by posting without one, and `DisableAntiforgery` is banned. The WebSocket `Origin` check covers the hub | `CsrfTests`, `BannedApis`, `WebSocketTests` |
| Server-side request forgery | Only the adapters may use `HttpClient`, only `Platform/Email` may use an SMTP client, and base URLs and the SMTP server come only from the configuration file (NFR-SEC-10) | `BannedApis`, then #17, #18 |
| Broken access control | Pages are [private by default](#access-control). Ownership checks go behind module contracts, which HTTP can't enforce on a circuit | `AccessTests`, then #55 |
| Authentication failures | [Lockout](#lockout), [password hashing](#passwords), [sessions](#sessions), single-use [reset links](#verification-and-resets), and [two-factor](#two-factor) | `LockoutTests`, `PasswordHasherTests`, `SessionTests`, `CircuitAccessTests`, `PasswordResetTests`, `TwoFactorSignInTests`, `TwoFactorCheckTests` |
| Cryptographic failures | HTTPS, HSTS, Secure cookies. [Secrets](#secrets) and Data Protection keys are encrypted at rest with AES-256-GCM | `HttpsTests`, `CookieTests`, `SecretStoreTests`, `KeyRotationTests`, `DataProtectionKeyTests` |
| Security misconfiguration | Startup refuses a missing or `http` public address, and validates these settings. `AllowedHosts` is the operator's to set | `WebSecuritySettingsTests`, #57 |
| Vulnerable components | The dependency scan (NFR-SEC-09) | CI |
| Logging failures | [Logging](#logging) and masking, and the [audit trail](#audit) | `CanaryScanTests`, `AuditTrailTests`, `ClientOriginTests` |

`DatabaseHealthCheck` uses the interpolated form so that the ban has no exceptions.

## Accounts

`Modules/Identity` owns users, passwords, legal acceptance, [preferences](#preferences) and who counts as an administrator. It also registers cookie authentication and the authorization policies. The Web UI reaches all of it through `Modules/Identity/Contracts`, which return typed results, because Identity can't use `Strings`.

### Forms that take a secret

**A form that takes a password or other secret is a static page that posts to itself.** That's sign-in and its [two-factor step](#two-factor), registration, changing a password, the bootstrap secret, setting up and turning off two-factor, and the pages behind [emailed and issued links](#verification-and-resets), whose token is a secret too. The other ways of hosting one all lose something:

| Approach | Why not |
|---|---|
| An interactive `EditForm` | The password crosses the circuit, there's no HTTP response to set a cookie on, and hub traffic never reaches the limiter |
| A plain form on an interactive page | The first interactive render empties the prerendered page, which clears whatever the user has typed |
| A static page that posts to `Endpoints/` | Showing an error means redirecting, so the address and the other fields need a second channel: a query string would put them in proxy logs |

To add one:

1. Mark the page `[ExcludeFromInteractiveRouting]`, which `App.razor` reads to leave it static, and `[EnableRateLimiting(RateLimits.Authentication)]`.
2. Use `EditForm` with a `FormName`, without `Enhance`, so the post is a real browser POST. It renders the token and the framework checks it.
3. **Write secret fields as a raw `<input type="password" name="Input.Password">`.** `InputText` writes its value into the page, so it would send the password back after a refusal. The framework's own sample does this, so don't copy it.
4. In the handler, call the contract, then `HttpContext.SignInAsync`, then `NavigateTo`, then `return`. `BlazorDisableThrowNavigationException` is on, so execution carries on past `NavigateTo`.
5. Link to it with a plain `<a>`. From a circuit, `NavigateTo` needs `forceLoad: true`, or it renders Not Found.

Use a plain `<select>` rather than `InputSelect`. A generic component passes its attributes through a helper, and `UserFacingText` then reads them as text.

**Sign-out is the exception.** The interactive menu submits it, so it's `POST /sign-out` in `Endpoints/`. An endpoint like that must bind `[FromForm]` or `IFormCollection`: `UseAntiforgery` only records its result, and the form reader is what refuses a bad token. Without a form parameter, the endpoint passes the metadata check and enforces nothing, which is why `CsrfTests` also posts without a token.

The framework appends encrypted state to every page, so a search of the HTML can't prove that a secret isn't there. It holds only the antiforgery token and a service registry, and `SignInTests` decrypts it to check. Don't put `[PersistentState]` on a form model.

### Access control

**Every page is private by default**, through `@attribute [Authorize]` in `Components/_Imports.razor`. One attribute covers both paths: it becomes endpoint metadata that `UseAuthorization` reads on HTTP, and it's what `AuthorizeRouteView` reads in a circuit. A public page adds `[AllowAnonymous]`, and `AccessTests` holds the list of those. On a component that isn't a page, the attribute does nothing.

There's no `FallbackPolicy`. It never reaches a circuit, because `AuthorizeRouteView` treats a page without attributes as public. It also applies to requests with no endpoint, so unknown URLs would redirect rather than 404, and it would need exceptions for assets, health and the hub.

- **Never chain `.AllowAnonymous()` on `MapRazorComponents`.** That builder's conventions reach every page, and the hub, so it would make all of them public. `.RequireAuthorization()` there would do the reverse, and lock visitors out of the hub the landing page needs.
- **An endpoint mapped in code gets no attribute**, so it has to say `.AllowAnonymous()` or `.RequireAuthorization()`. `AccessTests` fails on one that doesn't. Assets and health are anonymous this way.
- **Attributes on one page combine as AND.** A page can't relax the default policy by naming another, so a page reachable without it has to be `[AllowAnonymous]`, and check `User` itself. `/legal/accept` does this, because it exists to satisfy that policy.
- **`AuthorizeView` applies the default policy too**, so a user with a document to accept counts as not authorized. Use `AccountPolicies.SignedIn` for what such a user still needs, as the menu does for sign-out.

The two paths need one rule each for a failure. `AccountAuthorizationResultHandler` covers HTTP and `Components/Account/AccessGate.razor` covers a circuit: a visitor goes to sign-in, a user with a document to accept goes to accept it, and anyone else gets the status page. A challenge is a 302, so a 401 never renders. A 403 renders the not-found page's text under its own status, so the status still shows that the page exists, as a visitor's 302 does.

### Lockout

`users.failed_sign_ins` and `users.locked_until` hold the state, so that a lock survives a restart. An attempt is **charged before the hash runs, in one `UPDATE`**, guarded by the lock. A row lock queues concurrent attempts, and each then sees the last one's result, so a lock window allows `MaxFailedSignIns` verifications however they arrive. Checking first and charging after would let every parallel guess through a slow hash.

An unknown address, a malformed one, a wrong password and a locked account return one message, and each costs one hash, through `IPasswordHasher.SpendAsync` where there's nothing to verify. Each also writes an [audit record](#audit), but a wrong password commits an `UPDATE` too, which an unknown address skips. A hash this slow should hide that, but nothing measures it. A lock isn't extended by further attempts, and a correct password doesn't sign in during one.

**An attacker can keep an account locked**, which is the accepted cost of FR-AUTH-04 for now. Five wrong passwords whenever a lock ends is enough, and the owner only gets in during the gap, so one address at the 10-a-minute limit can hold about 30 accounts. With email off, that includes an administrator, who is the only way to reset a password. Counting failures per account and client network would close it, because an attacker's failures then wouldn't lock out the owner's network.

A refusal logs at Information, because at Warning anonymous visitors could write a database row per attempt. The audit record is that row anyway, because FR-AUD-01 asks for it, and the sign-in [rate limit](#rate-limiting) bounds how fast anyone can add them.

### Sessions

The cookie says which session it belongs to, and a row in `sessions` says whether that session is still allowed. **Revoking deletes the row**, because the [audit trail](#audit) keeps the history, not this table.

| Limit | Where it comes from | What moves it |
|---|---|---|
| Idle | `last_seen_at` + `Security:SessionIdleTimeoutMinutes` | An HTTP request or a user action, written at most every minute, or a tenth of the timeout if that's shorter |
| Absolute | `expires_at`, set at sign-in from `Security:SessionAbsoluteTimeoutHours` | Nothing |

**The cookie doesn't slide.** Its ticket lasts the absolute timeout, so it's only a backstop. A circuit can't reissue a cookie, so a sliding one would sign out someone working in a page. A cookie with no session claim is refused, so anything from before this table signs in again.

**HTTP checks the row on every request with a cookie**, through `OnValidatePrincipal`, so ending a session applies to the next request. A hub request is checked but doesn't count as activity, because long polling makes one per poll, which would keep an idle tab alive. The exception handler runs authentication again for the error page, and a lookup that just failed would fail again and leave an empty 500. So that pass rejects the principal for this request only, and leaves the cookie, because the session may be fine.

#### Ending a circuit

A circuit never passes through HTTP, and the framework has no public way to end one. `ConnectCircuit` attaches any connection that holds the circuit's secret, then gives the circuit that connection's user **without re-initializing its components**, so a page would keep the previous user's data and the IDs it captured. Closing a revoked session's connection therefore isn't enough on its own: someone who kept the secret could reconnect on another session and carry on working as the first user, or attach a session to a circuit that an anonymous connection started, where revoking the session wouldn't reach that first connection.

So **a circuit can only be rejoined by the session that started it, and only while that session lives. Everyone else gets the `false` that makes a client reload the page.** A circuit's identity is then fixed for its whole life, so every connection that can drive a signed-in circuit carries that session's claim, and closing those ends all access.

`SessionHubFilter` enforces this, and `SessionConnections` holds each session's connections and circuit secrets:

| Hub event | What happens |
|---|---|
| Connected | The connection is added and then the session is checked, which closes the race with a revocation that ran after the cookie check |
| `StartCircuit`, `ResumeCircuit` | The returned secret is recorded against the session |
| `ConnectCircuit` | Refused unless this connection's session owns that secret |
| `BeginInvokeDotNetFromJS`, `OnLocationChanged` | Counted as activity and checked, at the same throttle. A dead session is ended and the call refused |
| Anything else, including render acknowledgements | Passes through |

Ending a session deletes the row **first**, then closes its connections, so that a client reconnecting at once finds its cookie refused. In a browser, the connection closes, the client reconnects, `ConnectCircuit` says no, and the page reloads into sign-in. The framework evicts the circuit three minutes later.

- **Nothing throws to refuse.** A throw from a hub filter or a `CircuitHandler` logs an Error, and from `OnConnectedAsync` it also skips `OnDisconnectedAsync`. An ended session is routine, so it mustn't write a `log_events` row. A failed check closes that one connection and logs a Warning, because the session may be fine and the client can rejoin.
- **Don't gate on a render.** A client can stop acknowledging renders, and the renderer then stops at `MaxBufferedUnacknowledgedRenderBatches`, so nothing that relies on a re-render reaches it, while its events still dispatch. That's why this holds at the door rather than in a component.
- **The method names aren't public API.** `HubMethodName` is the resolved method's name, so a client changing the case can't get past a check. `CircuitAccessTests` fails if an upgrade renames one or changes its signature, so read `SessionHubFilter` against the new `ComponentHub` then.
- **Visitors aren't tracked,** so they can't grow the map, and an unknown secret is refused. A visitor's page reloads after a dropped connection rather than resuming. Those pages hold no input, so nothing is lost.
- **Secrets stay until the session ends,** which the absolute timeout bounds. Forgetting them is safe, for the same reason.

`SessionExpiryJob` deletes expired rows and closes their connections every 5 minutes. Nothing depends on it for a session to stop working: it moves tabs nobody is touching along, and keeps the table small.

#### Idle means a user action

A tab showing live data with nobody touching it times out, because acknowledging a render isn't something a person did. Someone watching a dashboard for an hour without clicking signs in again, and their agents carry on regardless. `BeginInvokeDotNetFromJS` also carries JS interop, so a component that calls .NET from a timer would count as activity.

#### Changing a password

`/account/password` is a [static page](#forms-that-take-a-secret), so the password never crosses a circuit. A guess is charged like a sign-in, so it counts towards the same [lockout](#lockout), and a locked account gets the answer a wrong password gets. Length is checked first, so a bad new password costs no hash and no attempt. The current session stays, as FR-AUTH-08 says.

**A sign-in racing a change can't survive it.** Verifying takes long enough for a change to finish meanwhile, which would leave a session the change never saw. So the session is inserted by one statement, conditional on the hash that was verified, `FOR SHARE`, and the change updates the hash before deleting sessions. The explicit lock matters: the one the foreign key takes doesn't wait for an update to the hash.

#### What this leaves

- After a revocation, a circuit's memory lasts up to three minutes, with nothing able to reach it.
- A silent tab can sit past its session until the next job run, showing what it last rendered.
- Signing out from a tab in that window gets a 400, because its token is bound to a user the request no longer has. `SignOutAsync` already ran during validation, so the cookie still goes.

### Registration

**Registering never signs in**, and it gets the same answer whether or not the address is taken, so the answer alone doesn't show which it was. Someone who registers again with a new password gets a failed sign-in, which is the cost of that.

This raises the price of a lookup and doesn't remove it. Registering an address with a fresh password and then signing in with it shows whether the address was free: a success also leaves the attacker holding that address. It's an accepted limit, because unverified accounts can sign in, even with email on: see [Verification and resets](#verification-and-resets). The address's owner can take the account back with a reset.

It inserts with `ON CONFLICT DO NOTHING` rather than catching an exception, because EF logs a failed save as an Error, and a taken address would then write a database row for anyone who asks. This also settles a race between two registrations.

Refusals that depend only on what was submitted, such as a short password or a blocked country, are shown directly. `EmailAddresses.IsValid` refuses anything further down can't take, because a failure there is a 500 and a database log row that a visitor chose to write. For the same reason, a query parameter that a page reads is a string: binding one to a `bool` makes any other value a 500.

The country field appears only when `Legal:BlockedCountries` isn't empty. The answer is checked and not stored (NFR-LEG-03), so a country blocked later doesn't affect existing users. `Modules/Identity/Countries.resx` is the one list, for the form and for validating the setting: its codes are ISO 3166-1 alpha-2, plus `XK` for Kosovo.

### Legal documents

A document's version is a constant in `LegalDocuments`, in the same build as its text in `Strings.resx`, so a user can't be shown one and recorded against another. `legal_acceptances` only gains rows, and each is the history of what a user agreed to and when.

The default policy carries the requirement, so plain `[Authorize]` enforces NFR-LEG-01 on HTTP and in a circuit, with no database call per navigation: it compares a claim, set at sign-in, with the compiled value. A new version needs a new build, so a restart ends every circuit, and the next request fails and goes to accept. Forms carry the versions they showed, so a deployment can't record one nobody saw.

**A material change needs a new version, and a typo doesn't.** Only a person can tell which, so `LegalDocumentTests` pins the rendered words of each document to its version, and any edit stops there until someone decides.

### Administrators

An administrator is an account whose verified address is in `Admin:AdminEmails` (FR-AUTH-10). **Nothing stores the answer**, as a column or a claim, because a cookie outlives a change to the configuration. `IAdministrators` works it out from the address and `email_verified_at`, and the `Administrator` policy calls it on each check, so removing an address and restarting takes effect at once. A circuit bypasses middleware, so check behind contracts too (#47).

With email off, nothing can verify an address, yet FR-AUTH-06 needs an administrator to issue resets. Treating a listed address as verified would give the rights to whoever registers it first, and those rights can take over any account. So the proof comes from the one channel only the operator controls: `Admin:BootstrapSecret`, entered at `/account/administrator` by a signed-in user with a listed address, sets `email_verified_at`. A stranger who registers a listed address stays an ordinary account, though they hold that address until #47, so the operator lists another. Someone can tell whether an address they've registered is listed, because the account page offers them the link.

With email on, the secret does nothing. The emailed link verifies the address instead, so a listed address becomes an administrator once its owner has used one: see [Verification and resets](#verification-and-resets).

### Verification and resets

With email on, a link proves that a user reads their address (FR-AUTH-03), and a link resets a forgotten password (FR-AUTH-06). With it off, nothing is verified, and an administrator issues reset links to hand over out of band (EXT-EML-01).

`account_tokens` holds one live link of each kind per account: the key is the user and the purpose. A link carries 32 random bytes in base64url, and only their SHA-256 is stored, so the table can't be read for links. Using a link deletes its row, in the transaction that makes the change it allows, so it works once.

| Link | Lasts | Comes from |
|---|---|---|
| Verify the address | 24 hours | Registering, or the button on `/account`, with email on |
| Reset, emailed | 1 hour | `/forgot-password`, with email on |
| Reset, from an administrator | 24 hours, because handing it over takes a while | `/admin/password-reset`, with email off |

The SRS asks for links that are time-limited, not configurable, so these are constants in `AccountTokens`.

- **A link verifies only for someone signed in to its account.** `/account/verify-email` is private, so a visitor signs in and comes back through `returnUrl`. Otherwise a stranger who registered someone's address first would get a verified account, and an administrator's if it's listed, the moment its owner clicked the link. That was the risk #9 left, and requiring the session means the person who set the password is the one who verifies. A link for another account is refused and left alone, so it still works for its own.
- **Unverified accounts can sign in,** with email on as well as off. They can't become administrators, and #39 keeps them from Live. Turning email on later locks nobody out: each user verifies when they choose. The cost is the lookup in [Registration](#registration), which stays open.
- **An emailed reset link verifies the address too.** Only the address's reader could have used it, and resetting replaces the password and ends every session, so whoever held the old password is out. That's how an address's owner takes back an account that a stranger registered with it. **An administrator's link never verifies,** because it proves nothing about the inbox. If it did, a stranger who registered a listed address could ask an administrator for a reset and become one.
- **A reset ends every session and clears any lock.** It replaces the hash before deleting sessions, the same order as [changing a password](#changing-a-password), so a sign-in racing it can't keep a session. A password change deletes any outstanding reset link, so a link asked for earlier can't undo it, and any sign-in waiting for its [two-factor code](#signing-in).
- **Opening a link changes nothing.** Each page shows a form, and only its POST uses the token, so a mail scanner that fetches the link can't spend it. The forms post back to their own address, so the token travels in the query string rather than a hidden field.
- **Links start with `App:PublicBaseUrl`,** never the request's `Host`, which a client chooses and could point at a site of its own. It's required when email is on. Without it, which only happens in Development with email off, an administrator's link is shown as a path. The token is in the query as `token=`, so the masking rules see it, and they also match `token%3D` in a `returnUrl`.
- **One account gets at most one email of each kind every 5 minutes, whoever asks.** The limiter only sees the sender's address, so without this anyone could fill someone's inbox. A request inside the window sends nothing and keeps the earlier link working. The throttle is a condition on the upsert that issues the link, so two requests at once can't both pass it. An administrator's link isn't throttled.
- **Asking for a reset gives one answer, whether or not the address has an account.** Sending runs in the background, so the slow part takes no time in either case. An unknown address still does less work, because it writes no row, which the [lockout](#lockout) accepts for sign-in too.
- **An administrator's link is shown once, on a static page,** so it never crosses a circuit. `IPasswordResets.IssueAsync` checks the administrator again, and refuses when email is on, because users reset their own then.

### Two-factor

A user can turn on TOTP two-factor (FR-AUTH-05). Once it's on, signing in needs a code from their authenticator app, and so does every sensitive action: adding or rotating exchange credentials, switching an agent to Live, the kill switch, and turning two-factor off. `ITwoFactor` holds all of it, and `/account/two-factor` is its [static page](#forms-that-take-a-secret).

| Table | Holds |
|---|---|
| `totp_authenticators` | One row per user: the key's ID in [`secrets`](#secrets), when a code confirmed it (null while setup is pending), the last time step used, and the throttle on checks |
| `recovery_codes` | The SHA-256 of each unused recovery code. Using one deletes it |
| `sign_in_challenges` | One sign-in per account waiting for its code: the SHA-256 of its token, and when it expires |

**The codes are RFC 6238 with fixed parameters:** HMAC-SHA1, 6 digits, a 30-second step, and a step either side of now for a phone's clock. Authenticator apps ignore other values without saying so, and then show codes that never work. The key is 20 random bytes, stored in base32 as a `SecretValue`, and only `TwoFactor` reveals it. `Totp` and `Base32` are this module's own, and `AuthenticatorTests` pins the test's separate implementation to the RFC vectors, so the application is checked against something that doesn't share its mistakes. The step comes from `TimeProvider`, which tests replace.

- **A code works once.** A code is accepted only for a step later than the last one used, and the update that moves `last_used_step` forward is conditional, so of two requests with one code only one gets through (RFC 6238 section 5.2).
- **Recovery codes are the way back** for someone who loses their phone, because the same check guards the kill switch. There are 10, each 50 random bits, shown once when two-factor goes on or the codes are replaced, and stored only as hashes. Each works once, anywhere a code from the app does, and its use is audited. A user who loses both the phone and the codes is locked out until [#47](#constraints-for-later-issues) decides on an administrator's reset.
- **A password reset leaves two-factor on.** Otherwise an emailed link, or an administrator's, would be enough to take over an account that has it.
- **Turning it on needs the password**, which counts towards the [lockout](#lockout), as well as a code from the new authenticator. Without the password, someone holding only a stolen session could put their own authenticator on the account, and the owner couldn't sign in again. It ends the other sessions, because none of them passed two-factor.
- **A setup that's never confirmed changes nothing.** Sign-in only asks for a code once one has confirmed the key. Starting again replaces the key and deletes the old one. To change phone, a user turns two-factor off and sets it up again.
- **There's no foreign key to `secrets`**, as for the Data Protection keys, and the boundary tests refuse `HasOne<Secret>()` anyway. `TwoFactor` deletes the key in the transaction that stops referring to it.

#### Signing in

**No principal is ever half signed in.** A right password for an account with two-factor creates a challenge, not a session, and the session starts only once a code follows. So a session cookie still means fully signed in, and nothing in the authorization policies changed. The alternative was a session with a flag that every policy, page and circuit would have had to respect.

1. `/sign-in` checks the password, then `SignInChallenges.IssueAsync` inserts the challenge conditional on the verified hash, `FOR SHARE`, the same way a [session starts](#changing-a-password). The token goes in the `ecnanib.sign-in` cookie: HttpOnly, SameSite=Strict, `Path=/sign-in`, lasting as long as the challenge, which is 5 minutes. Not the URL, which proxies log, and not a hidden field, which a phone loses when it reloads the tab after the user switches to their authenticator.
2. `/sign-in/two-factor` takes the code. A wrong one gets the same answer as a locked account. A right one clears the failure count, and then, in one transaction, it locks the `users` row by starting the session, uses the challenge, spends the code and records `SignedIn`. A password change locks that row first too, so the two queue rather than deadlock, and a change that commits first leaves no hash for the session to match. A change or reset also deletes the challenge, so a sign-in that began with the old password can't finish. Nothing in the transaction updates `users`, because it holds the row `FOR SHARE`, and two submissions at once would each wait for the other's.

**The code shares the password's lockout.** Each code is charged against `failed_sign_ins` before it's checked, exactly as a password is. For an account with two-factor, a right password doesn't clear the count, and only a right code does. Otherwise a right password between wrong codes would reset the count, and guessing would never stop.

One challenge per account means that a second sign-in, from another device, replaces the first's, which then has to start again. Only someone with the password can do that, and five attempts lock the account anyway.

#### The check for sensitive actions

`ITwoFactor.CheckAsync(userId, code)` is the check that later issues call, behind their own contracts, in the method that does the action and before changing anything. It returns `NotRequired` when two-factor is off, so a caller goes ahead when `Allows()` is true and needn't ask first. `IsEnabledAsync` tells a page whether to show a code field. Turning two-factor off and replacing recovery codes go through it too.

- **It has its own throttle**, because it runs on circuits, which the HTTP limiter never sees ([Circuits](#circuits)). `failed_checks` is charged before the code is checked, in one `UPDATE`, like a sign-in, and `MaxFailedSignIns` wrong codes pause checks for `LockoutMinutes`. A right code clears it.
- **It's separate from the sign-in lockout**, which anyone can trip with wrong passwords. Sharing it would let a stranger keep a signed-in owner from the kill switch.
- **A pause stops authenticator codes and not recovery codes.** Recovery codes aren't charged, because 50 random bits can't be guessed, so an owner who mistypes five codes in a hurry can still reach the kill switch with one.
- **A refused check isn't audited**, and logs at Information, because only a signed-in user can make one. The action it guarded isn't recorded either, because it didn't happen.

### Preferences

`IPreferences` holds a user's theme, animation level and display time zone (SRS 3.2), and which [notifications](#notification-email) they're emailed (FR-PREF-06). `user_preferences` has one row per user, written when they first save and deleted with the account by its foreign key. Without a row, `Ui:DefaultTheme` and `Ui:DefaultAnimation` apply, and both default to System. Visitors always get the System theme (FR-PREF-03), with the configured animation level. Saving isn't audited, because FR-AUD-01 doesn't list it.

**`App.razor` reads them once per full page load**, which is the only render that writes `<html>`, so the [theme](#theme-and-layout) and [motion level](#motion) are in the first response and never flash (AC-05). That's one query by primary key per page load. Navigating inside a circuit costs nothing here, though the menu's [unread count](#reading-them) reads again. The email choices aren't part of `UserPreferences`, so this read doesn't need them.

- **It renders nothing until they're read.** A component renders once at its first `await`, and that render would start `Routes`, and the page inside it, with the wrong time zone.
- **The error page renders as a visitor's**, whoever is signed in. The database may be why it's showing, and a failure while rendering it would leave an empty 500.
- **A change shows in another open tab only when that tab loads a page.** A circuit can't change `<html>`.

**`/account/preferences` is a static page**, though it takes no secret, because its result shows on `<html>`. The page has to load again anyway, and a post and redirect does that with no script, so the [form](#forms-that-take-a-secret) works like the others. It has the general rate limit, not the authentication one. The selects post strings, and only exact names count, as in `Ui`: `Enum.TryParse` would also take a number or a list.

**The email choices are only on the form with email on.** A browser posts nothing for a box that isn't ticked, so the form also posts a hidden `EmailShown`, which tells "none ticked" from "not shown". Without it, or with email off, `SaveAsync` leaves the stored choices alone. An administrator's types are only offered to administrators, and `SaveAsync` refuses them from anyone else.

#### Time zones

**Write every time through `DisplayTime`**, never with `UtcDateTime`. `Routes` cascades it, and `Format` converts a time to the user's zone and adds the offset, as in `2026-11-04 17:00 UTC+07:00`, from one format in `Strings.resx` (FR-PREF-05). The zone is the first of these that the host knows:

| Source | Where it comes from |
|---|---|
| The user's choice | `user_preferences.time_zone`, one of `TimeZones.Offered` |
| The browser's | The `ecnanib.time-zone` cookie, which `wwwroot/shell.js` writes on every page from `Intl.DateTimeFormat` |
| UTC | Neither |

- **The server can't see the browser's zone** until a script says, and a script runs after the first render. A cookie reaches the first render of the next page, so only the first page a browser ever loads shows UTC. Visitors see no times, and the sign-in page writes the cookie, so in practice nobody sees that. The cookie is written on every page, so it follows a laptop that travels.
- **`App` passes the zone's ID to `Routes` as a parameter.** A circuit has no `HttpContext` to read a cookie from, and a root component's parameters reach the circuit in the protected component descriptor. The cascaded value is fixed, because it only changes with a page load.
- **The cookie is untrusted.** On Unix, a zone lookup reads the file of that name in the system's zone directory, so `TimeZones.TryFind` refuses anything not shaped like an IANA ID before asking, including any dot. It accepts the aliases browsers report, such as Chromium's `Asia/Calcutta`, which aren't offered.
- **The zones come from the host:** its time zone data on Linux and macOS, and on Windows the IANA zone ICU maps each Windows one to. A host without the data offers only UTC: see [deployment](#constraints-for-later-issues).

### Passwords

Argon2id through NSec.Cryptography, which wraps libsodium, so its 64 MiB stays off the managed heap of the process that runs trading cycles. The parameters are m=64 MiB, t=3, p=1, and at most four hashes run at once, which caps the memory at 256 MiB. A verification took about 85 ms on an Apple Silicon laptop, so measure it on the real host before relying on that.

The queue for those four is unbounded, and sign-in and registration share it. The limit is per /64, so a /56 has 256 buckets, which at 10 a minute each is about 42 hashes a second. At 85 ms that's close to capacity, and a slower host would stall legitimate sign-ins, so measure it there too.

Hashes are PHC strings with their own parameters, so these can be raised later. **Pass bytes to `DeriveBytes`.** Its overload that takes a string hashes UTF-16, which no other Argon2 implementation would, and `PasswordHasherTests` checks against hashes made by another one.

## Credentials

`Modules/Credentials` holds users' own keys for the AI providers and for Binance Live and Demo (FR-KEY-01). The platform has no keys of its own (CON-11): only Credentials may build an `AiKey` or an `ExchangeKey`, which `BannedApis` enforces, so every key an adapter sees is a user's, and passed the checks below.

| Contract | What | Who uses it |
|---|---|---|
| `ICredentials` | Listing, adding, labelling, checking, replacing and deleting a user's keys. Nothing it returns holds a secret | The Web UI |
| `ICredentialKeys` | A valid key, decrypted for one use, refused on a market Binance didn't enable for it. Also every AI credential of one user, or of everyone, as IDs and statuses | Catalog, AiCouncil and Execution only, which `BannedApis` enforces |
| `ICredentialObserver` | Told after a change to a credential commits: added, rotated, checked with an answer, or deleted, with the status before and after. It must return at once | Catalog and Agents |

`credentials` has one row per key. The API key, and for Binance the secret, are rows in [`secrets`](#secrets), and the row keeps only their IDs, so listing never decrypts anything (FR-KEY-03). It shows the API key's last four characters, never the secret's. Every query filters on the user, so another user's credential behaves like one that doesn't exist (NFR-SEC-04). There's no foreign key to `users` or `secrets`, as for notifications and two-factor: #51 deletes through Identity's port.

### Statuses

FR-KEY-04's four, with a `problem` that says why a key isn't valid:

| Status | When |
|---|---|
| Valid | An AI key lists at least one model. A Binance key can trade Spot, Futures or both |
| Invalid | The provider refused the key. Or a check found a Binance key that can withdraw, which nothing then uses |
| Insufficient permissions | An AI provider answered 403, so it knows the key but won't let it list models, or the key can use no model that writes text. A Binance key can trade on neither market |
| Unverified | An AI key added while its provider couldn't be reached. Nothing uses it until a check says it's valid |

- **A refused key is never stored.** Neither is a Binance key with withdrawals enabled (AC-04), nor one Binance couldn't be asked about, so a key whose withdrawal permission was never read never reaches the database. An AI key has nothing like that to wait for, so one added during an outage is stored as Unverified.
- **An outage changes nothing.** A check that can't reach the provider leaves the status as it was, so a provider's bad hour can't downgrade a key, or pause its agents.
- **A key Binance didn't enable for a market is kept**, as Insufficient permissions when it can trade on neither, so the user can fix it on Binance and check again without pasting it twice.

### Validating a key

An AI key is checked with `IAiProviders.ListModelsAsync`, the lightweight call FR-KEY-04 suggests. The adapters read Gemini's and xAI's misleading 400s as `Authentication`, which means Invalid. Any other failure means the provider couldn't be reached.

A Binance key is checked with authenticated requests to its environment's servers, and its permissions are read where Binance exposes them (FR-KEY-02, FR-KEY-05):

| | Live | Demo |
|---|---|---|
| Restrictions | `GET /sapi/v1/account/apiRestrictions`: withdrawals, the IP restriction, Spot and Futures | Not asked, because Binance documents the wallet API only on `api.binance.com` |
| Spot | Enabled when the restrictions allow it and `/api/v3/account` says `canTrade` | Enabled when `/api/v3/account` says `canTrade` |
| Futures | Enabled when the restrictions allow it and `/fapi/v3/account` takes the key | Enabled when `/fapi/v3/account` takes the key |
| Refused | When Binance refuses the restrictions call | When both accounts refuse it |

- **-2015 covers a wrong key, an address the IP restriction leaves out, and a missing permission**, and Binance doesn't say which, so the page names all three.
- **A market counts only once a signed request to it has succeeded.** Binance says a key made before the Futures account was opened can't use the Futures API, whatever its restrictions say. This also settles whether Futures takes an Ed25519 key, which Binance's Futures pages don't document: if it doesn't, the key is recorded as not enabled there.
- **A Demo key's IP restriction isn't known**, so it gets no warning, and Demo holds no real funds. A Live key with no IP restriction is saved with a warning that lists `App:EgressIpAddresses`.
- **The Spot account's `uid` is stored**, and replacing a key must keep it, because agents' orders and positions live on that account. A key for another account is added as a credential of its own. When Binance didn't say which account a new key is for, it's refused too.
- **A Binance key's secret is HMAC or an Ed25519 private key** (FR-KEY-06). A shape check keeps out what plainly isn't either before anything is sent, and the adapter's signer refuses a PEM it can't import, which counts as Invalid.

### Adding and replacing

`/credentials/add` and `/credentials/{id}` are [static pages](#forms-that-take-a-secret), so keys never cross a circuit. Each kind of key has its own form, so no script decides which fields to show. A Binance key's secret goes in a password field for HMAC, or a text area for an Ed25519 PEM, and whichever is filled in decides the key type.

- **For a Binance key, the two-factor check comes first** (FR-AUTH-05). It's inside the contract, before Binance hears of the key, so a stolen session can't use the server to try keys. A code is spent even when Binance then refuses the key, which the next code covers.
- **A new key commits in one save:** its secrets, its row and the `CredentialAdded` record.
- **Replacing a key is conditional on the one it replaces**, in one transaction with deleting the old secrets and recording `CredentialRotated`. Of two replacements at once, or a replacement and a deletion, one wins, and the other changes nothing and leaves no secret behind.
- **The label is user text**, at most 60 characters, without control characters or half a surrogate pair. Pages show it only as text. Changing it isn't audited, because FR-AUD-01 doesn't list it, as it doesn't list preferences.

### Checking on demand

`/credentials` is interactive, because checking and deleting take no secret. Deleting asks first, on the page, and the question names the agents that use the key and says the active ones will pause (FR-KEY-10), from `IAgents.ListUsingCredentialAsync`.

A check claims the credential with one conditional `UPDATE` of `checked_at`, so each key is checked at most once a minute, however many tabs ask. Hub traffic never reaches the limiter, and every user shares the server's Binance weight, so the limit has to live behind the contract ([Circuits](#circuits)). A check's result is written only while the key it checked is still the credential's, so a replacement meanwhile keeps its own result. Each check that got an answer records `CredentialValidated`, with the status before and after.

### Checking on a schedule

`credential-revalidation` checks every key again every `Security:CredentialRevalidationHours`, 24 by default (FR-KEY-11). It and checks on demand share `CredentialChecks`, so they claim, write and record a check the same way, and a key a user checked a moment ago is skipped.

- **Four keys at a time, each on its own.** One key's failure is logged and counted, and never reaches another (NFR-REL-05). Once shutdown begins, nothing new starts, and the checks under way finish, because they can be exchange calls.
- **A check that loses something raises `CredentialInvalid`**, in the transaction that writes it: a key that was `Valid` and isn't, or a Binance market that was enabled and isn't. On demand too, because agents pause either way. It links to `/credentials`, and its subject is the key's label.
- **The observers hear of every check that got an answer**, and Agents pauses what it must: see [Pausing](#pausing-on-a-credential).
- **The record is by the system** on the schedule, with the owner as `UserId`, so the user's activity page shows it as by Ecnanib.
- **`details` holds counts**: keys, checked, unreachable, too soon, gone and failed.

`CredentialObservers` resolves the observers on the first change rather than taking them in its constructor. An observer uses the contracts it observes, as Agents reads `ICredentials`, so injecting them would be a cycle, and startup validates the container.

### Using a key

`ICredentialKeys.OpenAiKeyAsync` and `OpenExchangeKeyAsync` return a key only for the user's own `Valid` credential, and a Binance key only for a market it's enabled for, which is FR-KEY-05's last rule. They reveal its secrets through `ISecretStore` at that moment, and the caller disposes them (FR-KEY-09). Each use moves `last_used_at`, at most once a minute, with a conditional `UPDATE`, so a busy key doesn't write on every call. Validating isn't a use, and neither is listing a key's models for the catalog, which `OpenAiKeyToListModelsAsync` does with the same checks and no stamp. Otherwise every key would look used each sync.

`ListAiAsync` takes a user, and `ListAllAiAsync` lists everyone's. They're two methods so that no null can widen a user's query to everyone (NFR-SEC-04).

Credentials tells `ICredentialObserver`s after each change commits, never before, so an observer can't hear of one that rolled back. A refused key, and a check that couldn't reach the provider, change nothing and raise nothing. An observer that throws is logged, and neither undoes the change nor stops the others.

### Configuration

`App:EgressIpAddresses` lists the addresses the server's requests come from (SRS 7.2), which the pages tell users to put on a Binance key's IP restriction. It's optional, and the pages tell users to ask the operator when it's empty. Each entry is one IPv4 or IPv6 address, with no range or port, or startup stops naming it (CFG-03).

### Logging

Outcomes are logged under the user's and the credential's IDs, with the provider, status and problem, at Information: a refused key is something a user did, not a fault. The adapters log each call. Nothing logs a key, a secret, a label or the last four characters, and `CredentialLoggingTests` checks the logs and the audit trail with canary keys through the real pages.

## Model catalog

`Modules/Catalog` holds the models users' keys can use (FR-CAT-01), merged from every user's valid AI keys, with which key can use which model (FR-CAT-02). The platform has no keys of its own (CON-11), and what a key can use depends on its account, so asking with users' keys is the only way to know.

| Contract | What | Who uses it |
|---|---|---|
| `ICatalog` | The models, hidden ones included, with the configuration's metadata and administrators' overrides applied, the providers, what a user's keys can use, and each role's default provider and model | The Web UI, including the catalog pages, and Agents' role assignments. The council (#31) later |
| `ICatalogSync` | A user's sync of their own keys (FR-CAT-03) | The Web UI |
| `ICatalogAdministration` | A sync of every user's keys, the sync runs, a model's layers of metadata, and administrators' edits to models and role defaults, each audited | **Administration only**, which `BannedApis` enforces, because it doesn't check who's asking. `ICatalogControls` does. See [Administration](#administration) |
| `IModelStatusObserver` | Told after a sync commits changes to models' statuses (FR-CAT-04). An event Catalog raises ([Inversions](#inversions)) | Agents |

### Tables

| Table | What |
|---|---|
| `models` | One row per provider and model ID, with what providers reported: name, family, release date, context window, maximum output, the five capabilities as nullable booleans, status, first and last seen, and `missing_since`, when no key could use it any more |
| `model_availability` | One row per key and model: whether the key could use it at its last sync, and when that sync began asking. It holds `user_id`, so a user's rows are found without asking Credentials |
| `catalog_sync_runs` | One row per provider per run (FR-CAT-08): trigger, start and end, the keys asked, the keys that failed, models added, updated, deprecated and retired, and errors |
| `model_overrides` | Administrators' overrides for one model (FR-CAT-05): display name, `hidden`, the five capabilities, both prices, and `recommended_roles` as role names. A null field inherits. A model with no overrides has no row. Its foreign key to `models` cascades |
| `role_defaults` | One row per role that has a default (FR-ROLE-03): a provider, with a foreign key to `providers`, and optionally a model ID, which may name a model nobody's key has listed yet. A row with no provider is an administrator's "no default" |

- **A model's key is its provider and ID**, so a role assignment (#24) can name one as the SRS does. `models.provider` has a foreign key to `providers`, which is inside the module, so the seeder now seeds from `IAiProviders.All` and a test provider gets its row too.
- **There's no foreign key to `credentials` or `users`.** A sync removes a deleted key's rows, and account deletion (#51) removes a user's by `user_id`.
- **`role_defaults` is seeded from the `RoleDefaults` section, once per role** (NFR-DEP-02). `RoleDefaultSeeder` adds a row only for a configured role that has none, as the seeder contract requires, so administrators' edits survive a restart and a change to the section reaches only a new role. That's why "no default" keeps the row with a null provider rather than deleting it: a deleted row would be seeded again. It lives in Catalog rather than Agents because its providers are checked against `IAiProviders.All` at startup (CFG-03), which Agents can't use, and its rows refer to `providers`. FR-ADM-03 puts it beside the catalog too.
- **`model_overrides` is apart from `models`** because every sync copies what providers report onto `models`. Two check constraints keep the rows sane: both prices or neither, and no model ID in `role_defaults` without a provider.
- **`errors` holds `{credentialId, failure, statusCode}` and nothing else**, for the reason `job_runs.details` holds no exception text: a provider's message can quote a request, and administrators will read this table. The run's `correlation_id` finds its log events.
- **A run's row is inserted before anything is asked**, and closed in the transaction that writes its results, so its counts always match what changed. A row with no `finished_at` was cut short, and its `job_runs` row says how.

### Metadata

Providers report different things ([The four providers](#the-four-providers)), and `Catalog:Overrides` fills the rest (FR-CAT-06, ASM-04). **Configured metadata isn't stored.** `ICatalog` applies it field by field as it reads, so a setting beats the provider, takes effect with a restart (CFG-04), and leaves "updated" meaning what a provider changed. **Administrators' overrides are a third layer above both** (FR-CAT-05), stored in `model_overrides`, so they persist across syncs. They cover the display name, the capabilities and the price, which `Catalog.Effective` applies administrator first, then configuration, then provider. Visibility and recommended roles are theirs alone, because neither providers nor the configuration say anything about them. The family, context window and maximum output stay with the configuration and the provider.

- **An override names one exact model**, because a prefix such as `gpt-5` would also match `gpt-5-mini` and give it the wrong price. A `*` is refused rather than ignored, and so is a field the section doesn't have, because a misspelt price would otherwise set nothing without a word.
- **`CatalogRules` holds the rules both layers share**: text of at most 100 characters with no control characters, a price from 0 to 1,000,000, and one exact model ID of at most 256 characters with no whitespace. The section adds its own trim and refuses a wildcard.
- **Prices are decimal US dollars per million tokens**, as `ModelPrice`. It matches the adapters' `AiModelPrice`, but it's Catalog's own, because the Web UI can't use AiAdapters.
- **A null capability means nobody said.** OpenAI reports none, so every OpenAI model is null for all five until the configuration sets them.

### Syncing

Every trigger runs one pipeline over a set of keys. Each enabled provider runs on its own, so a slow one can't hold up another's write:

1. **Insert the provider's run row.**
2. **Fetch each key's list, with no lock or transaction held**, four keys at a time.
   - Open the key with `ICredentialKeys.OpenAiKeyToListModelsAsync`, which doesn't count as a use, and dispose its secret after.
   - A key gets 3 minutes, retries and pages included, so a provider that hangs costs each key that much rather than holding the run until its timeout.
   - A key ends with its models, a failure (the `AiException`'s category and status, or `Other` for anything else), or as unusable (`CredentialUnusableException`). One key's failure never reaches another (NFR-REL-05).
3. **Write, in one transaction, under an in-process lock** (ASM-06):
   - **Read the keys again**, inside the lock, and record availability only for keys still `Valid`, so a key deleted or invalidated while its list was fetched doesn't get its rows back. The observer's clean-up takes the same lock.
   - **Merge** every list. Each field takes the first non-null value, in credential-ID order, and a null never overwrites a stored value.
   - **Newer results win.** `checked_at` is when a sync began asking, and a key whose stored `checked_at` is later than this fetch's start is left alone, however late this one writes.
   - **A key that listed** has its models available and its others not. **An unusable key's** rows are deleted when it's gone, and marked unavailable when it isn't valid. **A key that failed keeps what it had**, as Credentials keeps a key's status in an outage.
   - **A sync of everyone's keys also tidies:** rows of a key that's gone are deleted, and those of a key that isn't valid are marked unavailable. That's the recovery path for lost events, which [Inversions](#inversions) requires.
   - **A sync of everyone's keys also ages** the models no key can use any more. See [Deprecation and retirement](#deprecation-and-retirement).
   - **Close the run row.**
   - **After the commit, tell every `IModelStatusObserver`** about the provider's changes of status, if there were any.
4. **Notify administrators**, after a scheduled or administrator's run, when the provider had keys and every one failed on the provider's side: a timeout, an overload, or `Other` that isn't a 4XX, which covers a server error and an answer the adapter can't read. A refused key, a rate limit or a key with no money left is one user's problem, so it never alerts anybody. The notice links to `/admin/catalog`.

Shutdown stops the fetching, through `context.Stopping`, because an AI call is safe to abandon and nothing is written until the end. The write takes the run's own token, so results already fetched aren't dropped just because shutdown began.

### Triggers

| Trigger | Job | Keys | Timeout |
|---|---|---|---|
| `Schedule`, every `Catalog:SyncIntervalHours` | `catalog-sync` | Everyone's, then a tidy | 30 minutes |
| `Administrator`, through `ICatalogAdministration.SyncAllAsync`, which doesn't wait | `catalog-sync-all` | Everyone's, then a tidy | 30 minutes |
| `Credential`, through `ICredentialObserver` | `catalog-sync-credential` | One | 5 minutes |
| `User`, through `ICatalogSync.SyncUserAsync`, which waits so the page can say how it went | `catalog-sync-user` | The user's | 5 minutes |

- **Only the schedule runs as `catalog-sync`**, so nothing on demand moves it ([Schedules](#schedules)).
- **The schedule and an administrator never overlap.** The run takes the guard itself, with `Wait(0)`, and records `Already running` when it's held. `SyncAllAsync` reads the guard only to choose its answer, so a run the runner refuses (`NotStarted`) can't leave it held. Other runs may overlap one, because the write lock and newer-wins keep them apart.
- **A user syncs at most once a minute**, held in memory, because hub traffic never reaches the rate limiter ([Circuits](#circuits)). A user with no valid key is told so without using up the minute.
- **The observer acts only when the catalog changes:**

  | Change | Action |
  |---|---|
  | Added or rotated, and `Valid` | Sync the key |
  | Checked, and `Valid` when it wasn't | Sync the key |
  | Checked, and not `Valid` when it was, or rotated to a key that isn't `Valid` | Mark its rows unavailable |
  | Deleted | Delete its rows |
  | Anything else, such as a check that leaves a key valid | Nothing. The check has just listed the models to validate the key, so they aren't listed again |

  It starts a job and returns, so the request that changed the key doesn't wait. A job is lost with the process, and the schedule's tidy recovers it.

### Deprecation and retirement

FR-CAT-04's statuses come from the write of a sync of everyone's keys, `Schedule` or `Administrator`, once its availability is recorded:

- **A model is missing** when no key listed it in this run and no key can use it any more, in a run where at least one of the provider's keys listed. A key that failed keeps its rows, so a model that any failing key could use isn't missing. A run in which every key failed judges nothing, so an outage can't deprecate anything. The tidy has already removed what deleted keys had and marked what invalid keys had unavailable, so those don't hold a model up.
- **The first time it's missing, it's Deprecated**, and `missing_since` is set. **At a run that finds it still missing `Catalog:RetireAfterMissingDays` later**, 7 by default, it's Retired. A run in which every key failed leaves it as it is, however long that lasts.
- **A sync of some keys never deprecates anything.** One user's keys, or one key, can't show that nobody can use a model.
- **Any sync that lists it makes it Active again**, whatever the trigger, and clears `missing_since`. That counts as an update.
- **Hiding is an override, not a status.** A row's status is only Active, Deprecated or Retired, so a hidden model ages like any other, and its agents are told when it goes. `ModelStatus.Hidden` stays, because FR-CAT-01 lists it, and `CatalogModel.ShownStatus` gives it for an Active model that's hidden. The migration that added `model_overrides` moved any row stored as Hidden there and made it Active.
- **The run's row counts** the models it deprecated and retired, and each change is an Information event.

Once the write has committed, `ModelStatusObservers` tells every `IModelStatusObserver` about the provider's changes. It works like `CredentialObservers`: the observers are resolved on first use, and one that throws is logged and doesn't stop the others. Agents subscribes, and has its own recovery path: see [Acting on retired models](#acting-on-retired-models).

### Logging

One Information event per provider run, with its counts, one per change of a model's status, and a Warning for each failed key, with its credential ID, provider and failure. Nothing logs a key, and `AiTransport` already logs each attempt. `CatalogLoggingTests` checks the logs, `catalog_sync_runs` and `job_runs` with canary keys through every kind of sync.

### The pages

`/credentials` shows how many models each AI key can use and when they were synced, and a **Sync models** button when the user has a valid AI key. The button waits for the sync, and says how it went in the page's status notice. The count includes hidden models, because it's what the key can use.

`/catalog` lists the models that aren't hidden (FR-CAT-07), with their capabilities, tokens, price, status and recommended roles, and the labels of the user's `Valid` AI keys whose last sync found each one, read as role assignments read them.

- **The filters are a plain GET form**, without `data-enhance`, read through `[SupplyParameterFromQuery]`, so they work without a script and a filtered page can be bookmarked. Anything in the query that isn't a provider, capability or status is ignored. The page is interactive, for the **Sync models** button.
- **A capability matches only a model known to have it.** OpenAI reports none, so its models are left out until the configuration or an administrator says, and the page says so.
- **The table scrolls inside its own region**, which is focusable and labelled, so a phone never scrolls the page sideways.
- `CatalogModelRow` renders a model's cells for both this page and the administrators' one, which add their own after it.

### Administration

`/admin/catalog` and `/admin/catalog/model` are static pages behind `AccountPolicies.Administrator` (FR-ADM-03). They take no secret, so they have only the global rate limit, not the sign-in one, which would stop an administrator after ten edits a minute.

- **`ICatalogControls`, in `Modules/Administration/Contracts`, is their only way in.** Each call checks `IAdministrators.IsAdministratorAsync`, because a circuit has no middleware, answers null to anyone else, and then calls `ICatalogAdministration`. Catalog can't use Identity, which is why the check is in Administration. Reading the catalog needs no check, so the pages use `ICatalog` for that.
- **Catalog makes each change and audits it** in the same transaction, as the administrator, with no `UserId`, because it's platform-wide. See [What's recorded so far](#whats-recorded-so-far).
- **A model's edit locks its `models` row** with `FOR UPDATE`, so two administrators take turns and each audit record's "before" is what the other left. An edit that changes nothing writes nothing, and one that sets every field back deletes the row.
- **The role defaults are saved together**, under `LOCK TABLE role_defaults IN SHARE ROW EXCLUSIVE MODE`, so readers carry on and two saves take turns. A role that had no row and still has no default gets no row, so the section can still give it one.
- **The model page takes the model from the query string**, not the route, because an ID can hold a dot, which the static file fallback would take, or a slash. A static form posts back to its own address, query and all.
- **The sync button** starts `catalog-sync-all` and audits it, or says one is already running, and the page lists the newest 50 rows of `catalog_sync_runs`. Their errors are a count, because `AiFailure` is the adapters', which the Web UI can't use.

## Agents

`Modules/Agents` holds users' trading agents: their settings (FR-AGT-02) and their lifecycle (FR-AGT-04). The [scheduler](#scheduler) runs the Active ones. What a cycle does is later issues' work: the council (#31) and execution (#34).

| Contract | What | Who uses it |
|---|---|---|
| `IAgents` | Listing, creating, editing, cloning, activating, pausing, stopping, archiving and deleting a user's agents, which agents use a credential (FR-KEY-10), and the models the user can give each role with the assignment a new agent starts with (FR-ROLE-02, FR-ROLE-03). Every call takes the user, so another user's agent behaves like one that doesn't exist (NFR-SEC-04) | The Web UI and the decision cycle now. The wizard, the council and execution later |
| `IActiveAgents` | Every user's Active agents on a timeframe, for the scheduler. It isn't scoped to a user, so `BannedApis` allows it only in Agents and Scheduler (NFR-SEC-04) | The scheduler |
| `IAgentExposure` | Whether an agent has open positions or orders, an account's available balance, and what its Futures account says for EXT-BIN-07. A port Agents declares and Execution implements ([Inversions](#inversions)) | Agents, and the settings page, which locks fields with it |
| `IAgentHistory` | Deletes an agent's rows in a module above Agents, on the delete's own context and transaction. A port Agents declares, and each module that keeps an agent's history implements ([Inversions](#inversions)) | `Agents.DeleteAsync`. DecisionCycle implements it, and Execution and Experience will |
| `RiskSettings`, in `RiskEngine/Contracts` | FR-RISK-02's settings, their defaults and each one's own range. Only numbers, so the engine still depends on Kernel alone (rule 4) | Agents stores them, and checks them against `RiskCaps` (FR-RISK-11). The engine takes them as input. See [Risk Engine](#risk-engine) |

### Tables

| Table | What |
|---|---|
| `agents` | One row per agent: its settings as plain columns, risk settings as `risk_*`, the budget, its status, why it's in it, when a loss limit's pause ends (`loss_limit_until`), and a `version` |
| `agent_role_assignments` | One row per agent and role: the model as provider and model ID, which is the catalog's key, and an optional fallback. Its foreign key to `agents` cascades, because both are this module's |
| `agent_model_notices` | One row per agent, model and status (Deprecated or Retired) its owner was told about, so they're told once ([Acting on retired models](#acting-on-retired-models)). It's apart from the role rows because an edit replaces those. Its foreign key to `agents` cascades |

- **There's no foreign key to `users` or `credentials`.** An agent keeps its exchange key's ID after the key is deleted, so the user can still edit it, and activation says the key is missing. Account deletion (#51) goes through Identity's port.
- **`version` is a concurrency token that every change increments.** Edits and status changes are conditional on it, so an edit from an old page, or one that crosses a pause, changes nothing and says so. It's explicit rather than `xmin`, so a conditional `ExecuteUpdate` can compare and increment it.
- **`name_key` is `lower(name)`, computed by the database**, for the unique index on a user's names among agents that aren't archived. Archiving frees a name.
- **`quote_asset` is stored from Binance's symbol** when the pair is saved, because the capital allocation is an amount in it (DATA-R-02).

### States

| From | Activate | Pause | Stop | Archive | Delete |
|---|---|---|---|---|---|
| Draft | ✓ | | | ✓ | |
| Active | | ✓ | ✓ | | |
| Paused | ✓, to resume | | ✓ | ✓ | |
| Stopped | ✓ | | | ✓ | |
| Error | ✓ | | ✓ | ✓ | |
| Archived | | | | | ✓, for good |

- **Each change is a conditional update on the status and version it read**, in one transaction with its audit record, which has the status before and after and the reason (FR-AGT-04). Of two changes at once, one happens and the other says the status doesn't allow it.
- **Archiving is how an agent is removed** (DATA-R-04), and needs no open positions or orders. Deleting is only for an archived agent, and removes its row and roles, and, through `IAgentHistory`, the history other modules keep, such as its decision cycles, in the same transaction.
- **Stopping leaves positions and orders as they are.** FR-AGT-07's other choice, closing them, comes with the kill switch (#37).
- **A daily or weekly loss limit's pause can't be ended early**, by resuming or by stopping and activating, and ends on its own. See [Loss limits](#loss-limits).
- **Only the system puts an agent in Error**, through `IAgents.EnterErrorAsync`, when its decision cycles keep failing to reach a decision. It's a conditional update, by the system, with reason `RepeatedCycleErrors`, an `AgentErrored` audit record and the `AgentPausedErrors` notification, in one transaction. The user activates it again once they've fixed what was wrong. See [Decision cycle](#decision-cycle).

### Activation

`ActivationChecks` holds FR-AGT-05's checks for Demo, and EXT-BIN-07's for Futures. They read credentials, the catalog, Binance's symbols and the Futures account, and a symbol lookup or an account read can call Binance, so they run before the transaction, which is then conditional on the version they checked. Each check gives a typed problem, and the page lists them all.

1. **Demo.** Live is refused until switching to Live exists (#39), which UC-04 makes an action of its own rather than an edit.
2. **The exchange key** exists, is the user's Binance key for the agent's environment, is `Valid`, and is enabled for the agent's market (FR-KEY-05).
3. **The pair** is one Binance lists as trading, and a perpetual contract on Futures. A delivery contract has no funding, which the funding filter assumes, and it expires.
4. **The Futures account** (EXT-BIN-07), once the key and the pair are good, read now through `IAgentExposure.ReadFuturesAccountAsync`:
   - Futures is enabled: Binance lets the account trade, and doesn't refuse the key there. Otherwise it's `MarketNotEnabled`, as when Credentials found it so.
   - One-way Mode, because FR-RISK-09 requires it. Ecnanib never changes it: it's account-wide, so it would change how the user's own trading works.
   - Not Multi-Assets Mode, which allows cross margin only, so FR-RISK-09 couldn't set isolated before an entry. This goes beyond EXT-BIN-07's list, which would otherwise let an agent activate that could never enter.
   - Margin enough for the smallest position Binance accepts for the pair: the pair's quote asset available on the account is at least its minimum notional over the agent's leverage after the caps, rounded up to 8 places. Without a minimum notional, anything above zero. More capital than the account has is FR-AGT-13's warning below, not a problem.
   - When Binance can't be reached, it's `FuturesAccountUnverifiable`, which a [loss limit's resume](#loss-limits) tries again, as it does `SymbolUnverifiable`.
   - The answer may be up to a minute old, like the balance below, and the problems' text says to try again in a minute.
5. **Each required role** (Technical Analyst, Strategist and Risk Critic) has a model that's `Active` in the catalog, that one of the user's `Valid` AI keys can use, and that doesn't lack structured output ([Role assignments](#role-assignments)). Validity comes from Credentials, not the catalog's rows, which follow a change to a key a moment later. An optional role's model, and every fallback, are checked the same way, but only warn. So does a Strategist and Risk Critic on one provider (FR-ROLE-04), and a Sentiment Analyst whose model says it can't search.
6. **Capital** above zero, and every risk setting in its range: "complete risk settings". And none beyond the global caps (FR-RISK-11), which may have been tightened since the agent was saved. Saving checks both too, per setting, so the page marks each one.
7. **The slot**, under the [user's lock](#the-users-lock).
8. **No loss limit's pause under way** ([Loss limits](#loss-limits)). It's checked from the agent's row, not its settings, so an Active agent's edit doesn't ask.

**FR-AGT-13's warning** comes after the agent is active: the capital of the key's active agents on that market, in the pair's quote asset, against the account's available balance in that asset. Execution reads the balance from Binance, at most once a minute per key and market, because every user shares the weight and a circuit's clicks never reach the rate limiter. The Futures account's settings are kept the same way, per key. An unknown balance gives no warning.

### Role assignments

`RoleModels` holds FR-ROLE-02's rule and FR-ROLE-03's defaults, so the settings page, activation and the wizard (#40) agree.

- **A hidden model isn't offered.** It's in no role's options and no default, even a default that names it, which then falls to its provider's next model. An agent that already has it keeps it, and activation still accepts it, because hiding withdraws a model from offer rather than retiring it (FR-CAT-05). A post that names one is accepted too, which is consistent with that. The settings page labels a chosen hidden model "no longer offered".
- **A model can fill a role** when it's `Active`, one of the user's `Valid` AI keys can use it, and its structured output isn't `false`. Every role needs structured output, but **only an explicit `false` counts against a model**. Only Anthropic reports it, so null means nobody said, and requiring `true` would leave nothing from Google, OpenAI or xAI assignable until the configuration said otherwise. The council validates every answer against its contract anyway (FR-PRM-08), and a failure falls back (FR-ROLE-06). Search beats a schema on Anthropic and Gemini ([The four providers](#the-four-providers)), which is no reason to refuse them.
- **Live search is preferred for the Sentiment Analyst.** Its options list models with live search first, then unknown, then without. Activation warns only on an explicit `false`. Image input is optional for the Technical Analyst, so it ranks nothing.
- **Administrators' recommendations come next.** A model recommended for a role comes before its provider's others, in the role's options and in its candidates for a default. The requirement's preference comes first, and a configured default model and provider still beat a recommendation from another provider.
- **The default assignment** is worked out per user, from the models they can use and `ICatalog.ListRoleDefaultsAsync`, rather than stored. The Strategist goes first, then the rest in role order. Each role takes the first model it can use from:
  1. its default model;
  2. its default provider's models, recommended first, then newest first;
  3. the other providers', by name, then recommended first, then newest first.

  Newest is by release date, then by when a sync first listed it, then by ID, because nothing says which model is strongest and Gemini gives no dates. That's why a default can name a model. A user with one key gets every role from it (FR-ROLE-05). The Risk Critic takes a provider other than the Strategist's when the user has one (FR-ROLE-04). Each fallback is the next on the role's list from another provider, else from its own, and the Risk Critic's avoids the Strategist's provider too. A user with nothing usable gets no defaults, and the selects start at None.
- **Saving a Draft checks none of this**, as before, and an Active agent's edit runs the activation checks, so it can't move a required role onto a model without structured output.
- **The landing page's "Default provider" column reads the same rows**, so it follows administrators' edits rather than claiming SRS 7.3's.

### One agent per key, market and pair

FR-AGT-10 and AC-13 hold the slot for **Active, Paused and Error** agents, because each may still hold positions or orders on the account, which the exchange shares between everything trading that symbol. A Draft or Stopped agent holds none. The user frees the slot by stopping or archiving the other agent.

The rule is checked under the user's lock, by activation and by an edit that moves an agent holding a slot. A unique index, `ix_agents_trading_slot`, filtered to those statuses, is the backstop. It should never fire, because a violation is logged as an Error, which `Accounts` avoids too.

### The user's lock

`AgentLock` takes a PostgreSQL advisory lock for the transaction, keyed on the user, around every write that could break a rule across rows: the most agents a user may have (`Agents:MaxAgentsPerUser`), unique names, and the slot. A credential belongs to one user, so every slot conflict is within one user, and one lock covers them all. A system pause takes it too, so it can't cross an activation.

It doesn't lock the `users` row, as `TwoFactor` does, because that's Identity's table, and SQL against another module's table is the one boundary the tests can't see. An in-process lock would also do for one instance (ASM-06), but this one doesn't depend on it.

### Editing

- **The market, environment, pair, timeframe and exchange key can't change while the agent has open positions or orders** (FR-AGT-03). The key is locked too, because the positions live on its account. The settings page asks `IAgentExposure` and shows them disabled.
- **An Active agent's new settings must pass the activation checks**, so an edit can't leave a running agent without a role or capital.
- **The pair is checked with Binance only when it changes**, so a pair that stopped trading doesn't block other edits. Activation checks it again.
- **The audit summary never holds the description or custom instructions.** Administrators can search the trail (FR-ADM-06), and must not read what users write to their models (FR-ADM-01).

### Pausing on a credential

`AgentCredentialObserver` hears of every change to a credential. A deletion, a key that isn't `Valid`, or a Binance key checked again, which may have lost a market, starts an `agent-credential-check` run for the user, and the request that changed the key doesn't wait.

`AgentPrerequisites` pauses each of the user's Active agents whose credentials no longer let it trade, one at a time, under the user's lock, with the reason: the exchange key was deleted, isn't valid or isn't enabled for the market, or no valid AI key is left for a required role's provider. The pause is by the system, with the owner as `UserId`, through `AgentPause`: a conditional update on the status and version it read, with its audit record, which a retired model's pause shares. Their positions and protective orders stay on the exchange (FR-AGT-06), and resuming is the user's choice. `CredentialInvalid` is raised by Credentials, in the transaction that records the check.

It reads only what Credentials says, so an outage, which leaves a key's status alone, pauses nothing. A model that's deprecated or retired has [steps of its own](#acting-on-retired-models).

`agent-prerequisites` runs the same check for every user with Active agents every hour, and asks nothing of Binance or the providers. It's the recovery path for an event lost with the process.

### Acting on retired models

`AgentModelObserver` hears of each change the catalog makes to models' statuses ([Deprecation and retirement](#deprecation-and-retirement)), and starts an `agent-model-check` run, so the sync doesn't wait. `ModelLifecycle` then takes every agent that isn't archived and names a Deprecated or Retired model, as a role's model or its fallback, that its owner hasn't been told about. It goes user by user, reading each user's keys and catalog once, then one agent at a time, under the user's lock, working out each step again from the agent as it is:

1. **Switch.** A role whose model retired takes its fallback, when the fallback can fill the role as [activation](#role-assignments) requires: `Active`, usable with one of the user's `Valid` keys, and not lacking structured output. The fallback becomes the model, and the role is left without one until the user chooses another. The agent's version moves on, and the change is audited as `AgentUpdated` by the system, with each role's model and fallback before and after. That applies to every agent that isn't archived, so one that's activated later doesn't fail on the retired model.
2. **Pause.** An Active agent with a required role still on a retired model pauses, with the reason `ModelRetired`, through `AgentPause`. An optional role is skipped when its model fails (FR-ROLE-06), and a fallback only stands by, so a retired one of either doesn't pause anything. An agent that isn't Active isn't running, and activation refuses a model that isn't `Active`.
3. **Tell.** At most one `ModelDeprecated` and one `ModelRetired` per agent per run, linking to its settings, labelled Demo for a Demo agent. Each model and status it was about becomes a row of `agent_model_notices` in the same transaction, so the owner is told once. When a model is Active again, its rows are deleted, so a later deprecation is news again.

A Deprecated model only warns: the agent keeps using it until it retires. `agent-models` runs the same pass every hour, and is the recovery path for an event lost with the process. One agent's or user's failure is logged and never stops the rest (NFR-REL-05).

### Loss limits

The Risk Engine finds a limit reached (FR-RISK-08), and `IAgents.PauseForLossLimitAsync` acts on it. The decision cycle (#31) calls it with the `LossLimitBreach` from `IRiskEngine.CheckLossLimits` at the start of each cycle, or from an entry's evaluation.

| Limit | Reason | Pause ends |
|---|---|---|
| Daily | `DailyLossLimit` | At the next UTC day, on its own |
| Weekly | `WeeklyLossLimit` | At Monday 00:00 UTC, on its own |
| Losses in a row | `ConsecutiveLosses` | When the user resumes it. A streak suggests something a person should look at, and the SRS gives it no period |

- **The pause is by the system, through `AgentPause`**, under the user's lock, with `AgentPausedLossLimit` raised in the same transaction, linking to the agent's settings and labelled Demo for a Demo agent. Only an Active agent pauses, and a period that's already over pauses nothing.
- **`loss_limit_until` holds the end of a daily or weekly pause.** Activation refuses with `LossLimitPause` until then, whatever the status, so stopping the agent can't get round it. Any activation clears it.
- **`agent-loss-limit-resume` ends the pause**, every minute. It resumes each agent still Paused for a daily or weekly limit whose end has passed, one at a time, through the same checks as a user's resume, with the reason `LossLimitEnded` and an audit record by the system. An agent the user stopped meanwhile is left alone. One that fails its checks stays paused for the user, and its `loss_limit_until` is cleared so it's tried no more, unless all that failed was reaching Binance, which the next run tries again. The end is in the table, so a restart loses nothing.
- **The streak restarts when the agent resumes.** The decision cycle will count losing trades closed since the agent last became Active, from the trade journal (#36), or a resumed agent would pause again at once. Until then it passes `TradingRecord.None`.

### The pages

| Page | What |
|---|---|
| `/agents` | Interactive. Each agent, labelled Demo (FR-UI-07), with its status, why the system paused it, and the actions its status allows. Stop, archive and delete ask first, in place. Activation lists what to fix, and its warnings. Archived agents are in a section of their own |
| `/agents/new`, `/agents/{id}/settings` | Static, like every form: a post goes through the HTTP rate limiter, which a circuit never reaches. Every FR-AGT-02 setting on one page, with each capped risk setting's global cap under its hint, and a new agent's risk settings brought within the caps. It offers the user's Binance Demo keys, and each role's options from `IAgents.ListRoleChoicesAsync`, keeping a chosen model that's no longer offered so that saving doesn't drop it. A new agent starts with the default assignment. When the Strategist and Risk Critic share a provider, a note under the Risk Critic says so, on load and after a refused post, because the page is static |

The wizard (#40) will take over `/agents/new`, and the agent's own page (#41) `/agents/{id}`. A clone is named by the page, "name (copy)", numbered when that's taken, because the name is text the user reads.

## Risk Engine

`Modules/RiskEngine` validates and sizes every order intent (SRS 3.9), and nothing reaches the exchange without a passing evaluation (FR-RISK-01, AC-01). It depends on `Kernel` alone (rule 4): it takes everything as plain values and returns results, so the council's Appendix B types never reach it, and a model's output can only become one of the engine's own `OrderIntent`s, which it doesn't trust (NFR-SEC-08).

| Contract | What | Who uses it |
|---|---|---|
| `IRiskEngine` | `Evaluate(intent, inputs)`, and `CheckLossLimits` for the start of each cycle | The decision cycle (#31), approvals' re-validation (#38), and Execution for the kill switch and the emergency close (#34, #37) |
| `OrderIntent`, `RiskInputs` | What's asked, and everything the rules look at | Their callers build them |
| `RiskEvaluation`, `SizedOrder` | Every rule's outcome, reason and inputs (FR-RISK-10), and what a passing evaluation allows to be sent | The decision trace (#31) and Execution (#34) |
| `RiskCaps` | FR-RISK-11's global hard caps, from the `RiskCaps` section, with SRS 7.3's values when it's missing. An invalid one stops startup and is named (CFG-03) | The engine, Agents and the settings page share one instance |
| `RiskPeriods` | The UTC day and the week from Monday that the loss limits count over and pause until | Whoever sums P&L for `TradingRecord` (#31, #36) |

### Determinism

- **An evaluation reads nothing but its arguments and the caps.** No clock (`RiskInputs.Now` is the caller's), no I/O, no state, no randomness, and `decimal` only (DATA-R-02). `RiskEngineDeterminismTests` scans the module for clocks, `Random`, `Guid.NewGuid`, tasks, threads, the network, files, the database, configuration and binary floating point, and `RiskEngineDeterminismGuardTests` proves the scan against broken fixtures. The entry class and the caps' reader are exempt, because they wire the engine up. The scan caught `DateTimeOffset.AddDays`, which takes a `double`, so whole days are `TimeSpan`s.
- **The caps come through DI**, not as an argument, so a caller can't pass looser ones. An agent saved before the caps were tightened is clamped to them at every evaluation, and the trace shows the setting, its cap and what was used.
- **Every rule runs and is recorded,** in `RiskRule` order, with its inputs written in the invariant culture. A rule that depends on one that failed is `NotEvaluated`. Only a failed `Settings` or `Action` stops the rest. The verdict passes when no rule failed.

### Rules

s is +1 for a long and −1 for a short. Comparisons multiply rather than divide, so no rounding moves a boundary, and a value exactly at a bound passes.

| Rule | Applies to | Fails when |
|---|---|---|
| `Settings` | Entries | A setting is out of its range, or there's no capital |
| `Action` | All | The action isn't the market's (FR-CYC-03), an entry is opposite the position, an exit or adjustment has no position, or there's no unfilled entry to cancel |
| `SymbolTrading` | Entries | Binance doesn't list the symbol as TRADING |
| `DailyLoss`, `WeeklyLoss`, `ConsecutiveLosses` | Entries | The loss is at least the limit, or the streak at least the count. The breach goes back to the caller ([Loss limits](#loss-limits)) |
| `EntriesPerDay`, `Cooldown`, `PendingEntry` | Entries | Too many entries today, adds and unfilled limits included; too few cycles since a loss; an entry still unfilled, which could otherwise stack past every cap |
| `Funding` | Futures entries | s × rate × 800 > threshold × interval hours: funding against the position, per 8 hours, above the threshold. No rate passes |
| `Prices` | Entries and adjustments | A filter is missing, a price is outside `PRICE_FILTER`, or a limit price (a limit entry, or a Spot target) is outside `PERCENT_PRICE`'s band |
| `StopRequired`, `StopSide`, `StopDistance` | Entries; `AdjustStop` for the first two | No stop; a stop on the wrong side of the entry, or past the price that triggers it; a stop outside the agent's ATR bounds. The bounds are for entries only, so a trailing stop may sit closer |
| `StopNotWidened` | `AdjustStop`, adds | A moved stop isn't strictly tighter, or an add's stop, which then guards the whole position, is wider (FR-RISK-06) |
| `TargetSide`, `RewardToRisk` | Entries with a target, `AdjustTarget` | A target on the wrong side, or below the minimum reward-to-risk. No target on `AdjustTarget` removes it |
| `NoAveragingDown` | Adds | The position isn't in profit at the price it would close at now, against Binance's break-even price when known (FR-RISK-07). Break-even counts as a loss |
| `Size`, `Quantity`, `Notional` | Entries and exits | Below |
| `LiquidationBuffer` | Futures entries | Below |

**Exits are never blocked by policy.** The loss limits, cooldown, funding and the rest apply to entries only, and `Settings` too, so an exit fails only when it can't be sent: no position, or a Spot position too small to sell (`Dust`). That's what lets the kill switch and the emergency close go through the engine (AC-01).

### Rounding and sizing

- **Prices are rounded before anything checks or sizes with them**, on Binance's grid, which starts at the filter's minimum: `min + n × tick`. A limit entry rounds to a better price, a stop toward the price, which can only tighten it, and a target toward the entry. A 0 in a filter is Binance turning that part off.
- **Quantity = (capital × risk %) ÷ |entry − stop|** (FR-RISK-03), with the risk asked for brought down to the agent's maximum, then capped by the smallest of: the maximum notional (the capital when unset); the capital, which on Futures is margin, so notional up to capital × leverage; the available balance, or margin × leverage; the leverage bracket's notional at the agent's leverage; and the smaller maximum of both lot filters, so the position can always be closed at market. It's rounded down, on `MARKET_LOT_SIZE`'s grid for a market order unless its step is 0. Below the minimum quantity or notional, the intent fails.
- **On an add, one stop guards the whole position,** so what the position already risks at the new stop comes out of the budget, and its notional and cost count against the caps.
- **A market entry is sized at the ask to buy and the bid to sell.** The minimum notional is checked at the lower of that and the last price, and the maximum at the higher, because Binance measures with its own.
- **An exit closes the share asked for, rounded down.** On Spot, only what the agent's own fills bought, and no more than the account holds (FR-AGT-09). A remainder too small to sell later is sold now. Futures reduce-only orders are exempt from the minimum notional.

### Liquidation

The liquidation price is Binance's isolated one-way formula, for the whole position after the entry, at the agent's leverage, with the bracket its notional falls in:

- long: (Q × EP − WB − cum) ÷ (Q × (1 − MMR))
- short: (Q × EP + WB + cum) ÷ (Q × (1 + MMR))

where WB = Q × EP ÷ leverage. The distance from the entry must be at least the agent's multiple of the stop's. A long that can't be liquidated passes. Without brackets, every Futures entry fails, because the buffer can't be checked. A notional exactly at a bracket's cap belongs to that bracket, which is the one sizing capped at.

### Orders and AC-01

- **Every order cites a passing evaluation.** An entry's protective stop and target are part of its `SizedOrder`, so they cite its evaluation. The kill switch and the emergency close are `Close` or `Sell` with `Origin = System`, which only the structural rules can stop. So the audit query over traces and orders that AC-01 asks for has no exceptions.
- **Only the engine makes a `SizedOrder`.** Its constructor is internal, `BannedApis` refuses it anywhere else, and it's a class rather than a record, so nothing can copy one with a change. Execution sends only these (#34).

### Inputs

The engine can't read them itself, so the decision cycle builds them (#31):

| `RiskInputs` | From |
|---|---|
| `Filters` | `IExchangeSymbols.FindAsync`: `IsTrading`, `Price`, `Quantity` (LOT_SIZE), `MarketQuantity`, `Notional`, `PercentPrice`. A null filter means Binance sent none |
| `Prices` | The snapshot's `Quote` (bid, ask, last) and, on Futures, `Futures.MarkPrice` |
| `Atr` | `snapshot.Candles.Indicators.Atr14` |
| `Funding` | `snapshot.Futures.FundingRate`, with `FundingIntervalHours`, from `/fapi/v1/fundingInfo`, which is 8 hours for a symbol Binance hasn't adjusted |
| `AvailableQuote`, `BaseHeld` | `IExchangeAccount.GetAccountAsync`: the quote asset's available balance on Spot, `AvailableBalance` on Futures, and the base asset's total on Spot |
| `Position` | Execution's records (#34, #35): the agent's own quantity and average entry, its stop and target. On Futures, Binance's `BreakEvenPrice` too |
| `HasPendingEntry` | Execution's records (#34) |
| `Record` | The trade journal (#36), over `RiskPeriods` |
| `Brackets` | `AgentAccount.Brackets`: the symbol's brackets on the agent's account, from `/fapi/v1/leverageBracket` through its key. Empty when Binance couldn't give them, which fails every Futures entry but no exit |

### What it doesn't do

- **Fees and slippage aren't in the size.** FR-RISK-03's formula is followed as written, so the loss if the stop fills is the agent's risk per trade plus the fees on both legs and a stop-market's slippage: on a tight Futures stop, roughly a tenth to a fifth more. A fee-aware size would need the account's commission rate as an input.
- **Spot's price band uses the last price**, where Binance uses an average over a few minutes. A rare rejection is refreshed and recorded (#34).
- **An add's liquidation price assumes the whole position posts notional ÷ leverage**, which is what Binance's isolated margin holds unless margin was added by hand, which an agent never does.

## Scheduler

`Modules/Scheduler` starts each Active agent's decision cycle when a candle of its timeframe closes (FR-CYC-01). It handles a concurrency limit, a fair queue and jitter (FR-CYC-08), the cycle's deadline (FR-CYC-06) and a catch-up after downtime (FR-CYC-04). What a cycle does belongs to DecisionCycle (#31).

| Contract | What | Who uses it |
|---|---|---|
| `IActiveAgents`, in `Agents/Contracts` | Every user's Active agents on a timeframe. It isn't scoped to a user, so `BannedApis` allows it only in Agents and Scheduler (NFR-SEC-04) | The scheduler |
| `IDecisionCycles`, `CycleRequest`, in `DecisionCycle/Contracts` | One cycle: the agent and its user, the candle's close, the deadline, and whether it's a catch-up. `BannedApis` allows it only in DecisionCycle and Scheduler, so no cycle starts outside the limit, the queue and the one-per-agent rule | The scheduler. See [Decision cycle](#decision-cycle) |

### When cycles run

Each timeframe has its own recurring job: `cycles-15m`, `cycles-1h`, `cycles-4h` and `cycles-1d`. Each job has its own loop, so a long queue at a daily close can't hold up the next 15-minute one. A schedule finds its last run by its job's name, so the names never change.

- **Closes are aligned to UTC.** `CandleCloses` counts intervals from the Unix epoch, which is a midnight. That gives 15m at :00, :15, :30 and :45, 1H every hour, 4H at 00:00, 04:00 and so on to 20:00, and 1D at 00:00. A close's run is due `CandleCloseDelaySeconds` after it, so the candle is final.
- **`CandleSchedule` works out the next run from the last start.** That start handled the last close at or before it, less the delay, so the next close after that is due. This holds even when the start was late. A job that has never run waits for the next close, so a new installation doesn't run a cycle for a close it wasn't there for. A restart that changes the delay can move one close either way.
- **After downtime, the due time is in the past.** The runner runs it once, however many closes were missed.

A run looks only at the latest close, so an agent gets at most one catch-up:

| The run is late by | Its cycles | Deadline |
|---|---|---|
| Up to 30 seconds (NFR-PERF-01) | Run on time | Close + delay + `CycleDeadlineMinutes` |
| Up to `CatchUpGraceMinutes` | Run as a catch-up, with `CycleRequest.CatchUp` set | Now + `CycleDeadlineMinutes`, because counting from the close would leave a catch-up no time |
| More | Skipped, with a Warning per agent | None |

- **A late run is a catch-up whatever made it late.** The application may have been down, or the database may have been down when the run tried to start.
- **The deadline counts from the close, so time in the queue counts against it.** After the deadline a cycle executes no new entry (FR-CYC-06). The cycle checks with `CycleRequest.AllowsNewEntry` right before an entry. Managing a position isn't an entry.

### Dispatching

`CycleDispatcher` takes each agent through four steps:

1. **One cycle per agent.** If the agent's last cycle is still queued or running, the new one is skipped, with a Warning. Nothing else starts cycles, so this is FR-CYC-02's agent lock. It's kept in memory, which is enough for one instance (ASM-06).
2. **Jitter.** A random wait of up to `MaxCycleJitterSeconds` spreads exchange requests (NFR-PERF-05). The setting is capped at 20 seconds, so cycles still start within NFR-PERF-01's 30.
3. **A slot.** `CycleSlots` has `MaxConcurrentCycles` of them, and orders waiters by three things:
   - Their deadline, so a 15m cycle goes before a 1D one at midnight.
   - Their user's round, which is how many cycles the user already had queued or running when the waiter arrived. Users take turns, so one user's agents can't crowd out another's.
   - When they arrived.

   A waiter still queued at its deadline is dropped, with a Warning. It could enter nothing, and its stop rests on the exchange (NFR-REL-02).
4. **A run of its own.** The cycle runs through `IJobRunner.RunAsync` as `decision-cycle`, so it gets its own correlation ID, `job_runs` row and error boundary (NFR-REL-05). It also gets the runner's two signals and a place in the shutdown drain (NFR-REL-04). Its timeout is the time left to its deadline plus two minutes to wind down. The start is logged with `StartDelayMs`, measured from close + delay, and with a Warning past 30 seconds, which is what #56 measures for NFR-PERF-01.

- **A job returns once each cycle has started or been dropped,** without waiting for the cycles to finish, so a slow cycle can't hold up the next close. Its `details` give the counts: due, started, still running, past deadline and not started.
- **A hung cycle frees its slot five seconds after its timeout,** with an Error. The runner keeps waiting for work that ignores its token, and without this a few hung cycles would stop everyone else's. The agent keeps its place until the work really ends, because two cycles at once for one agent would be worse than a stalled one.
- **Shutdown starts nothing more.** Whatever is still waiting is abandoned, and the job ends as `Interrupted`. Cycles that had started drain like any other run.
- **Catch-up is per close, not per agent.** Suppose a cycle was still waiting when shutdown began, or its start couldn't be recorded. After a restart it isn't caught up, because the job for its close already ran. Catching up per agent would read each agent's last cycle from `decision_cycles`, which the [decision cycle](#decision-cycle) keeps.

### Settings

They're in the `Agents` section (SRS 7.3), and `SchedulerSettings` reads them. A missing value takes its default, and an invalid one stops startup with a message that names it (CFG-03). `CatchUpGraceMinutes` and `CycleDeadlineMinutes` take one value per timeframe, keyed `15m`, `1H`, `4H` and `1D`, and any other key is refused.

| Setting | Default | Range |
|---|---|---|
| `CandleCloseDelaySeconds` | 10 | 0 to 300 |
| `MaxConcurrentCycles` | 50 | 1 to 1000 |
| `MaxCycleJitterSeconds` | 10 | 0 to 20. Not in SRS 7.3 |
| `CatchUpGraceMinutes` | 5, 15, 60, 180 | 0, which turns catch-up off, to less than the interval |
| `CycleDeadlineMinutes` | 3, 10, 20, 30 (NFR-PERF-02) | 1 to less than the interval |

### Testing it

`SchedulerApp` in `tests/Ecnanib.Tests/Scheduler` gives each test its own application, clock and database. It turns jitter off, keeps the four jobs for the test to run, and replaces `IActiveAgents` and `IDecisionCycles` with fakes. A fake cycle can be held at a `Gate` or made to fail. `TestClock` moves only the time code reads, and its timers still run in real time. So the test for dropping a cycle at its deadline moves the clock past it and then frees a slot, rather than waiting for the timer.

## Decision cycle

`Modules/DecisionCycle` runs an agent's decision cycle (FR-CYC-02) when the [scheduler](#scheduler) starts it. It checks the loss limits and the AI budget, collects the market and the account, and asks the AI council's roles. The Risk Engine then checks what they propose. Every step is saved in a decision trace as it happens. A cycle ends with an order the engine sized, which execution (#34) or an approval (#38) takes from there.

| Contract | What | Who uses it |
|---|---|---|
| `IDecisionCycles`, `CycleRequest` | One cycle, for one agent and candle | The scheduler only, which `BannedApis` enforces |
| `ICycleObserver`, `CycleProgress` | Each change to a cycle's progress, after it commits: each role's state, model and summary, and the outcome (FR-ROLE-08) | The Web UI (#41, #53). Nothing subscribes yet |
| `IDecisionTraces` | An agent's latest cycle, for its user only (NFR-SEC-04) | The Web UI. The trace viewer (#42) extends it |
| `IAiSpending`, `AiSpend`, `BudgetCheck` | Each agent's AI spend this UTC day and month, for its user only (NFR-SEC-04), and whether its budget has run out (FR-CYC-07). See [Budgets](#budgets) | The decision cycle, and the Web UI's agent list and settings page |
| `ICouncil`, in `AiCouncil/Contracts` | Asks one role, with its fallback, and returns the answer or the failure, with every call it made | The decision cycle. It spends the user's money, so `BannedApis` allows it only in AiCouncil and DecisionCycle |
| `IAgentAccounts`, in `Execution/Contracts` | The agent's balances, read through its key, the symbol's leverage brackets on Futures, which cost 1 more each cycle, and its own position and pending entry, which are always none until #34 and #35 record orders | The decision cycle |
| `IAgents.EnterErrorAsync`, `IAgentHistory`, in `Agents/Contracts` | Error after repeated failures, and deleting the agent's cycles with it | The decision cycle, and `Agents.DeleteAsync` |

### Steps

| Step | What happens | Ends as |
|---|---|---|
| 1 | The agent is read again. If it isn't Active, nothing is recorded. Otherwise the cycle's row is inserted with its roles queued | — |
| 1 | The agent already has a cycle for this candle, through the unique index on agent and candle close. Nothing more happens | — |
| 2 | `IRiskEngine.CheckLossLimits`, whose rules are saved on the row. A breach pauses the agent through `IAgents.PauseForLossLimitAsync` (FR-RISK-08) | `PausedForLossLimit` |
| 2 | The agent has an AI budget, and its spend this UTC day or month has reached a limit. The check is saved on the row, and no role is asked. See [Budgets](#budgets) (FR-CYC-07) | `OverBudget` |
| 3 | The pair from `IExchangeSymbols`, the market from `IMarketData` at the candle's close, then the account from `IAgentAccounts`. Binance's `Throttled` is waited out once, when its `RetryAfter` fits before the deadline. A last candle that isn't `LastOpenTime` is a gap, as during maintenance | `Failed`, with `MarketData` or `Account` |
| 4 | The Technical Analyst, and the Sentiment Analyst when it's assigned, at once | — |
| 5 | The Technical Analyst failed | `Hold`, with `RoleFailed` |
| 6 | The Strategist failed | `Hold`, with `RoleFailed` |
| 6 | The Strategist held | `Hold` |
| 7 | The Risk Critic failed. It's required for a trade action | `Hold`, with `RoleFailed` |
| 7 | The Risk Critic rejected | `RejectedByCritic` |
| 8 | The proposal, with the Risk Critic's changes, becomes an `OrderIntent`, which `IRiskEngine.Evaluate` checks against `RiskInputs`. The evaluation is saved. A loss limit it finds pauses the agent | `BlockedByRiskEngine` when it fails |
| 8 | It passed, but it's an entry and `CycleRequest.AllowsNewEntry` says the deadline has passed (FR-CYC-06) | `PastDeadline` |
| 8 | It passed | `ReadyToExecute` |

- **An optional role that fails is skipped**, and the cycle goes on without its output (FR-ROLE-06). A required one makes the cycle hold. The Sentiment Analyst is the only optional role in a cycle: the Reviewer works after a trade closes (#45).
- **The intent takes the nearest target**, the lowest for a long and the highest for a short, which gives the lowest reward-to-risk. The Risk Critic's stop, targets and risk replace the proposal's, and `OutputContracts` has already refused any change that adds risk. The engine trusts none of it anyway (NFR-SEC-08).
- **`RiskInputs` follow the Risk Engine's [Inputs](#inputs)**, with what doesn't exist yet left out. The position and pending entry come from #34 and #35, and the record from #36. Until then there's no position, and the record is `TradingRecord.None`. The brackets come with the account, and the funding interval with the snapshot. When Binance can't give the brackets, the cycle goes on without them, and every Futures entry fails `LiquidationUnknown`, while an exit or a tighter stop doesn't need them.
- **AI calls get `JobContext.Stopping`**, because they're safe to abandon. Binance calls get the run's token (NFR-REL-04).
- **An unexpected exception ends the cycle as `Failed`, with `Unexpected`**, and is thrown again, so its job run is Failed too. Shutdown during a cycle ends it as `Interrupted`, and that last write has its own five seconds.

### Asking a role

`Council`, in AiCouncil, composes the role's prompt once, then tries its model and then its fallback:

1. **The key** is the oldest of the user's `Valid` keys for the provider that the catalog says can use the model. An agent stores the model, not the key. With none, the model fails as `NoKey`, and nothing is sent. A key that became unusable since fails as `KeyUnusable`, because it's the credential's checks that pause the agent, not a provider failure.
2. **The request** has the catalog's price, and live search for the Sentiment Analyst when the model has it. Its output limit is 8,192 tokens, or the model's own when that's lower.
3. **`OutputContracts.CompleteAsync`** sends it, and sends a broken answer back once (FR-CYC-05). The adapter has already retried per its provider's settings (EXT-AI-03), so a failure here means the model has failed, and the fallback is next.

A missing prompt template fails the role without trying the fallback, which couldn't help. Each role gets its own part of the runtime context:

| Role | Gets |
|---|---|
| Technical Analyst | The snapshot, of which `PromptWindows` keeps 120 and 60 candles |
| Sentiment Analyst | The snapshot, without candles, for the pair and its prices |
| Strategist | The snapshot, the account, the position and pending entry, the record, the risk settings after `RiskCaps.Apply`, and the analysts' outputs |
| Risk Critic | The Strategist's, plus the proposal, which its contract check uses too |

- **Earlier roles' output is untrusted content**, of kind `RoleOutput`, with the role as its source (FR-PRM-06). It's the validated output, written by `OutputContracts.Write` in the contract's names, not the raw answer, which may hold what the check cleared.
- **A role's answer lists every call it made**, billed or not, for the trace. A call holds the composed instructions and message, the answer, the parsed output, the violations, the tokens, the cost, the latency, the provider's attempts and the template versions. A retry also sent the first answer and its violations, which are on the call before it.
- **Calls are logged by the adapter**, with provider, model, role, latency, tokens, cost and outcome (FR-LOG-07). The council logs only which role failed on which model. Prompts and answers are never logged.

### Budgets

An agent's AI budget (FR-AGT-02) is checked at the start of each cycle, after the loss limits and before anything else. Once it has run out, the cycle asks no role and ends as `OverBudget`, which is FR-CYC-07's protective-only mode. An agent with no limit isn't checked.

- **Spend is summed from the trace**, `ai_calls` joined to `decision_cycles`, because every billed call is there. That includes failed calls and contract retries, each saved as its role finished. A call counts in the UTC day and month its cycle started in, which the cycles' index on user, agent and start finds, so a cycle still running at midnight counts in the day it started. One query sums the month, with the day as a `FILTER`.
- **Dollars and tokens are counted apart.**
  - A call to a priced model counts its estimated [cost](#cost).
  - A call to an unpriced model counts its input and output tokens, which is all that token caps limit.
  - So a dollar limit never stops an unpriced model, and a token cap never stops a priced one.
- **A limit runs out once the spend reaches it**, until the end of its UTC day or month. When several have run out, a monthly one's end is given, because it's later.
- **There's one check per cycle.** A cycle that starts within budget runs to the end, so spend can pass a limit by up to one cycle's calls. Checking between roles would waste what the cycle had already spent.
- **Protective-only isn't a status.**
  - The agent stays Active and keeps running cycles, so each one checks again, and a new day, a new month or a higher limit ends it at the next close.
  - It isn't paused, so there's no audit record.
  - Its positions and their exchange-resting stops stay as they are. Managing them is for Execution and reconciliation (#34, #35), which don't ask the council.
- **Its user is told once.**
  - `AgentPausedBudget` is raised when the agent's previous cycle since it became Active wasn't `OverBudget`. That's by candle close, not counting `Interrupted`.
  - It's added in `CycleRun.FinishAsync`'s transaction, so it commits with the outcome.
  - Running out again after a cycle within budget tells the user again.
- **The check is saved on the cycle**, as `budget`, when the agent has a limit. It holds the limits, the spend, the limit reached and when it ends, for the trace viewer (#42).
- **`IAiSpending` reads the same sums for the pages.**
  - The agent list and the settings page, which the notification links to, say until when an Active agent can't use AI.
  - The page and the cycle both decide with `AiSpend.Check`.

### The trace

| Table | What |
|---|---|
| `decision_cycles` | SRS 6.1's DecisionCycle: the agent, its pair and timeframe, the candle's close, whether it's a catch-up, the deadline, the start and end, the outcome and failure, an input snapshot, the loss limits' rule results, the [budget](#budgets) check, the token and cost totals, and the correlation ID, which links it to its logs and its `job_runs` row. Unique on agent and candle close |
| `cycle_roles` | Each role's state, model, whether the fallback answered, and its summary (FR-ROLE-08) |
| `ai_calls` | SRS 6.1's AiCall, one row per call (FR-UI-04, FR-PRM-03, FR-EXP-01), with the provider's own error message, which can quote the prompt, so it's never logged |
| `risk_evaluations` | SRS 6.1's RiskEvaluation: the intent, the caps, the effective settings, every rule's inputs, outcome and reason (FR-RISK-10), the sized order, and any loss limit reached |

- **Each step saves as it ends**, on a context of its own, so a crash loses only the step under way. A role's calls and its state save in one transaction, so every billed call is on record, which [budgets](#budgets) count.
- **The input snapshot keeps each series' latest candle**, the indicators, the prices, the account and the effective risk settings. The candles each role saw are in its prompt.
- **JSON columns write enums by name.** An AI call's output is in the contract's names, as the model would write it.
- **A trace holds the user's prompts and models' answers**, so only its user may read it, and never an administrator (FR-ADM-01). Agents' deletion reaches it through `IAgentHistory`, account deletion through #51, and retention through #50.
- **`CycleRecovery` marks a cycle a crash left running as `Interrupted`** at the next start, with its unfinished roles skipped, as the job runner does with its runs. It works in `StartAsync`, after the migrations and before any scheduled cycle.

### Errors

A cycle errored when it ended without a decision: `Failed`, or `Hold` because a required role failed. After `Agents:CycleErrorsBeforePause` errored cycles in a row, 3 by default, the agent goes to Error through `IAgents.EnterErrorAsync`. The agent gets an `AgentErrored` audit record by the system, and its user gets `AgentPausedErrors` (FR-NTF-01). Its positions and protective orders stay.

- **Only cycles since the agent last became Active count**, so activating it again starts afresh. A cycle that decides ends the run. An interrupted one doesn't count either way, because the application stopped it.
- **`0` turns it off.** Startup refuses anything but a whole number from 0 to 100, and names the setting (CFG-03).

### Progress

`CycleRun` keeps the roles' states in memory, and after each save tells every `ICycleObserver` through `CycleObservers`, which resolves them on first use and logs one that throws. An observer hears every user's cycles, on the cycle's own thread, so it filters and returns at once. Events are lost on a crash, so a page reads `IDecisionTraces.FindLatestAsync` when it opens. A summary is the analyst's own, or the action or verdict with its rationale, cut to 600 characters. A model wrote it, so it's only ever shown as text.

### Testing it

`CycleApp` in `tests/Ecnanib.Tests/DecisionCycle` builds on `AgentApp`, with Binance's candles and prices, and a model on both test providers. The model answers each role from the test's `Replies`, by the role in the request, or with a Spot market buy the Risk Critic approves and the engine passes. The sample candles move by about 3 an hour around 120, while the sample ticker is at about 60,123, so the buy's stop and targets are a few dollars from the ticker's ask, which keeps the stop within the engine's ATR bounds. Tests run cycles through `IDecisionCycles` themselves, and `Missing` takes candles away, as during maintenance. `BudgetTests` prices the test model through `Catalog:Overrides`, so a buy's cycle costs $6. It also moves the clock to noon mid-month, so that no day or month ends during a test unless the test moves the clock there.

## Web UI

`Components/` holds the interface, and every page renders inside `Layout/MainLayout`: a skip link, a header with the wordmark and `NavMenu`, `<main>`, and a footer. There's one layout because nothing needs a second yet, including `/account`, the first signed-in page.

### Adding a page

1. Add the component to `Components/Pages`, with a `<PageTitle>` and an `<h1>`, which is where focus lands after navigating. It's [private by default](#access-control), so a page for visitors adds `[AllowAnonymous]`, and goes on the list in `AccessTests`.
2. Put each section in `.page`, which sets the width and gutters. Everything else goes in the component's isolated CSS.
3. Put its text in `Components/Strings.resx`, and write `@Strings.Key`.
4. Add its link to `Layout/NavMenu.razor`. Link only to pages that exist, so a link appears in the same change as its page.

### Text

All user-facing text is in `Strings.resx` (EXT-UI-02). The build generates the `Strings` class from it, so a missing key doesn't compile, where `IStringLocalizer` would show the key to the user. A translation is a `Strings.<culture>.resx` beside it, plus a way to choose the culture, which version 1 doesn't need. `lang` on `<html>` comes from the same file, so it follows the text.

- **A sentence with an element inside it is one value,** with `{0}` where the element goes, so a translation can move it. Split it around `{0}`, as `ReconnectModal` does, because `MarkupString` is banned.
- **A key can't be named `Culture` or `ResourceManager`.** Those are the generated class's own members, so the key is skipped with a warning, which fails CI.
- **Razor encodes everything outside ASCII, and apostrophes,** so a test that reads a page has to decode it first.

`UserFacingText` scans compiled components, as [`BannedApis`](#owasp) does, and `UserFacingTextGuardTests` proves it against broken fixtures. It reads literal text in markup and child content, and `aria-label`, `title`, `alt` and `placeholder` on elements and components. **It can't see these, so check them in review:**

- A CSS `content:` value. That's why the template's `.blazor-error-boundary` rule was removed, so an `ErrorBoundary` now shows nothing until you give it `ErrorContent`.
- A string passed to any other component parameter, because most of those are routes, classes and selectors.
- Text in a field, a helper method or a script.

### Notices

`Notices/MandatoryNotices.razor` holds the three EXT-UI-03 notices. The landing page shows them directly below the hero, and the footer repeats the short form on every page, which is what CON-14 asks for. `LandingPageTests` and `ShellTests` check the exact phrases, so rewording one fails a test. Their wording is a draft until the legal review (#58).

CON-14 also rules out imitating Binance's trade dress, which no test can judge, so **check it in review**: the name stays in title case, nothing uses yellow, gold or amber, and no mark uses diamonds or rotated squares.

### Theme and layout

`wwwroot/app.css` holds the tokens. Each color is `light-dark(light, dark)`, so the side follows `color-scheme`. `App.razor` renders the user's [theme](#preferences) as `data-theme` on `<html>`, and `app.css` sets `color-scheme` from it: one side for Light or Dark, and `light dark` for System, where the operating system's theme applies live with no script (FR-PREF-01, FR-PREF-02). The `color-scheme` meta tag, rendered from the same preference, covers the moment before the stylesheet loads, so the first paint is already right (FR-PREF-03).

- **Use a token, never a color.** A fixed background under a dark `color-scheme` leaves light text on it, which is what the template's reconnect modal did.
- **Keep green and red free** for profit and loss, and don't use color alone for direction (FR-ANI-03).
- **Breakpoints are phone under 600px, tablet from 600px and desktop from 1024px.** A media query can't read a variable, so each stylesheet repeats them.
- **Fonts are in `wwwroot/fonts` with their OFL licences:** Zilla Slab for headings and IBM Plex Mono for records and data. Body text uses `system-ui`. Add a weight only when a page needs it, because each is a download.

### Motion

`App.razor` renders the user's [animation level](#preferences) as `data-motion` on `<html>` (FR-PREF-04), and `app.css` turns it into two numbers:

| Level | `--motion-travel` | `--motion-time` |
|---|---|---|
| Full | 1 | 1 |
| Reduced | 0 | 1 |
| Off | 0 | 0 |
| System | 0 when the operating system asks for reduced motion, otherwise 1 | 1 |

- **Animate through them.** Multiply a distance, or a scale's change, by `--motion-travel`, and every duration and delay by `--motion-time`, as `Layout/ReconnectModal.razor.css` does. Reduced then keeps the fades and drops the movement, and Off ends everything at once (FR-ANI-07), with no rule per level. Don't read `prefers-reduced-motion` anywhere else: System already does.
- **An animation that takes no time still ends.** At Off it jumps to its last frame, or to the element's own style if it has no fill mode. Give an infinite animation a style that stands still, as the reconnect modal's ring has.
- **Custom properties reach `@keyframes` and isolated CSS,** because they inherit from `:root`. `::backdrop` only inherits them in browsers from 2024 on, and an older one skips the backdrop's fade.

### Links and focus

Blazor's events can't drive the shell's links: `@onclick` does nothing until the circuit connects, and the error page never has a circuit. So `wwwroot/shell.js` has one click listener, in the capture phase, which runs before `blazor.web.js`. A link in an open popover closes it. A link to an element on the current page focuses that element, because `blazor.web.js` only scrolls to it, and focus would stay behind.

- **`<base href="/">` makes `#x` mean the landing page.** The script treats a bare `#x` as the current page, so use that only for something on every page, such as `#main`. Write `/#x` for a section of the landing page.
- **A link target needs `tabindex="-1"`,** or it can't take focus.
- **The menu is a native popover,** which brings Esc, click-outside and focus return. From 1024px, CSS undoes the popover and shows it as a row. A menu opened before the circuit connects closes when it connects, because Blazor replaces the prerendered DOM. That window is short, so this is accepted.
- **`FocusOnNavigate` is only rendered after the first page change,** because it would otherwise pull focus past the skip link when the circuit connects.

### What was checked by hand

Layout and focus need a browser, and CI doesn't have one. These were checked in Chromium and WebKit, in Development and in a published build over TLS:

- No horizontal scroll at 375, 768 and 1280px, in both themes, with no console errors
- The skip link, the menu, and links from another page, including with `blazor.web.js` blocked, which is how the error page behaves
- The theme following the operating system live, and the fonts loading under the CSP

Accounts were checked in Chromium and WebKit in Development, over plain HTTP rather than a published build over TLS. `CookieTests` covers the Production cookie flags:

- Registering, signing in, and signing out from a live circuit and from the phone menu, whose antiforgery token reaches the page through the circuit
- A link from a circuit to a static page, a static page back to an interactive one, and typing on a form page
- A circuit refusing a private page for a visitor, and sending a user with a document to accept to the right page
- Lockout, re-acceptance and the bootstrap secret end to end
- No horizontal scroll on the account pages at 375, 768 and 1280px, in both themes, with no console errors

Sessions were checked in Chromium and WebKit in Development, with the rate limits as shipped. `CircuitAccessTests` covers what a client that isn't a browser can try:

- Revoking a session sends its other device to sign-in with nobody touching it, and the list updates
- Signing out sends another tab to sign-in, and a password change signs out the other device and keeps this one
- A dropped connection rejoins a signed-in page without reloading it, and reloads a visitor's
- With a 1-minute idle timeout, navigating inside a circuit keeps a session alive with no document requests, and a silent tab leaves by the next job run
- No `Error` in the log through any of it
- No horizontal scroll on `/account` and `/account/password` at 375, 768 and 1280px, in both themes, with the longest user agent that's stored

The audit trail was checked in Chromium and WebKit in Development, with two browsers on one account:

- A failed sign-in, two sign-ins, revoking the other session from `/account`, changing the password and signing out each appear on `/account/activity`, newest first, with the device that did them. The revoke runs on a circuit, so that's the connection's address and user agent
- "Show older activity" moves focus to the first record it adds, and goes after the last page
- No horizontal scroll on `/account/activity` at 375, 768 and 1280px, in both themes, and no console errors beyond the noise below
- `UPDATE` and `TRUNCATE` on `audit_events` are refused in psql

Email, verification and resets were checked in Chromium and WebKit in Development, over plain HTTP, with Mailpit as the SMTP server and one instance each with email on and off:

- Registering emails a link from `App:PublicBaseUrl`. Opening it as a visitor goes to sign-in and back, and confirming it verifies the address. A listed address becomes an administrator
- The account page's button sends a link on the circuit, and pressing it again within minutes says one was just sent, without sending another
- Forgot password, the emailed reset, and signing in with the new password. The used link then says it has expired
- With email off: the forgot-password page sends users to an administrator, an administrator confirmed with the bootstrap secret issues a link from the account page, and the user resets with it
- No horizontal scroll on the new pages, the account page with its verification block, and the administrator's page showing a link, at 375, 768 and 1280px, in both themes
- No console errors beyond the screenshot noise below, no `Error` in either log, and no token or address in the logs

Preferences were checked in Chromium and WebKit in Development, over plain HTTP, with Playwright emulating the operating system's color scheme, reduced motion and time zone:

- Visitors and users on System follow the operating system's scheme both ways with no reload. Light under a dark scheme, and Dark under a light one, are right from the first response, including with both stylesheets blocked, which leaves only the meta tag
- `--motion-travel` and `--motion-time` resolve as the [table](#motion) says for every level, and System follows reduced motion live. Opened by hand, the reconnect modal slides at Full, stays still at Reduced, and its animations take no time at Off
- `shell.js` writes the time zone cookie, `/account` shows the browser's zone until the user chooses one, then the chosen one, and the preferences page names the browser's zone
- No horizontal scroll on `/account/preferences` at 375, 768 and 1280px, in both themes, no console errors beyond the noise below, nothing above Warning in the log, and no cookie value in it

Notifications were checked in Chromium and WebKit in Development, over plain HTTP, with Mailpit as the SMTP server and notifications written into the table, because nothing raises them yet:

- The menu counts on a page load, and again after navigating inside the circuit. "Mark all as read" clears it without a reload and moves focus to its status message
- Following a notification's link marks it read, and the menu follows, both to an interactive page inside the circuit and to a static page that loads in full
- A subject with markup shows as text, and Demo is labelled
- The email choices show the defaults ticked, save, and are stored as names, and the email arrived within a minute as one message, with links on `App:PublicBaseUrl`
- No horizontal scroll on `/notifications` and `/account/preferences` at 375, 768 and 1280px, in both themes, and no console errors beyond the noise below
- WebKit ticked checkboxes in green, which is kept for profit and loss, so `.check` boxes now use `--accent`. That changes the registration page's boxes too

API keys were checked with Playwright in Chromium and WebKit, in Development over plain HTTP, against a local stand-in for Binance and the AI providers rather than the real servers:

- Adding AI and Binance keys from the static forms, with a refused AI key and a Binance key that can withdraw each answered on the form, and neither key nor secret written back into the page
- The list's statuses, the Unverified explanation, the Demo label, and a Live key's IP warning with both egress addresses
- "Check now" answers that a key was checked less than a minute ago. "Delete" moves focus to its confirmation, "Keep it" moves it back, and deleting moves it to the notice that says so
- Renaming and replacing a key on its static page, and the summaries on `/account/activity`
- No horizontal scroll on `/credentials` and `/credentials/add` at 375, 768 and 1280px in both themes, nor on a key's page and `/account/activity` at 375 and 1280px. No `Error` in the log, and no console errors beyond the negotiation noise below
- Not checked: real keys against Binance or a provider, including whether Binance's Demo servers answer `apiRestrictions`

The model catalog's additions to `/credentials`, the **Sync models** button and each AI key's models, haven't been checked in a browser. `CredentialPageTests` covers what they render, but not their layout at 375, 768 and 1280px.

Agents were checked with Playwright in Chromium and WebKit, in Development over plain HTTP, against a scratch database with a Binance Demo key, an AI key, models and symbols written into it, and Binance and the providers pointed at an address that refuses connections:

- Creating an agent from the settings form, with markup in its name, which shows as text. Activating it, and a second agent on the same pair refused with what to fix, including the agent that holds the slot
- Clone, pause, stop and archive from the list, with stop and archive asking first and focus moving to the confirmation, then to the notice when the card leaves. The archived section, and the activity page's records
- Deleting a key from `/credentials` names the agents that use it
- Activation still succeeds when the balance for FR-AGT-13 can't be read, which logs a Warning and gives no warning on the page
- No horizontal scroll on `/agents`, `/agents/new`, an agent's settings page and `/account/activity` at 375, 768 and 1280px, in both themes, and no console errors beyond the noise below
- Not checked: a real Binance Demo key, a real provider's models, and the pause after a scheduled check, which `AgentCredentialTests` covers

The model catalog's pages were checked with Playwright in Chromium and WebKit, in Development over plain HTTP, against a scratch database with models, two of a user's AI keys, their access rows and some sync runs written into it, and the providers and Binance pointed at a closed port:

- As an administrator, hiding a model, overriding a name, two capabilities, a price and recommended roles, and setting one role to no default and another to a model, each from its form, with the notices after and an audit record each. A lone price and a model ID with a space are refused on the form, and nothing changes
- **Sync every key** answers that the sync started, records it, and its runs appear. The scratch keys had no secrets, so each failed and logged an Error, which a real key wouldn't
- `/catalog` leaves out the hidden model, filters by provider, capability and status, and lists a label with markup as text
- No horizontal scroll on `/catalog`, `/admin/catalog` and a model's page, including one with a 76-character ID, at 375, 768 and 1280px, in both themes and both engines, and no console errors. The tables scroll inside their own regions on a phone
- Not checked: real keys, and a screen reader on the scrolling regions

Playwright's screenshots inject a style that the CSP refuses, so WebKit logs an error per screenshot. Leaving a page while its circuit is negotiating also logs one, because the fetch is cancelled. Signing out does the same, more loudly in WebKit: it closes the session's connections, including that tab's, so the tab tries to reconnect while it's leaving. The reconnect modal wasn't open on a single frame in either browser. None of these is a fault in the application.

The reconnect modal was checked in the published build, by stopping the server. Every text color is at least 6.5:1 against its background. A change to the shell or `shell.js` needs this check again until there's a browser test. On macOS, WebKit's Tab skips links by default, so use Option+Tab there.

## Constraints for later issues

- **The UI (#15, #41, #42, #52, #53):** no inline scripts, `<style>` blocks, `style=` attributes or `onclick=` handlers: the [CSP](#headers) blocks them all, and the failure is a console message and a silently broken page. Use classes, isolated CSS and script files. `Virtualize` renders `style=` attributes, so the first change that uses it adds `style-src-attr 'unsafe-inline'`, which is narrower than loosening `style-src`.
- **Animation (#52):** animate through `--motion-travel` and `--motion-time`, as the reconnect modal does, and each animation follows the user's level and the operating system's reduced motion setting with no rule of its own (FR-ANI-07, AC-06). See [Motion](#motion). Off ends everything that uses `--motion-time`, so an animation that's essential needs another way, and a reason.
- **Times (#41, #42 and every later page):** write each time through [`DisplayTime`](#time-zones), and give a chart `DisplayTime.Zone` for its axis. The account pages already do.
- **Switching to Live (#39):** render `MandatoryNotices` in the flow (EXT-UI-03). Require `IEmailVerification.IsSatisfiedAsync`, which is true when email is off or the address is verified (FR-AUTH-03, FR-AGT-05).
- **Legal review (#58):** review the notices in `Strings.resx`, which are drafts. If you change a phrase that EXT-UI-03 quotes, change the tests that check it. Replace the placeholder documents and `LegalDraftNotice`, set both versions in `LegalDocuments` to `1`, and update the digests in `LegalDocumentTests`. Everyone then accepts again, which is right, because nobody has seen the real text. Rule on whether agents keep trading for a user who hasn't re-accepted, and on keeping proof of acceptance after account deletion, which FR-AUTH-09 would remove.
- **Kill switch (#37):** call `ITwoFactor.CheckAsync` behind the Agents contract, on the circuit, before stopping anything. It has its own throttle, which the sign-in lockout can't trip. After five wrong codes it pauses authenticator codes, but recovery codes still work, so the page should say so when the result is `Throttled`.
- **Switching to Live (#39):** call `ITwoFactor.CheckAsync` as above. Add `Security:RequireTwoFactorForLive` from SRS 7.3, and when it's true, refuse a user for whom `ITwoFactor.IsEnabledAsync` is false (FR-AGT-05).
- **The dashboard (#41):** [idle means a user action](#idle-means-a-user-action), so a live page times out when nobody touches it. Don't call .NET from a timer in JS, because that would count as activity.
- **Administrators and account deletion (#47, #51):** add a revoke-all to `ISessions` for FR-ADM-01, built on `Sessions.DeleteAllAsync`, and call it before deleting a user. The foreign key removes the rows, but only `SessionConnections.End` closes live connections, and [a circuit's connection](#ending-a-circuit) would otherwise run until its next user action. `account_tokens` goes with the user through its foreign key.
- **User administration (#47):** use `IAdministrators` and `AccountPolicies.Administrator`, behind contracts as well as on pages. Add a way to remove an account, because a stranger who registers a listed address holds it until then. Link a user's page to `/admin/password-reset`, which issues reset links with email off. Decide whether an administrator can turn off a user's two-factor, for someone who has lost both their phone and their recovery codes. With email off, an administrator can already issue a reset link, so the two together would let an administrator take over any account: if it's added, audit it, and consider requiring the bootstrap secret.
- **Audited actions (#39, #47, and the agent, approval and emergency pause issues):** record each FR-AUD-01 action with `IAuditTrail.Add` on the context that makes the change, in the same transaction. Add its `AuditAction` value and its text in `ActivityText`. Set `UserId` to the account it's about, and the actor to whoever did it: an administrator acting on someone else's account, or the system for a job or a risk limit (FR-AGT-04). A before/after summary in `Changes` shows on the activity page, so give each field its text in `ActivityText.FieldOf`, or it's left out. See [Audit](#audit).
- **Account deletion (#51):** FR-AUTH-09 keeps audit records with the identity anonymized, and the [trigger](#append-only) refuses every `UPDATE`. Replace `audit_events_append_only()` in a new migration, so that an update can set `user_id`, `actor_id`, `ip_address` and `user_agent` to null and change nothing else, and add a test that it can't do more.
- **Audit search (FR-ADM-06):** add a search to `IAuditTrail` and a page through Administration, which may use Platform. Show actor IDs and correlation IDs there, which `IAccountActivity` leaves out. Index whatever it filters on. The catalog's records have no user, so no activity page shows them yet: give `CatalogAuditFields` their text in `ActivityText.FieldOf` then.
- **Raising notifications (FR-NTF-01):** raise each through `INotifications`, with `Add` on the context that makes the change it's about, so both commit together. Link to the page it's about, and set `Demo` for a Demo agent. See [Notifications](#notifications). Who raises what, by what `ModuleMap.cs` allows:
  - Credentials: `CredentialInvalid` is raised already, when a check finds a key stopped working or lost a market. See [Checking on a schedule](#checking-on-a-schedule)
  - Catalog: `CatalogSyncFailed` is raised already. See [Syncing](#syncing)
  - Agents: `ModelDeprecated` and `ModelRetired` are raised already, by `ModelLifecycle`. See [Acting on retired models](#acting-on-retired-models)
  - Agents: `AgentPausedLossLimit` is raised already, by `Agents.PauseForLossLimitAsync`. See [Loss limits](#loss-limits)
  - Agents: `AgentPausedErrors` is raised already, by `Agents.EnterErrorAsync`, which the decision cycle calls. See [Decision cycle](#decision-cycle)
  - DecisionCycle: `AgentPausedBudget` is raised already, when a cycle finds the agent's AI budget has run out (FR-CYC-07). See [Budgets](#budgets)
  - Execution (#34, #35, #36): `TradeOpened`, `TradeClosed` (including a close outside Ecnanib, FR-EXE-06) and `StopMoved`
  - Agents (#37): `KillSwitchExecuted`
  - DecisionCycle (#38): `ApprovalPending` and `ApprovalExpired`, linking to the approval page, which needs sign-in like every page (FR-APR-02)
  - Execution: `ExchangeRateLimitBan` is raised already, by `ExchangeBanNotifier`. See [Bans](#bans)
- **Live updates (#41):** push a new notification to the menu's count and an open list. Today they only change on navigation.
- **Retention (#50):** the SRS sets no period for `notifications`, so decide one, and purge in batches by `created_at`. With email on, the email job adds a `job_runs` row a minute, about 1,440 a day, plus a `send-email` row per message.
- **AI adapters (#19):** implement `IAiProviderAdapter` for each provider and register it in `AddAiAdapters`. Add its section to `appsettings.json` with the official base URL, and to the test settings in `AppFactory`, because every adapter needs its section. Put the key in a header, never the URI: Gemini's is `x-goog-api-key`. Count reasoning and thinking tokens as output. Set `Refused` for each provider's refusal, such as Anthropic's `refusal` stop reason, OpenAI's `refusal` content, and Gemini's blocked prompt or `SAFETY` finish, and set `Truncated` at the output limit. Use `ReadError` where the status misleads, such as for OpenAI's `insufficient_quota`, which is a 429 that waiting won't fix, so map it to `Other`. See [AI adapters](#ai-adapters).
- **System health (#49):** show `IMasterKeyRotation` for FR-ADM-04's credential re-encryption progress.
- **Account deletion (#51):** delete a user's secrets through the modules that own them, in the transaction that deletes the rows that refer to them. Nothing else can find them. That includes the TOTP key in `totp_authenticators.secret_id`: the row cascades with the user, but the secret doesn't.
- **Data export (#51):** include the user's preferences from `IPreferences.GetAsync`, and their email choices from `IPreferences.GetEmailNotificationsAsync` (FR-DATA-03). Deleting the account deletes their row through its foreign key.
- **Account deletion (#51):** delete the user's `notifications` through the port Identity declares, which Notifications implements. There's no foreign key to do it.
- **Exchange endpoints (#35):** add each one to `BinanceEndpoint` with its weight, and call it through `BinanceTransport`, so it shares the budget, the clock, the outcomes and the logging. Put reads on `IExchangeAccount` or `IExchangeMarketData`, and anything that changes the account, as the margin type and leverage do, on `IExchangeTrading`, which only Execution may use. See [Exchange adapter](#exchange-adapter).
- **Reconciliation (#35):** take the liquidation price from the position it reads, because Market Data has no keys. Fill `AgentAccount.Position` and `HasPendingEntry` in `AgentAccounts` from Execution's records, add the position's opening time, and pass it to the Strategist and Risk Critic as a `PromptPosition`, with the pending entry as a `PromptPendingEntry`: `DecisionCycles` passes none yet.
- **The wizard (#40):** fill the roles step from `IAgents.ListRoleChoicesAsync`, whose `Defaults` a user can accept as they are, and show the activation warnings from `AgentText`, including FR-ROLE-04's. Create through `IAgents.CreateAsync`, which checks the pair with Binance and takes only Demo and perpetual Futures contracts, and replace `/agents/new`. Offer pairs from `IExchangeSymbols.ListAsync`, which `AgentSettingsPage` doesn't. Its risk hints in `AgentText.RiskHintOf` are a start on NFR-USA-03. Activate from the last step with `IAgents.ActivateAsync`, and show its problems and warnings with `AgentText`.
- **Reconciliation (#35):** read a position with `IExchangeAccount.GetPositionAsync` for the agent's symbol, not the whole account (NFR-PERF-05). It's empty when there's no position, and its liquidation price is null when Binance sends 0. Check `positionRisk`'s weight and fields against Futures Demo first, because its page couldn't be read.
- **Retention (#50):** purge `candle_cache` by `fetched_at`, using `Retention:CandleCacheDays`, never by `open_time`, which would make every 1D cycle fetch its whole window. Batch the deletes. `exchange_symbols` needs no purge, because it only ever holds Binance's latest list.
- **Shadow outcomes (#44):** the candles after a proposal are in `candle_cache` for `Retention:CandleCacheDays`, but nothing reads a range after a time yet, so add that read to `IMarketData`. Use `Indicators` for any indicator, so the numbers match what the cycle saw.
- **Capacity (#56):** start from [Request weight per candle](#request-weight-per-candle).
- **Order execution (#34):** derive client order IDs that match `^[.A-Z:/a-z0-9_-]{1,36}$` (FR-EXE-04). Use `RequestPriority.Protective` for stops, closes and their queries. On `MayHaveExecuted`, query with `IExchangeAccount.GetOrderAsync` before sending again: only null allows it. Persist `SettlesAt` with the order, and don't send it again before then, even after a restart, which the adapter's memory doesn't survive. Check the Futures algo order parameters against Demo first, and add Spot OCO if it's wanted.
- **System health (#49):** show `IExchangeUsage.Current()` for FR-ADM-04's exchange usage and bans, at `/admin/health`, which ban notifications already link to.
- **Suggest assignment (#46):** start from `RoleModels`, which already filters and ranks each role's models, and replace "newest first" with the trading-experience metrics (FR-ROLE-07). Keep the Risk Critic off the Strategist's provider (FR-ROLE-04).
- **Anthropic's model lifecycle:** Anthropic's list also has `lifecycle`, `deprecated_at` and `retires_at`, which the adapter doesn't read yet. Reading them into `AiModel` would let the catalog deprecate a model before it disappears, ahead of [Deprecation and retirement](#deprecation-and-retirement)'s rule, and warn its agents sooner.
- **Switching to Live (#39):** `AgentInput` and `ActivationChecks` refuse Live, and `AgentForm.ToSettings` always sends Demo. Make the switch an action of its own (UC-04), for a Paused or Stopped agent with no open positions or orders, with its checks on top of [Activation](#activation)'s, and audit it with its own `AuditAction`. Add `Agents:DefaultEnvironment` from SRS 7.3 then. The slot and the exchange key's environment already follow the agent's environment.
- **An agent's history (#34, #36, #45):** implement `IAgentHistory` for each table that keeps an agent's history, such as orders, trades and reflections, as `AgentCycleHistory` does, so deleting an agent leaves nothing behind.
- **Order execution and reconciliation (#34, #35):** replace `AgentExposure.HasOpenPositionsOrOrdersAsync`, which is `false` while nothing places orders, with what Execution records. Answer from the database, without calling Binance, because Agents asks while it holds the user's lock. FR-AGT-03's lock and archiving both depend on it.
- **Stop and the kill switch (#37):** `IAgents.StopAsync` leaves positions and orders as they are. Add FR-AGT-07's choice to close them, and the kill switch, through `IAgentExposure` or a port beside it that Execution implements. "Stop all agents" should go through the user's agents one at a time, so one failure doesn't stop the rest.
- **The agent's page (#41):** `/agents/{id}` is free. Link the list's names to it, and move the actions there as well, using the same contract calls and `AgentText`.
- **Account deletion (#51):** stop the user's agents first, with their choice to close or keep positions (FR-AUTH-09), then delete them through the port Identity declares. There's no foreign key to `users`.
- **Execution (#34, #35):** open a key with `ICredentialKeys` for each use, never cache it, and dispose its secrets after, as `AgentAccounts` does. `CredentialUnusableException` means the credential changed since the agent was activated: treat it as a paused agent's cause, not a provider failure, and don't retry it. `BannedApis` lets only Catalog, AiCouncil and Execution use `ICredentialKeys`, and only Credentials build a key.
- **Binance Demo restrictions:** Binance doesn't document `/sapi` on its Demo servers, so a Demo key's restrictions aren't read. Try `apiRestrictions` against Demo with a real key, and if it answers, read them there too.
- **Account deletion (#51):** delete the user's credentials with `ICredentials.DeleteAsync`, which removes their secrets in the same transaction, through the port Identity declares. There's no foreign key to `users`.
- **Data isolation (#55):** `CredentialIsolationTests` and `AgentIsolationTests` cover every Credentials and Agents call. Add the credentials pages and `/agents/{id}/settings` to the HTTP suite. The settings page already answers 404 for another user's agent.
- **AI cost and budgets (#41, #42):**
  - Show FR-AGT-11's AI cost to date from `IAiSpending`, and add a longer period to it if "to date" means more than the month.
  - Show an `OverBudget` cycle's `budget` in the trace viewer.
  - The agent stays Active in protective-only mode, so the dashboard should say so beside the status, as the agent list does. See [Budgets](#budgets).
- **Output contracts:** changing a schema or a template means changing the other, and running `LiveProviderTests`. A seeded template change reaches only a database that has never had its key, so publish it as a new version (#48) once one exists. See [Output contracts](#output-contracts).
- **Charts:** FR-ROLE-02 allows the Technical Analyst an image. To add one, render the chart and pass it as an `AiImage` part of the first user message, in `Council`, for models whose catalog capabilities say they take images.
- **Reflections (#45):** fill `PromptContext.Reflections` for the Strategist and Risk Critic, newest first, up to the agent's count, leaving out excluded ones (FR-EXP-04, FR-EXP-05). They're untrusted content, so a user's edit can't become an instruction. The Reviewer's prompt needs the trade's lifecycle: add it to `PromptContext` then, as data, with any text a model wrote as untrusted. Keep the trade journal's R-multiple and P&L rather than the `TradeOutcome` the Reviewer copied, which the contract can't check.
- **Prompt versioning (#48):** add status, author and published-at to `prompt_templates`, and make the one in use the highest published version, so a draft never applies. Keep rows immutable, so a recorded ID always shows what was sent: a rollback publishes an old version's content as a new version. Preview with `PromptComposer.Compose`, passing the draft with the published templates and sample context, and sample custom instructions, because administrators may not see users' (FR-ADM-01). An override is a template with a narrower key ([Keys and overrides](#keys-and-overrides)). A change to a seed reaches existing databases only when it's published as a new version, because the seeder never touches a key that has a row.
- **Retention (#50):** the SRS sets no period for `catalog_sync_runs`, which gets a row per provider per scheduled run, plus one per key change and user sync.
- **Account deletion (#51):** delete the user's `model_availability` rows by `user_id`. `catalog_sync_runs.errors` holds credential IDs, and nothing else about anybody.
- **xAI prices:** confirm the unit of the prices in xAI's model list with `LiveProviderTests`, then read them into `AiModel`, below the configuration's.
- **The trade journal (#36):** replace `TradingRecord.None` in `DecisionCycles`, for the loss limits and `RiskInputs.Record`. Count `TradingRecord.ConsecutiveLosses` from the agent's last `StatusChangedAt` into Active, or a resumed agent pauses again at once.
- **Futures entries (#34, #38):** call `IFuturesSetup.PrepareEntryAsync` right before each Futures entry is sent, with its `SizedOrder`, and send nothing unless it's `Ready` (FR-RISK-09). It requires One-way Mode, and sets the symbol to isolated at `SizedOrder.Leverage`, which is the agent's after the caps. Record any other outcome on the cycle: `HedgeMode`, `MultiAssetsMode` and `TradingDisabled` need the user, so tell them. An approval (#38) calls it again when it sends, because the account may have changed while it waited. Check the [Futures account settings](#futures-account-settings) against Futures Demo first.
- **Order execution and AC-01 (#34):** send only a `SizedOrder`, and record the evaluation it came from on each order row, the stop and target legs citing their entry's. Cancel only the unfilled entry for `CancelEntry`, never a protective order. Send Futures stops with `workingType=MARK_PRICE`, which `StopSide` assumes. On Spot, place the stop for the base quantity received after the fee, which Binance takes from it. On a rejection where `ExchangeException.IsFilterRejection`, call `IExchangeSymbols.RefreshAsync`. Evaluate the emergency close (FR-EXE-02) as a `Close` or `Sell` with `Origin = System`. Write AC-01's audit query: every order row has a passing evaluation.
- **The trade journal (#36):** sum `TradingRecord`'s P&L over `RiskPeriods.DayStart` and `WeekStart`, net of fees and funding. A loss is net P&L below zero, and any other trade ends a streak. `EntriesToday` counts entry orders placed, adds included. `CyclesSinceLastLoss` counts cycles finished since the losing trade closed.
- **The kill switch (#37):** evaluate each close as a `Close` or `Sell` with `Origin = System` and no fraction, which only the structural rules can stop, and send its `SizedOrder`.
- **Approvals (#38):** re-evaluate on approval with fresh inputs (FR-APR-03): the size and prices may change, and a failure rejects the approval with its reasons. Show `SizedOrder.MoneyAtRisk` as the money at risk (FR-APR-01).
- **The trace viewer (#42):** give `RiskRule`, `RuleOutcome` and `RiskReason` their text, and show each rule's inputs as recorded. The Settings rule's inputs show each capped setting, its cap and what was used.
- **Order execution and approvals (#34, #38):** take a `ReadyToExecute` cycle's sized order from its `risk_evaluations` row, and call `CycleRequest.AllowsNewEntry` again right before sending an entry, with the time then (FR-CYC-06). Record the outcome on the cycle: add `Executed` and `AwaitingApproval` to `CycleOutcome`, for FR-ANI-01's reveal.
- **Per-agent catch-up (FR-CYC-04):** each agent's last cycle is now in `decision_cycles`, so a restart could catch up the agents whose close's job ran but whose cycle never started. See [Scheduler](#scheduler).
- **Live updates and the council view (#41, #53):** subscribe an `ICycleObserver`, filter by the signed-in user, and read `IDecisionTraces.FindLatestAsync` when the page opens, because events are lost on a crash. A role's summary is a model's text, so show it only as text.
- **The trace viewer (#42):** extend `IDecisionTraces` with the whole trace: the input snapshot, each AI call with its prompt, answer, violations, tokens, cost and latency, and the Risk Engine's rules. Every query takes the user (NFR-SEC-04), and administrators never see it (FR-ADM-01).
- **Account deletion (#51):** delete the user's `decision_cycles` by `user_id`, through the port Identity declares. Their roles, AI calls and evaluations go with them.
- **System health (#49):** FR-ADM-04's queued and running cycles are in `CycleSlots`, in memory. Add a Scheduler contract that reports them, and the scheduler's status from the four `cycles-*` jobs' last runs.
- **Retention (#50):** every decision cycle adds a `decision-cycle` row to `job_runs`, about 19,000 a day at 200 agents on 15m, plus a row per timeframe per close. Purge `decision_cycles` by `started_at`, using `Retention:DecisionTraceDays` (FR-DATA-01): its roles, AI calls and evaluations go through their foreign keys. A trace keeps every prompt, so at 200 agents on 15m it's of the order of 1 GB a day before PostgreSQL compresses it. Most of a prompt's instructions are its templates', which the call's `templates` identify, so storing them once per version is the first saving if that's too much.
- **Order execution (#34):** pass `cancellationToken`, not `Stopping`, to exchange calls. Persist the intent before sending, or a cut send can't be recovered (FR-EXE-05, FR-EXE-08). Once an order is placed, save with a deadline of its own, or the end of the drain window could cancel the write.
- **Reconciliation (#35):** run the startup pass from a hosted service's `StartAsync`, through `IJobRunner`, so it finishes before any scheduled cycle (FR-EXE-08).
- **Retention (#50):** purge `log_events` by `timestamp`, using `Retention:DatabaseLogDays`, which FR-LOG-02 and FR-DATA-01 require, and `audit_events` by `timestamp`, using `Retention:AuditDays`. The audit trigger allows `DELETE` for this, and refuses `TRUNCATE`. `job_runs` needs purging too, and the SRS sets no period. Keep each job's newest row, or it runs early once.
- **Deployment (#57):** a supervisor's stop timeout must be at least 60 seconds: 45 for the host, plus the final log flush. systemd's default of 90 is fine, and Docker's 10 and Kubernetes' 30 are too short. A SIGKILL behaves like a crash. A reverse proxy must send `X-Forwarded-For` and `X-Forwarded-Proto`, pass WebSocket upgrades, and be in `Security:TrustedProxies` unless it's on loopback. Set `AllowedHosts` to the public host, plus whatever host a local probe uses. The configuration file must be closed to everyone but the service account, such as with `chmod 600`, or startup is refused outside Development. Kubernetes mounts files that group and others can read by default, so set the mode. Install time zone data, or users can only choose UTC: Alpine and chiseled images need it added. Cover Windows ACLs, which aren't checked, and the [rotation steps](#rotating-the-master-key). The first start after #12 moves the Data Protection keys into the database under a new application name, so everyone signs in again once.

Other interfaces are defined by the issue that needs them. So far there's `ISystemInfo` in `Modules/Administration/Contracts`, which is part of FR-ADM-04 for #49 to build on, and `ICatalogControls` beside it, `IAccountActivity`, `IEmailVerification`, `IPasswordResets`, `ITwoFactor`, `IPreferences`, `IEmailRecipients`, `EmailNotificationChoices` and `TimeZones` in `Modules/Identity/Contracts`, `INotifications` in `Modules/Notifications/Contracts`, `ICatalog`, `ICatalogSync`, `ICatalogAdministration`, `CatalogAuditFields` and `IModelStatusObserver` in `Modules/Catalog/Contracts`, the exchange contracts in `Modules/ExchangeAdapter/Contracts`, including `IExchangeUsage` for #49, `IMarketData`, `IExchangeSymbols`, `Indicators` and `MarketDataException` in `Modules/MarketData/Contracts`, the AI contracts in `Modules/AiAdapters/Contracts`, `IPrompts`, `PromptComposer`, `PromptRequest`, `PromptContext`, `ComposedPrompt`, `PromptTemplate`, `PromptTemplateKey`, `PromptWindows`, `OutputContracts`, `ContractContext`, `OutputContractException`, `TradeActions` and the five role outputs in `Modules/AiCouncil/Contracts`, `ICredentials`, `ICredentialKeys`, `ICredentialObserver` and `CredentialAuditFields` in `Modules/Credentials/Contracts`, `IAgents`, `IActiveAgents`, `IAgentExposure`, `AgentSettings`, `RoleChoices` and `AgentAuditFields` in `Modules/Agents/Contracts`, `IAgentHistory` in `Modules/Agents/Contracts`, `IDecisionCycles`, `CycleRequest`, `CycleProgress`, `ICycleObserver` and `IDecisionTraces` in `Modules/DecisionCycle/Contracts`, `ICouncil` in `Modules/AiCouncil/Contracts`, `IAgentAccounts` and `IFuturesSetup` in `Modules/Execution/Contracts`, `RiskSettings`, `RiskCaps`, `IRiskEngine`, `OrderIntent`, `RiskInputs`, `RiskEvaluation`, `SizedOrder` and `RiskPeriods` in `Modules/RiskEngine/Contracts`, the persistence, logging, correlation, job, audit, secret, scrubbing and email contracts in `Platform/Contracts`, including `IMasterKeyRotation` for #49, and `SecretValue`, `NotificationType`, `Market`, `TradingEnvironment`, `Timeframe` and `AiRole` in `Kernel`.

## What the boundary tests can't see

The tests scan compiled code, including method bodies, lambdas, async methods, iterators, local functions, generics, attributes and catch clauses. Razor components compile to the same code, so they're covered too. `ModuleBoundaryGuardTests` proves each of these against deliberately broken fixtures.

These leave no reference to scan, so **check them in review**:

- Constants and enum values from another module, which the compiler inlines
- `nameof` on another module's type
- A forbidden type used only as the generic argument of a static field access, such as `Holder<Forbidden>.Value`
- Reflection, or calling something by name in a string
- SQL against another module's tables
