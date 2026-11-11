# Logging

Ecnanib writes structured log events: each one has a message template with named properties, a level, and the properties of the context it was written in. Levels and outputs come from the `Logging` section of the configuration file and from nowhere else (NFR-LOG-1 to -6).

The library is Serilog, behind the host's `ILogger<T>`. Only `Platform` uses Serilog directly (`src/Ecnanib/Platform/Logging`). The rules below are enforced by tests in `tests/Ecnanib.Tests/Logging` and `tests/Ecnanib.Tests/Architecture`.

Logs are not the audit trail. The audit trail is stored in the database and is a separate feature.

## Configuration

```json
"Logging": {
  "MinimumLevel": "Information",
  "Overrides": {
    "Microsoft.AspNetCore": "Warning"
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
| `RequestId` | `UseRequestCorrelation()`, for every HTTP request. |
| `UserId` | `UseRequestCorrelation()`, when the request is signed in. |
| `Provider`, `Model` | The AI Provider Gateway, around each call to a model. |
| `AgentId`, `CycleId`, `Role` | The component that runs a cycle and calls the team. |
| `ExchangeOrderId` | The component that places or tracks an order. |

Open a scope with the properties you know. Every event written until the scope is disposed carries them, whichever class writes it, across `await`:

```csharp
using (logger.BeginCorrelation(new Correlation(AgentId: agent.Id, CycleId: cycle.Id)))
{
    await RunTheTeamAsync(cycle, cancellationToken);
}
```

Scopes nest. Work that runs outside a request, such as a scheduled cycle, opens its own scope and logs its own failures inside it, so the error carries the agent and the cycle.

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

## Outbound calls

Every call to an AI provider or to Binance is logged with its duration, its outcome, and its usage (NFR-LOG-5).

**HTTP.** Each client from the host's factory logs every attempt, so an adapter gets this without writing any code. An adapter cannot create a client itself: the architecture tests reject `new HttpClient`.

```
Outbound call POST fapi.binance.com/fapi/v1/order Succeeded with status 200 in 84.2 ms {"Usage": {"X-MBX-USED-WEIGHT-1M": "12"}}
```

- The address is logged without its query, and headers are not logged.
- `Outcome` is `Succeeded`, `Failed` (a status of 400 or above, or no answer), or `Canceled`. A failed call is a warning.
- `Usage` holds the response headers that report usage. The component that knows the system names them: the Exchange Gateway registers Binance's used-weight and order-count headers through `OutboundCallLogOptions`.
- Request and response bodies are logged only at the debug level, which is off unless the file turns it on for the area `Ecnanib.Platform.Adapters.Http`. Bodies pass the masking like everything else. While it is on, each body is held in memory in full, so use it to investigate and then turn it off.

**AI calls.** The AI Provider Gateway also logs each call to a model, with what HTTP cannot see: the token usage.

```
AI call Succeeded in 2310.4 ms, using 1840 input and 212 output tokens {"Provider": "claude", "Model": "...", "AgentId": "42", "CycleId": "9001", "Role": "RiskManager"}
```

## Unhandled errors

An error that nothing handles is logged once, at the `Error` level, with its stack trace, the correlation properties of where it happened, and a `ReferenceId`. The user sees a plain message and that id, and nothing of the error (NFR-LOG-6).

| Where it happens | What the user gets |
|---|---|
| A request under `/api` | Status 500 and problem JSON with `referenceId`. |
| Any other request | Status 500 and the page `/Error`, which shows the reference. |
| Starting or running the host | Nothing to show. The error is logged as `Critical` and the process exits with code 1. |

To find an error a user reports, search the log file for the reference. The event's `RequestId` leads to everything else the request logged.

In the Development environment the host shows its developer exception page instead, and the error is still logged.

The first interactive component needs one more piece. An error inside an interactive component does not pass through the request pipeline, so wrap interactive content in an error boundary that calls `UnhandledErrors.Report(exception)` and shows the reference it returns. No page is interactive yet.
