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

The Web UI may use neither adapter nor `Platform`, so it can't reach secrets or place orders directly (2.4, FR-ADM-01, FR-KEY-09).

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

**After page load, a Blazor circuit doesn't pass through HTTP middleware.** Access rules therefore can't live there: ownership checks (NFR-SEC-04) belong behind module contracts. Rate limiting (NFR-SEC-07), correlation IDs (NFR-OBS-02) and audit fields such as IP address need a plan for circuit traffic too. Request logging doesn't: FR-LOG-06 covers HTTP, so a circuit logs one `/_blazor` request, and activity inside it is logged where it happens, behind module contracts.

## Persistence

`EcnanibDbContext` in `Platform/Contracts` is the one context, with one linear migration chain, because the SRS has one database and one migration version (FR-ADM-04). The code-first model owns the schema, so nobody changes it by hand (DATA-R-05).

- **Modules own their entities.** A module keeps its entities and their `IEntityTypeConfiguration` internal, and reaches them with `Set<T>()`, so the boundary tests cover them. The context finds configurations with `ApplyConfigurationsFromAssembly`. This is the one exception to "no module scanning" in rule 3: contributing the model through DI would leave `dotnet ef` with an empty model at design time, and it would generate drops.
- **Reference another module's rows by ID only**, with no navigations. Whether a foreign key constraint can cross modules isn't decided: `HasOne<OtherModuleEntity>()` fails the boundary tests, so the first issue that needs one decides.
- **Contexts are short-lived.** A Blazor circuit keeps its scope for as long as the user stays connected, so the context isn't in DI, and a service that injects it fails at startup. Inject `IDbContextFactory<EcnanibDbContext>`, and dispose each context when the operation ends.
- **The caller owns the context, which keeps writes atomic.** Anything that must commit with a caller's change, such as an audit record (#11), takes the caller's context, adds to it and doesn't save. One `SaveChangesAsync` then commits both (FR-AGT-04, NFR-REL-04). Across several saves, use one transaction on that context. Never hold a transaction open across an exchange call. Leave `EnableRetryOnFailure` off: it rejects transactions started this way.
- **Money and time follow SRS 6.2.** Store amounts as `decimal` next to their asset or currency code, and timestamps as `DateTime` or `DateTimeOffset` in UTC. `SchemaRulesTests` rejects binary floating point and timestamps without a time zone on any mapped column. It can't check that an amount has a code column, so check that in review.
- **Seeders add reference data (NFR-DEP-02).** Implement `IReferenceDataSeeder` and register it in `Add<Module>()`. Seeders run in registration order on every start, so they add only what's missing, and they don't save.

### Startup

`DatabaseStartup` is a hosted service that runs before the web server opens a port. It applies pending migrations, then runs the seeders. Any failure is logged and stops the process with a non-zero exit code (NFR-DEP-03).

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
- Set the ambient fields with a scope, using the names in `LogProperties`. Scopes flow with the async call, so code further down inherits them, including the adapters, which can't reference `Platform`:

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

## Constraints for later issues

- **Correlation IDs (#5):** set `LogProperties.CorrelationId` in a scope. `log_events.correlation_id` is already there and indexed.
- **Audit (#11):** must sit below every module that writes to it, and take the caller's context. See [Persistence](#persistence).
- **Sign-in and email links (#9, #13):** put tokens in the query string as `token=`, never in the path, so the text rules can see them. Set `LogProperties.UserId` in a scope.
- **Secrets (#12):** must sit below Identity and Credentials, and keep secret access separate from credential metadata (#20). Wrap decrypted secrets in a type that doesn't render its value: see [Masking](#masking).
- **Exchange adapter (#17):** separate read and trade surfaces, so only Execution can place orders (AC-01), and share one request-weight budget (EXT-BIN-04, FR-EXE-09). Log FR-LOG-08 events under the `Exchange` category.
- **AI adapters (#18):** no `AiProvider` enum. Providers are data, and an enum would break NFR-MNT-02. Log FR-LOG-07 events under the `AiCouncil` category.
- **Risk Engine (#27):** see rule 4.
- **Decision cycles (#30, #31):** set `LogProperties.AgentId`, `CycleId` and `Environment` in a scope around each cycle.
- **Retention (#50):** purge `log_events` by `timestamp`, using `Retention:DatabaseLogDays`, which FR-LOG-02 and FR-DATA-01 require.

Other interfaces are defined by the issue that needs them. So far there's `ISystemInfo` in `Modules/Administration/Contracts`, which is part of FR-ADM-04 for #49 to build on, and the persistence and logging contracts in `Platform/Contracts`.

## What the boundary tests can't see

The tests scan compiled code, including method bodies, lambdas, async methods, iterators, local functions, generics, attributes and catch clauses. Razor components compile to the same code, so they're covered too. `ModuleBoundaryGuardTests` proves each of these against deliberately broken fixtures.

These leave no reference to scan, so **check them in review**:

- Constants and enum values from another module, which the compiler inlines
- `nameof` on another module's type
- A forbidden type used only as the generic argument of a static field access, such as `Holder<Forbidden>.Value`
- Reflection, or calling something by name in a string
- SQL against another module's tables
