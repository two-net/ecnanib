# Exchange

Every call to Binance goes through the Exchange Gateway: one adapter behind a common interface (SRS 3.1, NFR-ARC-3). Binance is the only exchange. The gateway reaches its Spot market and its USDⓈ-margined perpetual Futures market, each on the real exchange and in Binance's test environment, with Binance's official public APIs, at the four addresses of the configuration file (FR-BIN-1, FR-BIN-2).

A call that needs a credential is signed with the user's own, for the environment that is asked. The gateway has no operation that withdraws, deposits, or transfers funds (FR-BIN-9). Every call is logged with its duration, its outcome, and the request weight that Binance reports (NFR-LOG-5).

The code is in `src/Ecnanib/ExchangeGateway`. The rules below are enforced by tests in `tests/Ecnanib.Tests/ExchangeGateway` and `tests/Ecnanib.Tests/Architecture`.

How a Binance credential is stored and checked is in [credentials.md](credentials.md). How a call is logged is in [logging.md](logging.md). Where the addresses are set is in [configuration.md](configuration.md). Who may use the gateway is in [architecture.md](architecture.md).

## The interface

```csharp
public interface IExchangeAdapter
{
    Task<ExchangeTime> GetServerTimeAsync(Market market, TradingMode mode, CancellationToken cancellationToken);

    Task<ExchangeKeyCheck> CheckKeyAsync(
        TradingMode mode, string apiKey, string secretKey, CancellationToken cancellationToken);

    Task<ExchangeBalances> GetBalancesAsync(
        Market market, TradingMode mode, string apiKey, string secretKey, CancellationToken cancellationToken);
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
| **A call is made once.** | The gateway does not call again after a failure. Whether a call is made again is its caller's to decide. |
| The check of a key answers with a `KeyVerdict`. | It is older than the outcomes below, and says the same: see "Validating a key" in [credentials.md](credentials.md). |

An operation ends with an error in two cases, neither of which is Binance's doing: the caller cancelled, or what it was given is no call, which is one without a key, or for a market or an environment that does not exist. Everything else is an outcome.

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
| **The list is in one place.** | `Adapters/Binance/BinanceEndpoint.cs` has one value for each call: the market whose address serves it, its path, and its query where it has one of its own. Nothing outside that class can make one. |
| **Every call passes one place.** | `Adapters/Binance/BinanceCall.cs` makes the exchange: the address, the signature, the time limit, and what the answer means. It takes a `BinanceEndpoint` and nothing else. `NoFundMovementTests` fails when another type of the gateway takes a client from the host's factory or sends with one. |
| No call takes a parameter of its caller yet. | The one query, `omitZeroBalances=true`, is a part of its call. Parameters come with the first call that needs one, and are then percent-encoded before they are signed, which Binance requires. |
| **The time limit is 15 seconds,** on the application's clock. | It covers the sending and the reading of the answer. A call that runs into it ends as `TimedOut`. |
| The weight is what Binance documents. | Nothing counts it yet: see "Not built yet". What Binance has counted is in the log. |

## The credential

A user's Binance credential is an API key and its secret key, one pair for each environment (FR-KEY-2). Both are given with each call that needs them.

| Rule | Detail |
|---|---|
| **A call is signed.** | The query is hashed with the secret key (HMAC-SHA256), and the hash is sent as its last parameter, `signature`. Before it come `recvWindow=5000`, which is Binance's own default for how long after its timestamp a call is taken, and `timestamp`, in milliseconds, by the application's clock. What is signed is the query exactly as it is sent. |
| **The secret key is never sent.** | It signs, and the bytes it was read into are cleared. The API key travels in the header `X-MBX-APIKEY`, and never in an address, which is logged. |
| **The environment says which address,** and the credential that was given for it signs. | A credential of the test environment reaches no address of the real exchange, and the other way around. |
| **The gateway keeps no key, and fetches none.** | It holds a key nowhere beyond the one request, and it may not use Credentials: the gateways are leaves of the architecture rules. What decrypts a stored key hands it over for the one call (FR-KEY-9). |
| A key is in no log event and no error. | The log of outbound calls leaves out the query and the headers. An error of the gateway does not repeat what it was given. `BinanceGatewayTests` reads the log at the debug level and finds neither key and no signature. |

## Outcomes

A call that gives no answer is not an error. Its result says how it ended, so that a caller, which may name no type of the network, can tell what to do next.

| Outcome | When | What it is for |
|---|---|---|
| `Succeeded` | Binance answered with what was asked for. | The time, or the balances. |
| `AuthenticationFailed` | Binance answered as it answers a wrong credential: 401, or one of its errors `-1002`, `-1022`, `-2008`, `-2014`, `-2015`. | The credential is checked again, with `IKeyValidation.RevalidateExchangeKeyAsync` (FR-KEY-3). The outcome does not say that the credential is wrong: the check decides that. |
| `TimedOut` | The call ended without an answer, and its caller did not cancel it. | The call may be made again. |
| `Failed` | Anything else: no connection, a limit on requests, a clock that is off, a block of the service's address, a fault of the exchange, or an answer that is not what Binance sends. | Counted where repeated failures are (NFR-REL-4). |

- **Only Binance's answer to a wrong credential is `AuthenticationFailed`.** A status that could also mean something else, such as 403, is `Failed`: a block of the service's address must not have every stored key checked, or marked.
- **`Failed` does not say which failure it was.** A limit on requests and a clock that is off are told apart when something acts on them: the rate limiter and the watch on the clock. See "Not built yet".
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

## Checking against Binance itself

The tests answer in Binance's place, with what its documentation says it sends. `LiveBinanceTests` asks Binance itself. It is skipped unless a credential is given for an environment, in the environment of the run, so the suite never reaches Binance by itself and no key is stored:

```
ECNANIB_LIVE_BINANCE_DEMO_KEY=... ECNANIB_LIVE_BINANCE_DEMO_SECRET=... \
  dotnet test --solution Ecnanib.slnx --filter-class "*LiveBinanceTests"
```

The variables are `ECNANIB_LIVE_BINANCE_<ENVIRONMENT>_KEY` and `ECNANIB_LIVE_BINANCE_<ENVIRONMENT>_SECRET`, with `DEMO` or `LIVE`. Give them for one environment or for both. For each that has both, these calls are made, at the addresses of the tracked configuration file. Every one of them reads, and costs request weight only:

| Call | What it must end as |
|---|---|
| The clock of each market. | `Succeeded`, with a time of now. |
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

## Not built yet

- What hands a stored credential to a call, and has it checked again when the call ends as `AuthenticationFailed` (FR-KEY-9, FR-KEY-3). It comes with what calls the gateway for an agent.
- The rate limiter (FR-BIN-4). Nothing counts the weight of a call, or holds a call back. It also tells a limit on requests apart from the other failures.
- The watch on the clock (FR-BIN-5). `GetServerTimeAsync` is what it reads. It also tells a clock that is off apart from the other failures.
- Market data: candles, prices, and the rules of a symbol (FR-BIN-3). It brings the first call that takes a parameter of its caller.
- The orders (FR-BIN-6), in `Ecnanib.ExchangeGateway.Trading`, and with them the first call that is not a `GET`.
- The feed of account events, and reconciliation (FR-BIN-7, FR-BIN-8).
- Breakers for repeated failures of Binance (NFR-REL-4).
