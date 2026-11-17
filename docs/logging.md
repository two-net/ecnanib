# Logging

Ecnanib writes structured log events: each one has a message template with named properties, a level, and the properties of the context it was written in. Levels and outputs come from the `Logging` section of the configuration file and from nowhere else (NFR-LOG-1 to -6).

The library is Serilog, behind the host's `ILogger<T>`. Only `Platform` uses Serilog directly (`src/Ecnanib/Platform/Logging`). The rules below are enforced by tests in `tests/Ecnanib.Tests/Logging` and `tests/Ecnanib.Tests/Architecture`.

Logs are not the audit trail. The audit trail is stored in the database: see "The audit trail" in [accounts.md](accounts.md).

## Configuration

```json
"Logging": {
  "MinimumLevel": "Information",
  "Overrides": {
    "Microsoft.AspNetCore": "Warning",
    "Microsoft.EntityFrameworkCore": "Warning"
  },
  "Outputs": {
    "Console": {},
    "File": { "Path": "logs/ecnanib.log", "Rolling": "Day", "RetainedFiles": 30 }
  }
}
```

| Key | Meaning |
|---|---|
| `MinimumLevel` | Events below this level are dropped. One of `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`. |
| `Overrides` | A different minimum level for an area. An area is the start of a logger's name, which is the full name of the class that logs, and it covers everything below it. The longest area that fits wins. A name may contain one `*`. |
| `Outputs:Console` | Present: events go to standard output. Absent: they do not. |
| `Outputs:File` | Present: events go to a file. Absent: they do not. |
| `Outputs:File:Path` | The file. A relative path starts from the content root. |
| `Outputs:File:Rolling` | `Day`: a new file each day, with the date before the extension, such as `logs/ecnanib20261111.log`. |
| `Outputs:File:RetainedFiles` | How many files are kept, today's included. Older ones are deleted. |

The host's default outputs (console, debug, event source) are removed, so an output exists only when the file has its key.

### Changing levels without a restart

Save the file with a new `MinimumLevel` or with overrides added, changed, or removed. The levels apply within a few seconds, to every logger, and the application logs them (NFR-CFG-7). To investigate one area, add an override for it and remove it afterwards:

```json
"Overrides": {
  "Microsoft.AspNetCore": "Warning",
  "Ecnanib.ExchangeGateway": "Debug"
}
```

An edit that makes the file invalid changes nothing and is logged as a warning. Outputs and every other key are read at the next start. See [configuration.md](configuration.md).

## Outputs

The console gets one line per event: the time in UTC, the level, the area, the message, and then the properties the message does not show.

```
2026-11-11 03:45:38.871Z [WRN] Ecnanib.Agents.Scheduler: Cycle was late by 1200 ms {"AgentId": "42", "CycleId": "9001"}
```

The file gets one JSON object per line, in the compact log event format (CLEF), so every property can be searched:

| Field | Meaning |
|---|---|
| `@t` | The time, in UTC. |
| `@mt` | The message template. |
| `@l` | The level. Left out for `Information`. `Trace` is written as `Verbose` and `Critical` as `Fatal`. |
| `@x` | The exception, with its stack trace. |
| `@tr`, `@sp` | The trace and span of the request. |
| `SourceContext` | The area: the name of the class that logged. |
| Anything else | A named property of the message or of a scope. |

## Writing events

Take `ILogger<T>` as a constructor parameter and write a template with named properties:

```csharp
logger.LogInformation("Order {ExchangeOrderId} for {Symbol} was filled at {Price}", orderId, symbol, price);
```

- Name every value. Do not build the message with `$"..."` or `+`: the values would be lost as properties.
- Write `{@Order}` to log an object with its members, and `{Order}` to log it as text.
- Do not use Serilog types. The architecture tests reject them outside `Platform`.

## Correlation

Events inside a request or a cycle carry the properties that say what they belong to (NFR-LOG-3). The names are defined once, by `Correlation` in `Platform/Logging`:

| Property | Set by |
|---|---|
| `RequestId` | `UseRequestCorrelation()`, for every HTTP request. It comes first in the pipeline. |
| `UserId` | `UseUserCorrelation()`, when the request is signed in: the id of the account. It comes right after authentication, which is what knows the user. See [accounts.md](accounts.md). |
| `Provider`, `Model` | The AI Provider Gateway, around each call to a model. `Provider` alone around a check of a key and a list of models, and by the Catalog around a sync. |
| `AgentId`, `CycleId`, `Role` | The component that runs a cycle and calls the team. The job of the Decision Engine that starts the cycles sets `UserId`, `AgentId`, and `CycleId` around each cycle: see [cycles.md](cycles.md). What runs a role sets `Role` around each call of it, so the events of the gateway carry it: see "A role call" in [team.md](team.md). |
| `ExchangeOrderId` | The component that places or tracks an order. |

Open a scope with the properties you know. Every event written until the scope is disposed carries them, whichever class writes it, across `await`:

```csharp
using (logger.BeginCorrelation(new Correlation(AgentId: agent.Id, CycleId: cycle.Id)))
{
    await RunTheTeamAsync(cycle, cancellationToken);
}
```

Scopes nest. Work that runs outside a request, such as a scheduled cycle, opens its own scope and logs its own failures inside it, so the error carries the agent and the cycle.

The scheduler that runs a component's jobs names the job in its own events, as `Job`. That is a property of those events, not a correlation property: it is not added to what the job logs. See [background-work.md](background-work.md).

## Masking

Secrets, API keys, tokens, and passwords never appear in a log (NFR-LOG-4). Every event passes one place, `MaskingSink`, before any output. It applies the rules of `SecretMask` to the event's properties at any depth, to its message text, and to the text of its exception:

1. **By type.** A `Secret` prints as `***`.
2. **By name.** A value logged under a sensitive name is replaced, whether the name is a property, a member of a logged object, or an entry such as a header. A name is sensitive when it says password, secret, signature, authorization, cookie, or credential, or ends in key or token: `ApiKey`, `X-MBX-APIKEY`, `access_token`. Counts and identifiers are left alone: `InputTokens`, `ActiveKeyId`.
3. **By value.** Every secret in the configuration file is replaced wherever its whole value appears.
4. **By shape.** Credentials in text are replaced: `Bearer ...` and `Basic ...`, pairs such as `password=...`, `signature=...`, and `key=...` in a connection string, a query, or a JSON body, the key formats of the AI providers, and signed tokens.

What the rules cannot recognise is a secret that is not in the configuration file, logged as bare text under a name that does not say what it is, such as a user's Binance key in `{Value}`. So:

- Keep a secret in a `Secret`, and call `Reveal()` only where it is used.
- If a secret must be named in an event, name it for what it is, such as `{ApiKey}`. It is then masked.
- Never log request headers.

`SecretMask.Holds` says whether a text holds a secret beyond doubt: by value, and by the key formats of the AI providers and signed tokens, without the rules that go by a word beside a value. It is asked where a finding refuses something, and masks nothing: what composes a prompt asks it, so that no prompt holds a secret (FR-PRM-9). See [prompts.md](prompts.md).

## Outbound calls

Every call to an AI provider or to Binance is logged with its duration, its outcome, and its usage (NFR-LOG-5).

**HTTP.** Each client from the host's factory logs every attempt, so an adapter gets this without writing any code. An adapter cannot create a client itself: the architecture tests reject `new HttpClient`.

```
Outbound call POST fapi.binance.com/fapi/v1/order Succeeded with status 200 in 84.2 ms {"Usage": {"X-MBX-USED-WEIGHT-1M": "12"}}
```

- The address is logged without its query, and headers are not logged.
- `Outcome` is `Succeeded`, `Failed` (a status of 400 or above, or no answer), or `Canceled`. A failed call is a warning.
- `Usage` holds the response headers that report usage. The component that knows the system names them: the Exchange Gateway registers Binance's used-weight and order-count headers, and the used-weight headers of Binance's wallet API, which have a name of their own, through `OutboundCallLogOptions`.
- Request and response bodies are logged only at the debug level, which is off unless the file turns it on for the area `Ecnanib.Platform.Adapters.Http`. Bodies pass the masking like everything else. While it is on, each body is held in memory in full, so use it to investigate and then turn it off.

The calls that the application makes so far are the ones that validate a user's key, the ones that list a provider's models for the catalog, the calls to a model, and the calls that read from Binance: see "Validating a key" in [credentials.md](credentials.md), [catalog.md](catalog.md), [ai-providers.md](ai-providers.md), and [exchange.md](exchange.md). A key travels in a header, and the signature of a call to Binance in the query, so neither is logged. A provider that refuses a key answers with a status of 400 or above, so such a call is a warning: it is what the provider said, and not a fault of the application.

```
Outbound call GET api.anthropic.com/v1/models Failed with status 401 in 324.5 ms {"Provider": "claude"}
Outbound call GET api.binance.com/sapi/v1/account/apiRestrictions Succeeded with status 200 in 231.5 ms {"Usage": {"x-sapi-used-ip-weight-1m": "1"}}
```

**AI calls.** The AI Provider Gateway also logs each call to a model, with what HTTP cannot see: how the call ended for its caller, and the token usage.

```
AI call Succeeded in 2310.4 ms, using 1840 input and 212 output tokens {"Provider": "claude", "Model": "...", "AgentId": "42", "CycleId": "9001", "Role": "RiskManager"}
AI call InvalidResult in 9120.7 ms, using 1840 input and 16000 output tokens {"Provider": "claude", "Model": "...", "AgentId": "42", "CycleId": "9001", "Role": "Strategist"}
AI call TimedOut in 60000.3 ms {"Provider": "gemini", "Model": "...", "AgentId": "42", "CycleId": "9001", "Role": "MarketAnalyst"}
AI call Succeeded in 18420.9 ms, using 9310 input and 388 output tokens, with 3 live searches {"Provider": "grok", "Model": "...", "AgentId": "42", "CycleId": "9001", "Role": "SentimentAnalyst"}
```

- `Outcome` is the outcome of the call: `Succeeded`, `InvalidResult`, `TimedOut`, `AuthenticationFailed`, `ModelNotFound`, or `Failed`. See "Outcomes" in [ai-providers.md](ai-providers.md). A call that gave no result is a warning. It is not an error of the application, so the event has no exception.
- `DurationMs` is the latency of the call, as its caller records it.
- `InputTokens` and `OutputTokens` are there when the provider counted them, also for a call that gave no result: an answer that was cut short was paid for.
- `LiveSearches` is there for a call that asked for live search and was answered: how many searches the provider ran, which it charges for beside the tokens (FR-CYC-3). A call that asked for none has no such property. A model that was offered a search and ran none is a warning of its own, under `Ecnanib.AiProviderGateway.Adapters.ModelCall`: `The model was offered a live search, and its answer shows none`. See "Live search" in [ai-providers.md](ai-providers.md).
- A call that its caller cancelled is `Canceled`, and an error that an adapter did not expect is `Failed`, with the exception.
- The HTTP call that an adapter makes is logged as every other, under `Ecnanib.Platform.Adapters.Http`, and carries the same `Provider` and `Model`. So a call to a model is two events: what was sent and answered, and what it came to.

An answer that came with a success status and holds no result has a third event, which says why, under `Ecnanib.AiProviderGateway.Adapters.ModelCall`:

```
The answer of the model holds no structured result: Truncated, which the provider calls max_tokens {"Provider": "claude", "Model": "..."}
```

`Reason` is `Refused`, `Truncated`, `NotFinished`, `NotJson`, or `Empty`. `ProviderReason` is the provider's own word for how the answer ended, and is there only when that is one short word. Nothing else that a provider writes is in an event above the debug level: not the text of an answer, and not the message of an error, which can repeat a part of a key.

**Binance calls.** The Exchange Gateway logs a call to Binance once more only for what HTTP cannot see, as a warning, under `Ecnanib.ExchangeGateway.Adapters.Binance.BinanceCall`. A call that went well has the one event above.

```
Binance answered fapi/v3/balance on Futures Demo with its error -1021
The answer of Binance to api/v3/time on Spot Live is not what the call asks for: NotJson
```

- `BinanceCode` is the number that Binance gives an error. Binance answers most of its errors with the same status, and says which it is in the body. The text of the error is not logged.
- `Reason` is `NotJson` or `NotExpected`, for an answer that came with a success status and is not what the call asks for.
- `Path`, `Market`, and `Environment` say which call it was. See "The log" in [exchange.md](exchange.md).

The gateway also logs each reading of Binance's clocks, with how far the clock of the service is from them, and once when new entries pause for it and when they go on, under `Ecnanib.ExchangeGateway.Adapters.Binance.BinanceClock`. See "The log" in [exchange.md](exchange.md).

## Unhandled errors

An error that nothing handles is logged once, at the `Error` level, with its stack trace, the correlation properties of where it happened, and a `ReferenceId`. The user sees a plain message and that id, and nothing of the error (NFR-LOG-6).

| Where it happens | What the user gets |
|---|---|
| A request under `/api` | Status 500 and problem JSON with `referenceId`. |
| Any other request | Status 500 and the page `/Error`, which shows the reference. |
| Starting or running the host | Nothing to show. The error is logged as `Critical` and the process exits with code 1. A database that cannot be migrated is such an error: see [persistence.md](persistence.md). |
| A scheduled job | Nothing to show, and no reference. The scheduler logs the error once, as `Error`, with the job's name, and the application runs on. See [background-work.md](background-work.md). |

To find an error a user reports, search the log file for the reference. The event's `RequestId` leads to everything else the request logged.

In the Development environment the host shows its developer exception page instead, and the error is still logged.

The first interactive component needs one more piece. An error inside an interactive component does not pass through the request pipeline, so wrap interactive content in an error boundary that calls `UnhandledErrors.Report(exception)` and shows the reference it returns. No page is interactive yet.
