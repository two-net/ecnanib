# Exchange

Every call to Binance goes through the Exchange Gateway: one adapter behind a common interface (SRS 3.1, NFR-ARC-3). Binance is the only exchange. The gateway reaches its Spot market and its USDⓈ-margined perpetual Futures market, each on the real exchange and in Binance's test environment, with Binance's official public APIs, at the four addresses of the configuration file (FR-BIN-1, FR-BIN-2).

A call that needs a credential is signed with the user's own, for the environment that is asked. The gateway has no operation that withdraws, deposits, or transfers funds (FR-BIN-9). Every call passes one limiter, for all users, because Binance counts its limits on requests against the address of the service (FR-BIN-4). Every call is logged with its duration, its outcome, and the request weight that Binance reports (NFR-LOG-5).

The code is in `src/Ecnanib/ExchangeGateway`. The rules below are enforced by tests in `tests/Ecnanib.Tests/ExchangeGateway` and `tests/Ecnanib.Tests/Architecture`.

How a Binance credential is stored and checked is in [credentials.md](credentials.md). How a call is logged is in [logging.md](logging.md). Where the addresses are set is in [configuration.md](configuration.md). Who may use the gateway is in [architecture.md](architecture.md).

## The interface

```csharp
public interface IExchangeAdapter
{
    Task<ExchangeTime> GetServerTimeAsync(Market market, TradingMode mode, CancellationToken cancellationToken);

    Task<ExchangeKeyCheck> CheckKeyAsync(
        Guid userId, TradingMode mode, string apiKey, string secretKey, CancellationToken cancellationToken);

    Task<ExchangeBalances> GetBalancesAsync(
        Guid userId, Market market, TradingMode mode, string apiKey, string secretKey, CancellationToken cancellationToken);
}

public sealed record ExchangeTime(ExchangeCallOutcome Outcome, DateTimeOffset? ServerTime);

public sealed record ExchangeBalances(ExchangeCallOutcome Outcome, IReadOnlyList<ExchangeBalance> Balances);

public sealed record ExchangeBalance(string Asset, decimal Total, decimal Available);
```

| Rule | Detail |
|---|---|
| **A caller names a market and an environment, and nothing of Binance.** | `Market` is `Spot` or `Futures`, and `TradingMode` is `Demo` or `Live`. The adapter is private to the gateway, and so are the paths, the parameters, and the errors of Binance. What a call gives is a type of the gateway. |
| **Every operation reads.** | The clock of a market, what a credential may do, and what an account holds. See "No funds are moved". |
| **The credential comes with the call.** | See "The credential". |
| **A call with a credential says whose it is.** | `userId` is the user that the credential belongs to. It is for the limiter, which gives the users whose calls wait their turns, and it is not sent to Binance. An empty one is no call. See "The limiter". |
| **A call is made once.** | The gateway does not call again after a failure. Whether a call is made again is its caller's to decide. |
| The check of a key answers with a `KeyVerdict`. | It is older than the outcomes below, and says the same: see "Validating a key" in [credentials.md](credentials.md). |

An operation ends with an error in two cases, neither of which is Binance's doing: the caller cancelled, or what it was given is no call, which is one without a key or without a user, or for a market or an environment that does not exist. Everything else is an outcome.

## Markets and environments

| Market | Environment | Setting | In the tracked file |
|---|---|---|---|
| Spot | Live | `Binance:Spot:Live:BaseUrl` | `https://api.binance.com` |
| Spot | Demo | `Binance:Spot:Demo:BaseUrl` | `https://demo-api.binance.com` |
| Futures | Live | `Binance:Futures:Live:BaseUrl` | `https://fapi.binance.com` |
| Futures | Demo | `Binance:Futures:Demo:BaseUrl` | `https://demo-fapi.binance.com` |

- **The market and the environment of a call say where it goes,** and nothing else does. `BinanceSettings.BaseUrl` answers for the pair, and the gateway reaches no other address.
- **Demo is Binance's own test environment,** with no real funds: its demo mode for Spot, and its test network for Futures. Binance documents both as taking the calls of the real exchange: those of Spot as they are, and most of those of Futures. The gateway asks both as it asks the real exchange.
- **Futures is USDⓈ-margined perpetual contracts,** which is Binance's API under `/fapi`. That is assumption A1 of the specification. Its question Q3, whether Futures means only those, is still open.
- `TrackedFileTests` holds the tracked file to Binance's own four addresses.

## The calls

These are all the calls that the gateway makes. Each is a `GET`.

| What | Call | Signed | Weight |
|---|---|---|---|
| The clock of Spot | `/api/v3/time` | No | 1 |
| The clock of Futures | `/fapi/v1/time` | No | 1 |
| What a key may do, on the real exchange | `/sapi/v1/account/apiRestrictions`, at the address of Spot | Yes | 1 |
| The account of Spot: what it holds, and whether it may trade | `/api/v3/account?omitZeroBalances=true` | Yes | 20 |
| Whether the account of Futures may trade, in the test environment | `/fapi/v1/accountConfig` | Yes | 5 |
| What the wallet of Futures holds | `/fapi/v3/balance` | Yes | 5 |

| Rule | Detail |
|---|---|
| **The list is in one place.** | `Adapters/Binance/BinanceEndpoint.cs` has one value for each call: the market whose address serves it, its path, its query where it has one of its own, its weight, and the limit that Binance counts it against. Nothing outside that class can make one. |
| **Every call passes one place.** | `Adapters/Binance/BinanceCall.cs` makes the exchange: the limiter, the address, the signature, the time limit, and what the answer means. It takes a `BinanceEndpoint` and nothing else. `NoFundMovementTests` fails when another type of the gateway takes a client from the host's factory or sends with one. |
| No call takes a parameter of its caller yet. | The one query, `omitZeroBalances=true`, is a part of its call. Parameters come with the first call that needs one, and are then percent-encoded before they are signed, which Binance requires. |
| **The time limit is 15 seconds,** on the application's clock. | It covers the sending and the reading of the answer, and not the wait for a turn at the limiter before them. A call that runs into it ends as `TimedOut`. |
| The weight is what Binance documents. | The limiter counts it: see "The limiter". What Binance has counted is in the log, and the limiter goes by it when it is more. |

## The credential

A user's Binance credential is an API key and its secret key, one pair for each environment (FR-KEY-2). Both are given with each call that needs them.

| Rule | Detail |
|---|---|
| **A call is signed.** | The query is hashed with the secret key (HMAC-SHA256), and the hash is sent as its last parameter, `signature`. Before it come `recvWindow=5000`, which is Binance's own default for how long after its timestamp a call is taken, and `timestamp`, in milliseconds, by the application's clock. What is signed is the query exactly as it is sent. |
| **The secret key is never sent.** | It signs, and the bytes it was read into are cleared. The API key travels in the header `X-MBX-APIKEY`, and never in an address, which is logged. |
| **The environment says which address,** and the credential that was given for it signs. | A credential of the test environment reaches no address of the real exchange, and the other way around. |
| **The gateway keeps no key, and fetches none.** | It holds a key nowhere beyond the one request, and it may not use Credentials: the gateways are leaves of the architecture rules. What decrypts a stored key hands it over for the one call (FR-KEY-9). |
| A key is in no log event and no error. | The log of outbound calls leaves out the query and the headers. An error of the gateway does not repeat what it was given. `BinanceGatewayTests` reads the log at the debug level and finds neither key and no signature. |

## The limiter

Binance counts its limits on requests against the address that the calls come from, and not against a key. So everyone who uses the service spends one budget (risk R2 of the specification). One limiter governs every call to Binance, for all users: `Adapters/Binance/BinanceLimiter.cs`, which `BinanceCall` asks before it sends anything (FR-BIN-4). `RequestLimitTests` holds it to the rules below, on a clock that the tests move.

| Limit | Its calls | Binance allows in a minute | The service allows itself, at 70 percent |
|---|---|---|---|
| Spot | Those under `/api` | 6,000 | 4,200 |
| Futures | Those under `/fapi` | 2,400 | 1,680 |
| Key permissions | `/sapi/v1/account/apiRestrictions` | 12,000 | 8,400 |

- **A limit is a request weight in a minute.** Each call costs its weight of the limit that it is counted against: see "The calls".
- **Each limit is counted on its own,** and in each environment on its own. That makes five budgets: Spot and Futures in both environments, and the permissions of a key on the real exchange, which is the only one that has the call. Binance's wallet API counts each of its calls apart from the others.
- **`Binance:LimitSharePercent` is the part of each limit that the service allows itself.** The rest is left for what the limiter cannot see.
- The numbers are in `BinanceLimit`, beside the weights, and are what Binance documents. Its documentation gives them as what `exchangeInfo` answers, and says nothing of its test environment, which is taken to count as the real exchange does.

| Rule | Detail |
|---|---|
| **Any sixty seconds carry at most the part.** | A fifth of what a minute allows may go at once, and the rest flows in evenly: at 70 percent, 336 of the Futures limit at once and 22.4 more in each second. So there is soon room again after a burst, and no minute is spent in its first second. Binance does not document whether its minute is one of the clock or the last sixty seconds, and this holds for both. |
| **A call that finds no room waits for its turn,** for 10 seconds at most. | Then it ends as `RateLimited`, and was not sent. It ends at once when the limiter already knows that its turn cannot come in time. A caller that cancels meanwhile gets the error of a cancelled call. |
| **A call is signed when it is sent.** | Not when it began to wait: Binance takes a signed call for five seconds after the time that it carries. The time limit of 15 seconds starts there as well. |
| **Calls that wait take turns, user by user.** | One call of each user after the other, so the burst of one user leaves the call of another one call behind, and not a burst behind. A call without a credential, such as a read of the clock, is the service's own, and the service takes turns as one more user. The order is strict: a light call does not pass a heavy one whose turn it is. |
| **When Binance signals that it has had too many, nothing is sent.** | The signal is the status 429, or 418 when Binance has blocked the address for ignoring it. The limit of the call is held for as long as Binance says, in `Retry-After`. Where it names no time, that is 1 minute, or 2 after a block, and twice as long each time the signal comes again with no other answer in between, up to 1 hour. After it the flow starts from nothing. |
| **No call is made again in a loop.** | The gateway makes a call once, as before. While a limit is held, a caller that asks again gets `RateLimited` and Binance gets nothing, however often it asks. |
| **What Binance has counted is what counts.** | Binance says it with every answer: see "The log". When it is more than the limiter let through, the limit has only what Binance has left of the part, until Binance counts less again. That happens when something else calls from the same address, or when a call weighs more than its documentation says. |
| It counts in memory, for one process. | Version 1 runs as one instance. A second one at the same address would show in what Binance counts. |

Only weight is limited so far. Binance also limits how many orders an account places, for each account, which comes with the orders.

### What is used

```csharp
public interface IExchangeLimits
{
    IReadOnlyList<ExchangeLimitUsage> Usage();
}

public sealed record ExchangeLimitUsage(
    string Name, TradingMode Environment, int Limit, int Allowed, int Used,
    int? Reported, DateTimeOffset? ReportedAt, int Waiting, DateTimeOffset? HeldUntil);
```

The limiter says where each limit stands, in the order of the table above, each in the real exchange and then in the test environment. It is what the operator status screen shows of Binance (FR-OPS-2), which is not built.

| | |
|---|---|
| `Limit`, `Allowed` | What Binance allows in a minute, and the part of it that the service allows itself. |
| `Used` | The weight of the calls that the limiter let through in the last sixty seconds. |
| `Reported`, `ReportedAt` | What Binance itself had counted when it last answered, and when that was. It counts by its own minute, so it is not `Used` to the digit. |
| `Waiting` | The calls that wait for their turn. |
| `HeldUntil` | Until when nothing is sent, after a signal of Binance. |

## Outcomes

A call that gives no answer is not an error. Its result says how it ended, so that a caller, which may name no type of the network, can tell what to do next.

| Outcome | When | What it is for |
|---|---|---|
| `Succeeded` | Binance answered with what was asked for. | The time, or the balances. |
| `AuthenticationFailed` | Binance answered as it answers a wrong credential: 401, or one of its errors `-1002`, `-1022`, `-2008`, `-2014`, `-2015`. | The credential is checked again, with `IKeyValidation.RevalidateExchangeKeyAsync` (FR-KEY-3). The outcome does not say that the credential is wrong: the check decides that. |
| `TimedOut` | The call ended without an answer, and its caller did not cancel it. | The call may be made again. |
| `RateLimited` | The limit on requests: the limiter held the call back for longer than a call waits, and it was not sent, or Binance answered with its signal of too many, 429 or 418. | The call is made again later, and not at once: until the limiter has room again, the same call ends the same. It is no failure of Binance, and says nothing about a credential. |
| `Failed` | Anything else: no connection, a clock that is off, a block of the service's address by the firewall in front of Binance, a fault of the exchange, or an answer that is not what Binance sends. | Counted where repeated failures are (NFR-REL-4). |

- **Only Binance's answer to a wrong credential is `AuthenticationFailed`.** A status that could also mean something else, such as 403, is `Failed`: a block of the service's address must not have every stored key checked, or marked.
- **`Failed` does not say which failure it was.** A clock that is off is told apart when something acts on it: the watch on the clock. See "Not built yet".
- **A result holds what was read when the call succeeded, and never otherwise.** A `ServerTime` is there with `Succeeded` alone, and so are balances.
- **A success that is not what was asked for is `Failed`:** the page of something in between, an answer of another kind, a clock without a time, an amount that is not a number.

The check of a key says the same with a `KeyVerdict`: `Succeeded` is `Accepted`, `AuthenticationFailed` is `Refused`, and anything else is `Unreachable`.

## Balances

`GetBalancesAsync` reads what the account of a credential holds on one market.

| | Spot | Futures |
|---|---|---|
| Read from | `balances[]` of the account | The list of the wallet |
| `Asset` | `asset` | `asset` |
| `Total` | `free` and `locked` together | `balance` |
| `Available` | `free` | `availableBalance` |

- **An asset that is not among the balances is one the account holds none of.** Spot is asked without them, and a row of Futures whose amounts are both zero is left out.
- **An amount is exact.** Binance writes amounts as text, so that no digit is lost, and they are read as decimals. An amount of the Futures wallet can be below zero.
- **A list is whole or it is none.** A row without an asset, an asset that is there twice, and an amount that does not read make the answer `Failed`, with no balance. A part of a list would make the rest look empty.
- It reads the whole account. Which part of it an agent may use is for what sizes a position (FR-BIN-S3, FR-BIN-F6).

## No funds are moved

Ecnanib never withdraws, deposits, or transfers funds, between the Spot and the Futures wallet of a user included (FR-BIN-9). A key that could withdraw is not even held: see "Binance permissions" in [credentials.md](credentials.md).

| Rule | Detail |
|---|---|
| **The gateway has no such operation.** | `IExchangeAdapter` has the three above, and each reads. |
| **It can send Binance nothing but the calls of the list.** | See "The calls". `NoFundMovementTests` asks the gateway for everything it does, in each market and each environment, and holds what was sent to that list. It fails for an operation that it does not ask, so an operation that is added is held to the list too. |
| **Binance keeps the calls that move funds apart.** | Withdrawals, the addresses of deposits, and transfers between wallets are in its wallet API, under `/sapi`. The gateway makes one call there, which reads what a key may do. |
| The orders will not move funds either. | They come in `Ecnanib.ExchangeGateway.Trading`, which only the Decision Engine may use: see "Placing orders" in [architecture.md](architecture.md). The list of calls is where they are added, and the test is where they are held to it. |

## The log

Every call is in the log of outbound calls, as every call of a client of the host's factory is: see "Outbound calls" in [logging.md](logging.md).

```
Outbound call GET fapi.binance.com/fapi/v3/balance Succeeded with status 200 in 61.8 ms {"Usage": {"x-mbx-used-weight-1m": "5"}}
```

- **`Usage` holds what Binance has counted against the service.** Its Spot and its Futures API report it in headers that begin with `X-MBX-USED-WEIGHT` and `X-MBX-ORDER-COUNT`, one for each interval. Its wallet API reports it in headers that begin with `X-SAPI-USED`. The gateway registers all three with `OutboundCallLogOptions`.
- The address is logged without its query, which holds the signature, and no header is logged.

The gateway adds an event of its own for what that log cannot see, as a warning, under `Ecnanib.ExchangeGateway.Adapters.Binance.BinanceCall`:

```
Binance answered fapi/v3/balance on Futures Demo with its error -1021
The answer of Binance to api/v3/time on Spot Live is not what the call asks for: NotJson
```

- **Which error it was.** Binance answers most of its errors with the same status, and says which in the body, which the log of outbound calls writes at the debug level only. `BinanceCode` is the number that Binance gives the error. Its text is not logged.
- **A success that gave nothing.** The log of outbound calls has such a call as `Succeeded`, by its status. `Reason` is `NotJson`, or `NotExpected` for an answer of another kind than the call asks for.
- A call that went well has no event of the gateway.

What the limiter does is said as warnings too. A call that it did not let through is in no log of outbound calls, so the gateway says it, under the same area. The limiter says the rest, under `Ecnanib.ExchangeGateway.Adapters.Binance.BinanceLimiter`:

```
The call fapi/v3/balance on Futures Live was not sent to Binance: the limit on requests has no room for it in time
Binance signalled that it has had too many requests on Futures Live: Throttled. No call is sent there for 30 seconds
Binance has counted 4350 of request weight on Spot Live, which is more than the limiter let through. The limiter goes by what Binance counts
```

- **`Signal`** is `Throttled` for the status 429 and `Banned` for 418, and `HoldSeconds` is for how long nothing is sent. `Limit` is the name of the limit, as in "The limiter".
- **That Binance has counted more is said once,** when it begins, and not with every answer while it lasts.

## Checking against Binance itself

The tests answer in Binance's place, with what its documentation says it sends. `LiveBinanceTests` asks Binance itself. It is skipped unless a credential is given for an environment, in the environment of the run, so the suite never reaches Binance by itself and no key is stored:

```
ECNANIB_LIVE_BINANCE_DEMO_KEY=... ECNANIB_LIVE_BINANCE_DEMO_SECRET=... \
  dotnet test --solution Ecnanib.slnx --filter-class "*LiveBinanceTests"
```

The variables are `ECNANIB_LIVE_BINANCE_<ENVIRONMENT>_KEY` and `ECNANIB_LIVE_BINANCE_<ENVIRONMENT>_SECRET`, with `DEMO` or `LIVE`. Give them for one environment or for both. For each that has both, these calls are made, at the addresses of the tracked configuration file. Every one of them reads, and costs request weight only:

| Call | What it must end as |
|---|---|
| The clock of each market. | `Succeeded`, with a time of now, and with what Binance has counted against the address, which the limiter reports then. |
| The balances of each market, with the credential that was given. | `Succeeded` |
| The check of the credential. | `Accepted`, and on the real exchange with whether it can withdraw and whether it is restricted to addresses. |
| The balances of each market, with a credential that Binance does not know. | `AuthenticationFailed` |
| The check of such a credential. | `Refused` |

The tests read the variables. The application reads none (NFR-CFG-2).

A key of the real exchange that works only from listed addresses is refused from any other, as a wrong one is. Allow the address of the machine at Binance for the run, or the check of the credential fails.

Run it when a call is added or changed, and when Binance changes its API. These things rest on documentation, or on less, until a run has shown them:

- That one credential of the test environment works at the addresses of both its markets. Binance's documentation of each sends the user to the same place for a key, and does not say so.
- That the test environment of Futures serves `/fapi/v1/accountConfig` and `/fapi/v3/balance`. Its documentation says that most calls work there.
- What Binance answers a wrong credential with. Its documentation lists `-1002`, `-1022`, `-2014`, and `-2015`, and describes no status 401. The error `-2008`, which its wallet API answers an unknown key with, is in no list of its errors.
- That the wallet API reports its weight as `X-SAPI-USED-IP-WEIGHT-1M` with an answer that succeeded.
- That Spot and Futures report theirs as `X-MBX-USED-WEIGHT-1M`, in both environments. Binance's documentation gives the name as a pattern, with the interval at its end.
- What Binance allows an address in a minute, in its test environment above all, and that it sends `Retry-After` with its signal on Futures. Neither is asked by a run: the limits are what `exchangeInfo` answers, which the gateway does not call yet.

## Not built yet

- What hands a stored credential to a call, and has it checked again when the call ends as `AuthenticationFailed` (FR-KEY-9, FR-KEY-3). It comes with what calls the gateway for an agent.
- The operator status screen, which shows what `IExchangeLimits` says (FR-OPS-2).
- The limit on the orders of an account, which Binance counts for each account and not for the address. It comes with the orders.
- The watch on the clock (FR-BIN-5). `GetServerTimeAsync` is what it reads. It also tells a clock that is off apart from the other failures.
- Market data: candles, prices, and the rules of a symbol (FR-BIN-3). It brings the first call that takes a parameter of its caller, and `exchangeInfo`, from which the limits of "The limiter" can then be read.
- The orders (FR-BIN-6), in `Ecnanib.ExchangeGateway.Trading`, and with them the first call that is not a `GET`.
- The feed of account events, and reconciliation (FR-BIN-7, FR-BIN-8).
- Breakers for repeated failures of Binance (NFR-REL-4).
