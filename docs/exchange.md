# Exchange

Every call to Binance goes through the Exchange Gateway: one adapter behind a common interface (SRS 3.1, NFR-ARC-3). Binance is the only exchange. The gateway reaches its Spot market and its USDⓈ-margined perpetual Futures market, each on the real exchange and in Binance's test environment, with Binance's official public APIs, at the four addresses of the configuration file (FR-BIN-1, FR-BIN-2).

A call that needs a credential is signed with the user's own, for the environment that is asked. The gateway places orders, each with the identifier that its caller gave it, and says of each call whether there is an order, so that none is sent twice (FR-BIN-6). On Futures it reads how an account stands, and sets the margin type and the leverage of a symbol, which are no settings of the whole account (FR-BIN-F2). It reads the position that an account holds there with its margin, and the funding that the account received and paid, and it adds no margin to a position (FR-BIN-F4, FR-BIN-F5). It has no operation that withdraws, deposits, or transfers funds (FR-BIN-9). Every call passes one limiter, for all users, because Binance counts its limits on requests against the address of the service (FR-BIN-4). The clock of the service is held against Binance's, and new entries pause while it is off (FR-BIN-5). Market data is read once and shared by all who need it, and is held in memory for a short time and nowhere else (FR-BIN-3, NFR-DAT-6). Where Binance has an account event feed, the gateway follows the account of a credential on it, and says when something changed there (FR-BIN-7). Every call is logged with its duration, its outcome, and the request weight that Binance reports (NFR-LOG-5).

The code is in `src/Ecnanib/ExchangeGateway`. The rules below are enforced by tests in `tests/Ecnanib.Tests/ExchangeGateway` and `tests/Ecnanib.Tests/Architecture`.

How a Binance credential is stored and checked is in [credentials.md](credentials.md). What places the orders of an agent, and when, is in [orders.md](orders.md). How a call is logged is in [logging.md](logging.md). Where the addresses are set is in [configuration.md](configuration.md). Who may use the gateway is in [architecture.md](architecture.md).

## The interface

```csharp
public interface IExchangeAdapter
{
    Task<ExchangeTime> GetServerTimeAsync(Market market, TradingMode mode, CancellationToken cancellationToken);

    Task<ExchangeKeyCheck> CheckKeyAsync(
        Guid userId, TradingMode mode, string apiKey, string secretKey, CancellationToken cancellationToken);

    Task<ExchangeBalances> GetBalancesAsync(
        Guid userId, Market market, TradingMode mode, string apiKey, string secretKey, CancellationToken cancellationToken);

    Task<ExchangeFuturesAccount> GetFuturesAccountAsync(
        Guid userId, TradingMode mode, string apiKey, string secretKey, string symbol, CancellationToken cancellationToken);

    Task<ExchangeFuturesPosition> GetFuturesPositionAsync(
        Guid userId, TradingMode mode, string apiKey, string secretKey, string symbol, CancellationToken cancellationToken);

    Task<ExchangeFundings> GetFundingAsync(
        Guid userId, TradingMode mode, string apiKey, string secretKey, string symbol,
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

public sealed record ExchangeTime(ExchangeCallOutcome Outcome, DateTimeOffset? ServerTime);

public sealed record ExchangeBalances(ExchangeCallOutcome Outcome, IReadOnlyList<ExchangeBalance> Balances);

public sealed record ExchangeBalance(string Asset, decimal Total, decimal Available);

public sealed record ExchangeFuturesAccount(ExchangeCallOutcome Outcome, bool HedgeMode, bool MultiAssetsMargin, bool HoldsPosition);

public sealed record ExchangeFuturesPosition(
    ExchangeCallOutcome Outcome, decimal Quantity, decimal? EntryPrice, decimal? MarkPrice, decimal? LiquidationPrice);

public sealed record ExchangeFundings(ExchangeCallOutcome Outcome, IReadOnlyList<ExchangeFunding> Fundings, bool More);

public sealed record ExchangeFunding(string TransactionId, decimal Amount, string Asset, DateTimeOffset At);
```

| Rule | Detail |
|---|---|
| **A caller names a market and an environment, and nothing of Binance.** | `Market` is `Spot` or `Futures`, and `TradingMode` is `Demo` or `Live`. The adapter is private to the gateway, and so are the paths, the parameters, and the errors of Binance. What a call gives is a type of the gateway. |
| **Every operation of this interface reads.** | The clock of a market, what a credential may do, what an account holds, how an account stands on Futures, the position that it holds in a symbol there with its margin, and the funding that it received and paid. The orders have an interface of their own, and so has what sets the margin of a symbol: see "The orders" and "No funds are moved". |
| **What an account holds is asked before an entry.** | `AgentOrders` of the Decision Engine asks `GetBalancesAsync` for the asset that the symbol of an entry is priced in, and skips an entry that the account does not cover: its value on Spot (FR-BIN-S3), and its margin in the wallet of Futures (FR-BIN-F6). See "The quote balance" and "The margin of Futures" in [orders.md](orders.md). |
| **How an account stands on Futures is asked before an entry there, and before an agent runs.** | `GetFuturesAccountAsync`: see "The account of Futures". |
| **The position of an account on Futures and its funding are asked while an agent holds a position there.** | `PositionWatch` of the Decision Engine asks `GetFuturesPositionAsync` for the margin of every open position, every few minutes, and `GetFundingAsync` for what a position received and paid, after each funding time. See "The position of an account, and its margin" and "The funding of an account", and "Watching the positions of Futures" in [orders.md](orders.md). |
| **The credential comes with the call.** | See "The credential". |
| **A call with a credential says whose it is.** | `userId` is the user that the credential belongs to. It is for the limiter, which gives the users whose calls wait their turns, and it is not sent to Binance. An empty one is no call. See "The limiter". |
| **A call is made once.** | The gateway does not call again after a failure. Whether a call is made again is its caller's to decide. |
| The check of a key answers with a `KeyVerdict`. | It is older than the outcomes below, and says the same: see "Validating a key" in [credentials.md](credentials.md). |

An operation ends with an error in two cases, neither of which is Binance's doing: the caller cancelled, or what it was given is no call, which is one without a key or without a user, or for a market or an environment that does not exist. Everything else is an outcome.

Candles, prices, and the rules of the symbols are not read through this interface, where a call is made once for whoever makes it. They have an interface of their own, `IExchangeMarketData`, behind which a read is shared: see "Market data". The account event feed has one as well, `IExchangeAccountFeed`, behind which a connection stays open: see "The account event feed".

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

These are all the calls that the gateway makes. Each is a `GET`, but for the calls that place an order or take one off the exchange, which are in the second table, the two that set the margin of a symbol, which are in the third, and the two of the account event feed, which are in the fourth.

| What | Call | Signed | Weight |
|---|---|---|---|
| The clock of Spot | `/api/v3/time` | No | 1 |
| The clock of Futures | `/fapi/v1/time` | No | 1 |
| What a key may do, on the real exchange | `/sapi/v1/account/apiRestrictions`, at the address of Spot | Yes | 1 |
| The account of Spot: what it holds, and whether it may trade | `/api/v3/account?omitZeroBalances=true` | Yes | 20 |
| The settings of the account of Futures: whether it may trade, in the test environment, and the two modes of the whole account | `/fapi/v1/accountConfig` | Yes | 5 |
| What the wallet of Futures holds | `/fapi/v3/balance` | Yes | 5 |
| The positions of the account of Futures in a symbol: whether it holds one, and how the margin of one stands | `/fapi/v3/positionRisk?symbol=…` | Yes | 5 |
| The funding that the account of Futures received and paid in a symbol, between two moments | `/fapi/v1/income?incomeType=FUNDING_FEE&symbol=…&startTime=…&endTime=…&limit=1000` | Yes | 30 |
| The candles of a symbol on Spot | `/api/v3/klines?symbol=…&interval=…&limit=499` | No | 2 |
| The candles of a symbol on Futures | `/fapi/v1/klines?symbol=…&interval=…&limit=499` | No | 2 |
| The price of a symbol on Spot | `/api/v3/ticker/price?symbol=…` | No | 2 |
| The price of a symbol on Futures | `/fapi/v2/ticker/price?symbol=…` | No | 1 |
| The symbols of Spot, with their rules | `/api/v3/exchangeInfo?showPermissionSets=false` | No | 20 |
| The symbols of Futures, with their rules | `/fapi/v1/exchangeInfo` | No | 1 |
| The mark price of a symbol on Futures, with its funding | `/fapi/v1/premiumIndex?symbol=…` | No | 1 |
| The open interest of a symbol on Futures | `/fapi/v1/openInterest?symbol=…` | No | 1 |

The calls of the orders, each signed:

| What | Call | Weight |
|---|---|---|
| Places an order on Spot: at the market, at a price, or a stop | `POST /api/v3/order` | 1 |
| What became of an order of Spot | `GET /api/v3/order` | 4 |
| Takes an order of Spot off the exchange | `DELETE /api/v3/order` | 1 |
| The executions of an order of Spot | `GET /api/v3/myTrades?symbol=…&orderId=…` | 5 |
| Places the pair of a stop-loss and a take-profit on Spot | `POST /api/v3/orderList/oco` | 1 |
| Takes a pair of Spot off the exchange | `DELETE /api/v3/orderList` | 1 |
| Places an order on Futures: at the market, or at a price | `POST /fapi/v1/order` | 0 |
| What became of an order of Futures | `GET /fapi/v1/order` | 1 |
| Takes an order of Futures off the exchange | `DELETE /fapi/v1/order` | 1 |
| Places a stop or a take-profit on Futures | `POST /fapi/v1/algoOrder?algoType=CONDITIONAL` | 0 |
| What became of a stop or a take-profit of Futures | `GET /fapi/v1/algoOrder` | 1 |
| Takes a stop or a take-profit of Futures off the exchange | `DELETE /fapi/v1/algoOrder` | 1 |
| The executions of an order of Futures | `GET /fapi/v1/userTrades?symbol=…&orderId=…` | 5 |

The calls that set the margin of a symbol of Futures in the account of a credential, each signed:

| What | Call | Weight |
|---|---|---|
| Gives the symbol isolated margin | `POST /fapi/v1/marginType?marginType=ISOLATED` | 1 |
| Sets the leverage of the symbol | `POST /fapi/v1/leverage` | 1 |

The calls of the account event feed of Futures, each with the API key in its header, and with no signature and no parameter:

| What | Call | Weight |
|---|---|---|
| Begins the stream of the events of the account, and names it | `POST /fapi/v1/listenKey` | 1 |
| Renews that stream | `PUT /fapi/v1/listenKey` | 1 |

On Spot the feed is no call: it is a connection to Binance's WebSocket API, which Binance counts as 2 of the limit of Spot, and one request on it, which it counts as 2 more. The limiter counts both: see "The account event feed".

| Rule | Detail |
|---|---|
| **The list is in one place.** | `Adapters/Binance/BinanceEndpoint.cs` has one value for each call: the market whose address serves it, its method, its path, its query, its weight, and the limit that Binance counts it against. Nothing outside that class can make one. |
| **Every parameter is in the query,** also of a call that is no `GET`. | Binance takes them there. No call has a body, and the log of outbound calls leaves the query out. |
| **Every call passes one place.** | `Adapters/Binance/BinanceCall.cs` makes the exchange: the limiter, the address, the signature, the time limit, and what the answer means. It takes a `BinanceEndpoint` and nothing else. `NoFundMovementTests` fails when another type of the gateway takes a client from the host's factory or sends with one. The connection of an account event feed is no call, and one type opens it: see "The account event feed". |
| **A parameter of a caller is percent-encoded,** which Binance requires. | The calls of the market data take a symbol, and the candles the length of a candle. `BinanceEndpoint` makes such a call from the one of the list that it is, so what a caller gives is one value of one parameter, whatever it is. A query that a call always has, such as `omitZeroBalances=true`, is a part of its call. |
| The candles of Futures cost 2 for what is asked. | Binance counts them by the rows that are asked for: 1 below 100, 2 below 500, 5 up to 1,000. A read asks for 499, which is the most that cost 2: see "Market data". |
| **The time limit is 15 seconds,** on the application's clock. | It covers the sending and the reading of the answer, and not the wait for a turn at the limiter before them. A call that runs into it ends as `TimedOut`. |
| The weight is what Binance documents. | The limiter counts it: see "The limiter". What Binance has counted is in the log, and the limiter goes by it when it is more. |

## The credential

A user's Binance credential is an API key and its secret key, one pair for each environment (FR-KEY-2). Both are given with each call that needs them.

| Rule | Detail |
|---|---|
| **A call is signed.** | The query is hashed with the secret key (HMAC-SHA256), and the hash is sent as its last parameter, `signature`. Before it come `recvWindow=5000`, which is Binance's own default for how long after its timestamp a call is taken, and `timestamp`, in milliseconds, by the application's clock, which is watched for that: see "The clock". What is signed is the query exactly as it is sent. |
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
- **`Binance:LimitSharePercent` is the part of each limit that the service allows itself.** The rest is left for what the limiter cannot see. It is 2 at the least: the part must hold the heaviest call of its limit, or that call would never pass, and the read of funding on Futures weighs 30, where a hundredth of that limit is 24.
- The numbers are in `BinanceLimit`, beside the weights, and are what Binance documents. Its documentation gives them as what `exchangeInfo` answers, and says nothing of its test environment, which is taken to count as the real exchange does.

| Rule | Detail |
|---|---|
| **Any sixty seconds carry at most the part.** | A fifth of what a minute allows may go at once, or the heaviest call of the limit where that is more, and the rest flows in evenly: at 70 percent, 336 of the Futures limit at once and 22.4 more in each second. So there is soon room again after a burst, and no minute is spent in its first second. Binance does not document whether its minute is one of the clock or the last sixty seconds, and this holds for both. |
| **A call that finds no room waits for its turn,** for 10 seconds at most. | Then it ends as `RateLimited`, and was not sent. It ends at once when the limiter already knows that its turn cannot come in time. A caller that cancels meanwhile gets the error of a cancelled call. |
| **A call is signed when it is sent.** | Not when it began to wait: Binance takes a signed call for five seconds after the time that it carries. The time limit of 15 seconds starts there as well. |
| **Calls that wait take turns, user by user.** | One call of each user after the other, so the burst of one user leaves the call of another one call behind, and not a burst behind. A call without a credential, such as a read of the clock or of market data, is the service's own, and the service takes turns as one more user. The order is strict: a light call does not pass a heavy one whose turn it is. |
| **When Binance signals that it has had too many, nothing is sent.** | The signal is the status 429, or 418 when Binance has blocked the address for ignoring it. The limit of the call is held for as long as Binance says, in `Retry-After`. Where it names no time, that is 1 minute, or 2 after a block, and twice as long each time the signal comes again with no other answer in between, up to 1 hour. After it the flow starts from nothing. |
| **No call is made again in a loop.** | The gateway makes a call once, as before. While a limit is held, a caller that asks again gets `RateLimited` and Binance gets nothing, however often it asks. |
| **What Binance has counted is what counts.** | Binance says it with every answer: see "The log". When it is more than the limiter let through, the limit has only what Binance has left of the part, until Binance counts less again. That happens when something else calls from the same address, or when a call weighs more than its documentation says. |
| It counts in memory, for one process. | Version 1 runs as one instance. A second one at the same address would show in what Binance counts. |

Only weight is limited. Binance also limits how many orders an account places, for each account, which the limiter does not count: see "Not built yet". Placing an order on Futures costs no request weight. An order that the limiter holds back was not sent, and ends as `NotSent`: see "The orders".

### What is used

```csharp
public interface IExchangeLimits
{
    IReadOnlyList<ExchangeLimitUsage> Usage();

    bool HasRoom(Market market, TradingMode environment);
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

**`HasRoom` says whether a read that nothing depends on may be made now,** on a market in an environment: one that only keeps a price on a page up to date. It may while less than half of what the service allows itself of the limit of that market is used, no call waits for its turn there, and the limit is not held. What is used is what the limiter let through in the last sixty seconds, or what Binance itself counted within the last minute, where that is more. So what places and protects orders keeps half of the part for itself, whatever the pages ask for: see "Following a price".

## The clock

Binance takes a signed call only when the time that the call carries is near its own: less than a second after it, and no longer before it than `recvWindow` says, which is five seconds. With a clock that is off, Binance refuses the calls of the service, those that protect a position among them. So the gateway holds the clock of the service against Binance's, and new entries pause while it is further from them than the configuration file allows (FR-BIN-5). `Adapters/Binance/BinanceClock.cs` is that watch, and `ClockWatchTests` holds it to the rules below, on a clock that the tests move.

| Setting | In the tracked file | |
|---|---|---|
| `Binance:ClockCheckSeconds` | 60 | The time between two readings of Binance's clocks, from 30 to 3600. |
| `Binance:ClockToleranceMilliseconds` | 500 | How far the clock of the service may be from Binance's, ahead of it or behind it, from 1 to 999. |

| Rule | Detail |
|---|---|
| **Each market has a clock in each environment, and each is watched on its own.** | That makes four. One that is off, or that cannot be read, pauses new entries on its market in its environment, and leaves the other three as they are. |
| **The clocks are read on a schedule.** | The job `clock-watch` reads them every `Binance:ClockCheckSeconds`, and as the background work starts: what was read is kept in memory, so an application that starts has no reading. It reads all four at once, with the two calls of a clock in "The calls". They are the service's own, and each costs 1 of request weight. See [background-work.md](background-work.md). |
| **What the clock differs by is taken at the middle of the call.** | Binance reads its clock somewhere between the call and its answer. The drift is the time of the service half way between the two, less the time that Binance answered with: more than nothing is ahead of Binance, and less is behind it. The wait for a turn at the limiter is no part of it. A reading is exact to half of how long the answer took, which is reported with it, and that is longer when a connection had to be opened for the call. |
| **New entries pause while the clock is further from Binance's than the tolerance,** and go on when a reading is back within it. | In either direction. The tolerance is below the second at which Binance begins to refuse, so that entries pause before calls fail. Exactly the tolerance is within it. |
| **A clock that was not read pauses them too.** | A reading counts for three times `Binance:ClockCheckSeconds`. A read that fails once or twice pauses nothing. After that the clock is unknown, as it is from the start of the application to its first reading: a clock that cannot be read is not one that is right (NFR-REL-2). How old a reading is, is counted in time that no change of the time of day moves. |
| **Binance's own word counts for more than a reading.** | Its error `-1021` says that the time of a call was outside of what it takes. `BinanceCall` tells the watch, and new entries pause on the market and in the environment of that call until its clock is read again. The call ends as `Failed`, as before: see "Outcomes". |
| The watch pauses nothing by itself. | What places an entry asks `IExchangeClock.EntriesPaused` first, and places none while it says so: `AgentOrders` of the Decision Engine does. See "When no entry is placed" in [orders.md](orders.md). What is open stays as it is, with its protective orders resting on Binance, and is still closed and protected. |
| It watches in memory, for one process. | As the limiter counts. An instance that stands by reads no clock, so it has new entries paused, and it runs no agent either. |

### How the clock stands

```csharp
public interface IExchangeClock
{
    IReadOnlyList<ExchangeClockDrift> Drift();

    bool EntriesPaused(Market market, TradingMode mode);
}

public sealed record ExchangeClockDrift(
    Market Market, TradingMode Environment, ExchangeClockState State,
    TimeSpan? Drift, TimeSpan? RoundTrip, DateTimeOffset? ReadAt, TimeSpan Tolerance);
```

The watch says how the clock of the service stands against each of Binance's: Spot and then Futures, each on the real exchange and then in the test environment. It is what the operator status screen shows of the clock (FR-OPS-2), which is not built.

| `State` | When | New entries |
|---|---|---|
| `Within` | The latest reading is within the tolerance. | Go on |
| `Exceeded` | The latest reading is further from Binance's clock than the tolerance. | Paused |
| `Unknown` | The clock was not read yet, or not for longer than a reading counts. | Paused |
| `Refused` | Binance refused a call for its time, and the clock was not read since. | Paused |

| | |
|---|---|
| `Drift`, `RoundTrip`, `ReadAt` | What the latest reading found, how long its answer took, and when it was taken, by the clock of the service. They are of the latest reading however old it is, and empty before the first. |
| `Tolerance` | `Binance:ClockToleranceMilliseconds`. |
| `EntriesPaused` | Whether new entries are paused there, which they are in every state but `Within`. The method says the same for one market in one environment. |

## Outcomes

A call that gives no answer is not an error. Its result says how it ended, so that a caller, which may name no type of the network, can tell what to do next.

| Outcome | When | What it is for |
|---|---|---|
| `Succeeded` | Binance answered with what was asked for. | The time, the balances, or the market data. |
| `AuthenticationFailed` | Binance answered as it answers a wrong credential: 401, or one of its errors `-1002`, `-1022`, `-2008`, `-2014`, `-2015`. | The credential is checked again, with `IKeyValidation.RevalidateExchangeKeyAsync` (FR-KEY-3). The outcome does not say that the credential is wrong: the check decides that. |
| `TimedOut` | The call ended without an answer, and its caller did not cancel it. | A call that reads may be made again. What became of a call that placed an order is not known: see "The orders". |
| `RateLimited` | The limit on requests: the limiter held the call back for longer than a call waits, and it was not sent, or Binance answered with its signal of too many, 429 or 418. | The call is made again later, and not at once: until the limiter has room again, the same call ends the same. It is no failure of Binance, and says nothing about a credential. |
| `Failed` | Anything else: no connection, a clock that is off, a block of the service's address by the firewall in front of Binance, a fault of the exchange, or an answer that is not what Binance sends. | Counted where repeated failures are (NFR-REL-4). |

- **Only Binance's answer to a wrong credential is `AuthenticationFailed`.** A status that could also mean something else, such as 403, is `Failed`: a block of the service's address must not have every stored key checked, or marked.
- **`Failed` does not say which failure it was.** A clock that is off is told apart by what acts on it: the watch on the clock hears of Binance's error `-1021`, and pauses new entries. See "The clock".
- **A result holds what was read when the call succeeded, and never otherwise.** A `ServerTime` is there with `Succeeded` alone, and so are balances, candles, and every figure of the market data.
- **A success that is not what was asked for is `Failed`:** the page of something in between, an answer of another kind, a clock without a time, an amount that is not a number.
- **How a read of market data ended is shared as what it read is.** Whoever waited for the read gets its outcome, and a failure counts for 5 seconds: see "Market data".

The check of a key says the same with a `KeyVerdict`: `Succeeded` is `Accepted`, `AuthenticationFailed` is `Refused`, and anything else is `Unreachable`.

## The orders

The orders are in the namespace `Ecnanib.ExchangeGateway.Trading`, which only the Decision Engine may use: see "Placing orders" in [architecture.md](architecture.md). `Adapters/Binance/BinanceOrders.cs` is Binance's, and `OrderPlacementTests` holds it to the rules below.

```csharp
public interface IExchangeOrders
{
    Task<ExchangeOrderResult> PlaceAsync(
        Guid userId, Market market, TradingMode mode, string apiKey, string secretKey,
        ExchangeOrderRequest order, CancellationToken cancellationToken);

    Task<ExchangeOrderResult> FindAsync(
        Guid userId, Market market, TradingMode mode, string apiKey, string secretKey,
        ExchangeOrderRef order, CancellationToken cancellationToken);

    Task<ExchangeOrderResult> CancelAsync(
        Guid userId, Market market, TradingMode mode, string apiKey, string secretKey,
        ExchangeOrderRef order, CancellationToken cancellationToken);

    Task<ExchangeFills> FillsAsync(
        Guid userId, Market market, TradingMode mode, string apiKey, string secretKey,
        string symbol, string exchangeOrderId, CancellationToken cancellationToken);

    Task<ExchangePairResult> PlacePairAsync(
        Guid userId, TradingMode mode, string apiKey, string secretKey,
        ExchangePairRequest pair, CancellationToken cancellationToken);

    Task<ExchangePairResult> CancelPairAsync(
        Guid userId, TradingMode mode, string apiKey, string secretKey,
        ExchangePairRef pair, CancellationToken cancellationToken);

    Task<ExchangeOrderOutcome> SetIsolatedMarginAsync(
        Guid userId, TradingMode mode, string apiKey, string secretKey,
        string symbol, int leverage, CancellationToken cancellationToken);
}

public sealed record ExchangeOrderRequest(
    string ClientOrderId, string Symbol, ExchangeOrderSide Side, ExchangeOrderType Type,
    decimal Quantity, decimal? Price, bool Closes);

public sealed record ExchangePairRequest(
    string PairId, string Symbol, decimal Quantity,
    string StopLossId, decimal StopLossPrice, string TakeProfitId, decimal TakeProfitPrice);

public sealed record ExchangePairResult(ExchangeOrderOutcome Outcome, ExchangeOrder? StopLoss, ExchangeOrder? TakeProfit);

public sealed record ExchangeOrderResult(ExchangeOrderOutcome Outcome, ExchangeOrder? Order);

public sealed record ExchangeOrder(
    string ExchangeOrderId, ExchangeOrderStatus Status, decimal ExecutedQuantity, decimal? AveragePrice,
    IReadOnlyList<ExchangeFill> Fills);
```

| Rule | Detail |
|---|---|
| **Every order carries the identifier that its caller gave it** (FR-BIN-6). | 1 to 36 characters of letters, digits, and `. : / _ -`, which is what Binance takes on both markets. It is sent as `newClientOrderId`, and for a stop of Futures as `clientAlgoId`. An identifier that Binance would not take is an error of the caller, and nothing is sent. |
| **An order is asked about and taken off by that identifier.** | So it can be asked about although the call that placed it never said what Binance made of it. |
| **There are four kinds of order.** | `Market` is executed at once. `Stop` rests until the price reaches its trigger, and is then placed at the market: `STOP_LOSS` on Spot, and `STOP_MARKET` on Futures. `TakeProfit` rests the same way, with its trigger on the side of a profit: a sale is triggered at its price or above, and a purchase at its price or below. It is `TAKE_PROFIT_MARKET` on Futures, where it is the take-profit of a position (FR-BIN-F3), and `TAKE_PROFIT` on Spot, where nothing places one alone: the take-profit of a position there is one order of a pair. `Limit` is executed at its price or a better one, and what is not executed at once rests until it is, or until it is taken off: `LIMIT`, good until it is taken off, on both markets (FR-BIN-S2). `Price` of a request is the trigger of a stop or of a take-profit, or the price of a limit order. |
| **The pair of Spot is one list of two orders.** | A take-profit above the price and a stop-loss below it, which both sell the same quantity. Each is a stop: it is placed at the market when the price reaches its trigger, `TAKE_PROFIT` and `STOP_LOSS`, and Binance ends the other then (FR-BIN-S2). Binance calls the list an OCO. `PlacePairAsync` places it with one call, and Binance takes both orders or neither. It takes no market: only Spot has such a pair, as only Futures has a mark price. |
| **A pair carries three identifiers.** | One for each order, and one for the pair, which Binance keeps apart from those of the orders: `belowClientOrderId`, `aboveClientOrderId`, and `listClientOrderId`. A pair whose three are not different, or not what Binance takes, is an error of the caller, and nothing is sent. |
| **Each order of a pair is an order like any other.** | It is asked about with `FindAsync`, by its own identifier. `CancelPairAsync` takes both off with one call, by the identifier of the pair, and Binance reports each under the identifier that it carried: `origClientOrderId`, where `clientOrderId` is the identifier of the call that took it off. An answer that does not say both orders does not read. |
| **Binance takes a pair while the price is between its two triggers.** | It refuses one whose stop-loss is at or above the price, or whose take-profit is at or below it, as it refuses an order that would be triggered at once. That is a refusal: there is neither order. |
| **A stop and a take-profit of Futures are conditional orders.** | Binance takes them with calls of their own, under `/fapi/v1/algoOrder`, and answers one at the call of the other orders with its error `-4120`. It gives each an id of its own until it is triggered. Then it becomes an order, and `ExchangeOrderId` is the number of that order, which its executions are read with. An order is asked about and taken off where Binance keeps it, which its kind says: `ExchangeOrderRef.Type`. |
| **Both are triggered on the mark price** (FR-BIN-F3). | `workingType=MARK_PRICE`. Binance's own default is the price that the contract last traded at, which one trade can move. Binance refuses one that would be triggered at once, with its error `-2021`: that is a refusal, and there is no order. |
| **Binance ends neither for the other.** | They are two orders, and no list: when one is executed, the other rests on. What placed them takes it off: see "The take-profit of Futures" in [orders.md](orders.md). |
| **An order that only closes is sent to Futures as one that only reduces.** | `Closes` is `reduceOnly=true` there, so that the order opens nothing when the position is gone already. Spot has no positions: a sale there sells what the account holds. So what sells on Spot sells the quantity that its agent bought, and no more: see "The stop-loss" in [orders.md](orders.md). |
| **The answer says what became of the order.** | An order at the market, and one at a price, is asked for with `newOrderRespType=FULL` on Spot, which has its executions with their fees, and `RESULT` on Futures. A stop and a pair are asked for with `RESULT`: nothing of them is executed when they are placed. |
| **A call is made once.** | Nothing here places an order again. |
| **A call that changes something cannot be cancelled once it is sent.** | Cancelling ends the wait at the limiter, with the error of a cancelled call, and then nothing was sent. From the moment that the call is sent it runs to its answer or to its time limit, whatever its caller does: a call that was dropped half way would leave nobody knowing whether Binance took the order. |
| **The margin of a symbol is set with two calls** (FR-BIN-F2). | `SetIsolatedMarginAsync` gives a symbol of Futures isolated margin, and then the leverage that its caller asks for. Each is a setting of the symbol in the account of the credential, and neither is one of the whole account. It takes no market: only Futures has margin. See "The margin of a symbol". |

How a call ended says whether there is an order, and errs on the side of not knowing.

| `ExchangeOrderOutcome` | When | Of a call that placed an order |
|---|---|---|
| `Answered` | Binance answered with the order as it has it. | There is an order, and the result says what became of it. |
| `Rejected` | Binance refused the call with one of its errors, or what stands in front of Binance did, with a status from 400 to 499. | There is no order. |
| `NotSent` | The limiter held the call back, or Binance answered with its signal of too many, 429 or 418. | There is no order. |
| `KeyRefused` | Binance answered as it answers a wrong credential: see "Outcomes". | There is no order. The credential is checked again. |
| `NotFound` | Binance's own word that it has no such order: its errors `-2013` and `-2011`. Only a call that asks about an order, or takes one off, ends so. | |
| `Unknown` | No answer in time, a connection that broke, a status from 500, the status 408, one of Binance's errors `-1000`, `-1001`, `-1006`, `-1007`, of which it says that the execution is unknown, or an answer that says that the call went well and does not read. | There may be an order. It is asked about before anything else is done with it. |

- **Only `NotFound` says that Binance has no order.** Every other end of a question about an order says nothing about it: an order is never taken for one that was not placed because the question failed.
- **A call about a pair ends as a call about an order does,** and what it says, it says of both orders: `ExchangePairResult` has both when Binance answered with them, and neither otherwise. A pair that Binance does not take off, because one of its orders was executed and the pair is over, ends as `NotFound`, or as `Rejected` where Binance answers with another of its errors: either way each order is asked about.
- **`NotFound` is final only once the order could no longer arrive:** `ExchangeOrderTimes.FinalAfter`, 10 seconds after the call that placed it ended. Binance takes a call for 5 seconds after the time that it carries. What that means for an order is in "An order is never sent twice" in [orders.md](orders.md).
- **An amount of an order is exact,** and is sent in digits with a point, whatever the culture.

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
- It reads the whole account. What is free of the asset that a symbol is priced in is what an entry on Spot is held to, where the order is placed (FR-BIN-S3): see "The quote balance" in [orders.md](orders.md). What is available of it in the wallet of Futures is what the margin of an entry there is held to (FR-BIN-F6): see "The margin of Futures" in [orders.md](orders.md).

## The account of Futures

Futures agents use isolated margin and one-way position mode. The system sets margin type and leverage for the agent's symbol before entry. It does not change account-wide settings; if the account is in a conflicting mode or already holds a position in the symbol, the agent does not start and the user is told what to change (FR-BIN-F2). The gateway reads how an account stands, and sets the two settings of a symbol. `FuturesAccountTests` holds it to the rules below.

`GetFuturesAccountAsync` reads how the account of a credential stands for one symbol, with two calls: the settings of the account, and then its positions in the symbol.

| | Read from | |
|---|---|---|
| `HedgeMode` | `dualSidePosition` of the settings | Whether the account keeps a long and a short position of a symbol apart. In one-way mode a symbol has one position. It holds for the whole account. |
| `MultiAssetsMargin` | `multiAssetsMargin` of the settings | Whether the account margins its positions with more than one asset. Binance takes no isolated margin in that mode. It holds for the whole account. |
| `HoldsPosition` | `positionAmt` of the rows of the symbol | Whether the account holds a position in the symbol, long or short. Binance lists a symbol that has a position or orders, with an amount of nothing where it has orders only. In hedge mode a symbol has a row for each side. |

- **It only reads.** The gateway has no call that sets the position mode or the asset mode of an account: both are its user's to set at Binance.
- **An account that does not say both of its modes is not read.** The answer is `Failed`, as is a list of positions with a row that does not read: an account is never taken for one in one-way mode because the answer did not say.
- **The first call that does not answer is how the read ended.** The positions are not asked for once the settings did not say.
- **A result says how the account stands when the read succeeded, and never otherwise.**

### The margin of a symbol

| Rule | Detail |
|---|---|
| **The margin first, and then the leverage.** | `SetIsolatedMarginAsync` asks for isolated margin, and for the leverage only once the symbol has it. A margin that Binance did not take is followed by no call. |
| **A symbol that has isolated margin already is one that has it.** | Binance answers it with its error `-4046`, which says that nothing is to change. That is no refusal. |
| **A leverage is set when Binance answers with it.** | An answer that names another leverage, or none, does not read: how the symbol is margined is not known then. |
| **It ends as a call about an order ends.** | `Answered`: the symbol has isolated margin and that leverage. `Rejected`: Binance refused one of the two, which it does while the account has open orders or a position in the symbol that the margin would change, in Multi-Assets mode, and for a leverage that it does not allow. `KeyRefused`, `NotSent`, and `Unknown` as in "The orders". Only `Answered` says that both are set. |
| **A leverage is a whole number of 1 or more.** | Anything else is an error of the caller, and nothing is sent. |
| Neither moves funds. | Each names the symbol and what the symbol is to have, and nothing else. The margin of a position is taken from the wallet of Futures by the order that opens it. The gateway has no call that adds margin to a position: see "No funds are moved". |

## The position of an account, and its margin

The system checks the margin state of open positions at each reconciliation and alerts the user when a position approaches liquidation. It never adds margin on its own (FR-BIN-F5). The gateway reads the position, and has nothing that adds margin. `FuturesPositionTests` holds it to the rules below.

`GetFuturesPositionAsync` reads the position that the account of a credential holds in one symbol, with one call: its positions in the symbol, which is the second of the two calls of "The account of Futures".

| | Read from | |
|---|---|---|
| `Quantity` | `positionAmt` of the row of the symbol that holds something | How much the position holds: above zero for a long position, and below zero for a short one. Zero when no row of the symbol holds anything: the account has no position in it. |
| `EntryPrice` | `entryPrice` | The average price that the position was entered at. |
| `MarkPrice` | `markPrice` | The mark price of the symbol, which a liquidation goes by. |
| `LiquidationPrice` | `liquidationPrice` | The mark price at which Binance liquidates the position, which is where the margin of the position no longer covers what it must keep. Binance writes nothing, as `0`, where it names none: that is no price. |

- **It only reads.** Binance has a call that adds margin to a position, or takes margin out of one: the gateway does not make it. See "No funds are moved".
- **A position has its prices, or it is not read.** A row that holds something and lacks its entry price, its mark price, or its liquidation price is `Failed`, as is a list with a row that does not read: a position is never taken for one that is far from liquidation because the answer did not say.
- **How the margin stands is said by the prices.** The liquidation price is Binance's own word of where the margin of the position runs out, with the maintenance margin in it. The amounts of the margin are in the same answer, and are not read: nothing goes by them.
- **Two rows of a symbol that hold something are no one position.** In hedge mode an account can hold a long and a short position in a symbol. That is `Failed`: the service trades in one-way mode.
- **A result says a position when the read succeeded, and never otherwise.**

## The funding of an account

The current funding rate and the next funding time are included in the data package, and funding paid or received is recorded per position (FR-BIN-F4). The rate and the next funding time come with the mark price: see "Market data". This is what an account received and paid. `FundingReadTests` holds it to the rules below.

`GetFundingAsync` reads the funding that the account of a credential received and paid in one symbol between two moments, both of them included, with one call: the rows of its income that are funding.

| | Read from | |
|---|---|---|
| `TransactionId` | `tranId` | The number that Binance gave the row, which is unique among the funding of an account. Binance writes it as a number, which is kept digit for digit. |
| `Amount` | `income` | How much: above zero when the account received it, and below zero when it paid. |
| `Asset` | `asset` | The asset that it was received or paid in. |
| `At` | `time` | When, by the clock of Binance. |
| `More` | | Whether the answer has 1000 rows, which is the most that Binance gives: there may be more of the time that was asked for. |

- **It only reads,** as every call of the income of an account does.
- **The rows are said oldest first,** whatever order Binance gave them in, and those of one moment in the order of their numbers.
- **A list is whole or it is not a list.** A row that does not read, that is no funding, or that is of another symbol than the one that was asked for makes the answer `Failed`: a part of a list would make the rest look as if it had not been paid.
- **A time that ends before it begins is an error of the caller,** and nothing is sent. One moment is a time.
- **It is the heaviest call of the gateway,** at 30 of request weight. What reads the funding of a position reads it when it is due, and for so many positions in a run: see "Funding" in [orders.md](orders.md).
- Binance keeps the income of an account for three months.

## The account event feed

Order and position changes are tracked through the exchange's account event feed where available (FR-BIN-7). Binance says what changes in an account on a connection that stays open, as it happens: an order that was executed or taken off, and what the account holds. The gateway follows the account of a credential on it, and says of every event which symbol it is about. `Adapters/Binance/BinanceFeed.cs` is that, and `AccountFeedTests` holds it to the rules below, against a server of the test's in Binance's place.

```csharp
public interface IExchangeAccountFeed
{
    bool IsAvailable(Market market, TradingMode mode);

    ExchangeFeedState StateOf(Guid userId, Market market, TradingMode mode);

    Task<ExchangeFeedOutcome> FollowAsync(
        Guid userId, Market market, TradingMode mode, string apiKey, string secretKey, CancellationToken cancellationToken);

    void Keep(Guid userId, Market market, TradingMode mode);

    Task<IReadOnlyList<ExchangeAccountChange>> TakeAsync(TimeSpan wait, CancellationToken cancellationToken);
}

public sealed record ExchangeAccountChange(Guid UserId, Market Market, TradingMode Mode, string? Symbol);
```

| Setting | In the tracked file | |
|---|---|---|
| `Binance:Spot:Live:FeedUrl` | `wss://ws-api.binance.com:443/ws-api/v3` | Binance's WebSocket API of Spot, on the real exchange. |
| `Binance:Spot:Demo:FeedUrl` | `wss://demo-ws-api.binance.com:443/ws-api/v3` | The same, in its test environment. |
| `Binance:Futures:Live:FeedUrl` | `wss://fstream.binance.com/private` | The address that the user data streams of Futures are under, on the real exchange. |
| `Binance:Futures:Demo:FeedUrl` | `wss://demo-fstream.binance.com/private` | The same, in its test environment. |

| Rule | Detail |
|---|---|
| **A feed is there where the file names its address.** | `IsAvailable` says so for a market in an environment. Where the file names none, or leaves it empty, no account is followed there, and `FollowAsync` ends as `NotAvailable`: the records of an agent are then reconciled on the schedule alone. That is "where available". |
| **An event is a signal, and no record.** | A change says that something of a symbol changed in the account of a user, on a market in an environment, and nothing of what: no status, no quantity, and no price of an event leaves the gateway. What keeps the orders and the positions asks Binance what changed, with the calls that read, and writes what those say: see "The account event feed" in [orders.md](orders.md). So an event that is missed, late, or not what Binance's documentation shows changes when Binance is asked, and nothing else. |
| **It only reads.** | Following an account places no order, and moves nothing (FR-BIN-9). See "No funds are moved". |
| **The credential comes with the call, and is kept nowhere** (FR-KEY-9). | It is needed when an account begins to be followed, and again when Binance wants the feed renewed: `StateOf` says `RenewalDue` then. The bytes that the secret key was read into are cleared. The key that names a stream of Futures is in the address that the connection was opened with, and nowhere else. |
| **A feed that nobody asks for is closed.** | Whoever follows an account says so again and again, with `Keep`. A feed that nobody kept for `ExchangeFeedTimes.IdleAfter`, which is 3 minutes, is closed. So an instance of the application that no longer does the background work follows nothing after that: see [background-work.md](background-work.md). |
| **A change is said once until it is taken.** | `TakeAsync` gives the changes that the feeds said since they were last taken, each once however often it was said, and waits for one when there is none. One execution is several events at Binance. |
| **A feed that begins or ends is a change of everything in its account.** | `Symbol` is empty then. What happened in the account before it was followed, and while a feed ended, was not heard. So is an event of a kind that says a change and does not say its symbol where it is looked for, and a message that does not read. |
| **A feed that ends is no error.** | Binance closes every connection after a day, a connection breaks, and Binance ends a stream that was not renewed. The account is no longer followed, `StateOf` says so, and it is followed again when it is asked for, with the credential. |
| **It follows in memory, for one process.** | As the limiter counts. Nothing of a feed is written to the database. |

### On each market

| | Spot | Futures |
|---|---|---|
| The feed | Binance's WebSocket API. Binance took the REST calls that named a stream of an account of Spot out of service in February 2026. | A user data stream, which a call begins and names. |
| To follow an account | One request on the connection, `userDataStream.subscribe.signature`, with the API key, the time, and their signature under the secret key (HMAC-SHA256), as a signed call has it. Binance takes it with any kind of key, and with no login before it. | `POST /fapi/v1/listenKey`, which carries the API key alone. Binance answers with the key of the stream, and with the same key while one is valid. The stream is read at `<FeedUrl>/ws/<key>`. |
| To renew it | Nothing. | `PUT /fapi/v1/listenKey`, half an hour after the stream was begun or renewed (`BinanceFeed.RenewEvery`): Binance ends one that is not renewed within an hour. |
| An event that says a change, with its symbol | `executionReport` and `listStatus`, each with `s`, inside `event`. | `ORDER_TRADE_UPDATE` and `ALGO_UPDATE`, each with `o.s`, `TRADE_LITE`, with `s`, and `ACCOUNT_UPDATE`, with `s` of each position of `a.P`, or `a.S`. |
| An event that ends the feed | `eventStreamTerminated` | `listenKeyExpired` |
| What says nothing here | What the account has of an asset (`outboundAccountPosition`, `balanceUpdate`), and anything else. | A margin call, a change of the leverage of a symbol, a change of the wallet alone, and anything else. |

| Rule | Detail |
|---|---|
| **A connection sends one thing at most.** | The request that subscribes, on Spot, and nothing on Futures. Binance's WebSocket API takes orders as well, with other requests: `BinanceFeedConnection`, which is the one type of the gateway that opens a WebSocket, has no way to send one. `NoFundMovementTests` holds what is sent on a feed to that one request, and the WebSocket to that one type. So a feed is no second way to an order beside the calls of the orders (NFR-SEC-6). |
| **Opening a feed costs of the limit on requests,** so it passes the limiter (FR-BIN-4). | The call that begins a stream of Futures passes it as every call does. The connection of Spot and its request are counted as 4 of the limit of Spot. A connection that Binance refuses with its signal of too many, 429 or 418, tells the limiter, which then sends no call on that limit for a while. |
| **No more than 20 feeds are opened in a minute,** for all users. | `BinanceFeed.MostOpenedAMinute`. Binance limits how many connections an address opens, 300 in five minutes on Spot, and a start of the application would open one for every account at once. `FollowAsync` ends as `NotNow` for the rest, which are opened with the next minutes. |
| **A connection is made within 5 seconds,** or not at all. | `BinanceFeed.ConnectsWithin`. It is less than the time limit of a call: an address that does not answer keeps whoever follows the accounts from listening to the feeds that are open. On Spot, Binance's answer to the request has the time limit of a call, 15 seconds. |
| **A request that Binance refuses for its time tells the watch on the clock.** | Its error `-1021`, as with a call: see "The clock". |

`ExchangeFeedOutcome` says how following an account ended:

| Outcome | When |
|---|---|
| `Following` | The account is followed. Only this says so. |
| `NotAvailable` | The file names no address of a feed for the market in the environment. |
| `KeyRefused` | Binance answered as it answers a wrong credential: see "Outcomes". The credential is checked again. |
| `NotNow` | The limit on requests has no room, Binance signalled that it has had too many, or enough feeds were opened in the minute. It is asked again later. |
| `Failed` | Anything else: no connection, a fault of Binance, or an answer that is not what it sends. |

## Market data

Market data is candles, prices, and the rules of the symbols, and on Futures also the mark price, the funding rate, and the open interest. It is fetched once per symbol and timeframe and shared by all agents that need it (FR-BIN-3): every read costs of the one budget that all users spend, so a read for each agent would be what uses it up (risk R2). `Adapters/Binance/BinanceMarketData.cs` reads it, and `MarketDataTests`, `MarketDataSharingTests`, and `CandleCacheTests` hold it to the rules below, on a clock that the tests move.

```csharp
public interface IExchangeMarketData
{
    Task<ExchangeCandles> GetCandlesAsync(
        Market market, TradingMode mode, string symbol, CandleInterval interval, CancellationToken cancellationToken);

    Task<ExchangePrice> GetPriceAsync(Market market, TradingMode mode, string symbol, CancellationToken cancellationToken);

    Task<ExchangeSymbols> GetSymbolsAsync(Market market, TradingMode mode, CancellationToken cancellationToken);

    Task<ExchangeMarkPrice> GetMarkPriceAsync(TradingMode mode, string symbol, CancellationToken cancellationToken);

    Task<ExchangeOpenInterest> GetOpenInterestAsync(TradingMode mode, string symbol, CancellationToken cancellationToken);
}

public sealed record ExchangeCandles(ExchangeCallOutcome Outcome, IReadOnlyList<ExchangeCandle> Candles, DateTimeOffset? ReadAt);

public sealed record ExchangeCandle(
    DateTimeOffset OpenedAt, DateTimeOffset ClosedAt, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);
```

### Read once, and shared

| Rule | Detail |
|---|---|
| **Whoever asks first causes the read.** | Whoever asks while that read is under way waits for the same read, and whoever asks while what was read still counts gets it with no call. So two agents on the same symbol and timeframe cause one fetch, and not two. Everyone gets the same answer, as it was read: `ReadAt` says when the read was sent. |
| **Nothing is read that nobody asked for.** | There is no job that reads ahead. The gateway may use no other component, so it does not know which agents run, and whatever starts a cycle asks: the job of the Decision Engine does, at each close, for the symbols and timeframes of the agents that run then. Each cycle that starts then asks for the rest of its data package: the candles of the higher timeframes, and on Futures the mark price and the open interest. See [cycles.md](cycles.md). And what works out the equity of an agent asks for the price that its open position is valued at, the mark price on Futures and the price that the symbol last traded at on Spot, each time that the equity is asked for: at a cycle, as a plan is checked, and at every reconciliation (FR-RSK-8). An agent that holds no position is asked for none. See "The equity of an agent" in [orders.md](orders.md). |
| **A read is the service's own call.** | It needs no credential, and takes the turn of the service at the limiter, in one line with the reads of the clock: see "The limiter". It is in the log of outbound calls once, however many share it. |
| **Each market has data of its own in each environment.** | A read goes to the address of its market in its environment, as every call does. An agent in the test environment decides on the candles, the prices, and the rules of the environment that its orders go to. |
| **Only the symbols of `Binance:Symbols` are served.** | Any other symbol is no call: it ends with an error, and nothing is sent. So what is held has a bound, and no text of a caller reaches Binance. |
| **The price of a symbol of `Binance:FeeSymbols` is served as well, and nothing else of it.** | `GetPriceAsync` takes a symbol of either list. A fee that Binance took in an asset of neither side of the symbol that was traded, such as BNB, is valued by the price that such a symbol last traded at (FR-BIN-10): see "The value of a fee" in [orders.md](orders.md). Its candles, its mark price, and its open interest are not read, and `GetSymbolsAsync` does not list it: no agent trades it for being listed there. |
| **A read belongs to nobody who waits for it.** | An asker that cancels stops waiting, with the error of a cancelled call, and the others get the answer. The read ends when the application stops. |
| **A read that failed is shared too, and is not made again for 5 seconds.** | Whoever waited gets how it ended, and so does whoever asks in those seconds, with no call. So agents that ask one after the other while Binance does not answer do not each make the call. It is no breaker: after the seconds the next ask reads again. |

### For how long it counts

| What | One read is for | It counts for |
|---|---|---|
| Candles | A symbol and a length of a candle, on a market in an environment | Until the candle that is in progress closes, and 1 minute at most |
| The price | A symbol, on a market in an environment | 1 second |
| The mark price, with the funding rate and the time of the next funding | A symbol of Futures, in an environment | 1 second |
| The open interest | A symbol of Futures, in an environment | 1 second |
| The symbols, with their rules | A market in an environment | 1 hour |
| A read that failed | The same | 5 seconds |

- **These are fixed, as the time limit of a call is,** and are not settings: `CandlesCount`, `MomentCounts`, `SymbolsCount`, and `FailureCounts` of `BinanceMarketData`.
- **Time is counted in time that no change of the time of day moves.** Only the close of a candle is a time of day, which is looked at with every ask.
- **What no longer counts is given to nobody, and is let go of when its time is up,** by a timer, without waiting for an ask.

### Candles

Candles are cached only for as long as cycles need them (NFR-DAT-6). A cycle takes its candles as it starts, and the cycles that a close starts do so within half a minute (NFR-PRF-3). So candles are held for twice that, and never past the close of the candle that is in progress, after which a cycle needs newer ones.

| Rule | Detail |
|---|---|
| **A length of a candle is a `CandleInterval`.** | `FifteenMinutes`, `OneHour`, `FourHours`, `OneDay`, and `OneWeek`: the four timeframes of an agent, and the week, which is the context of the longer ones (SRS 4.6). Binance's days begin at midnight in UTC, and its weeks on a Monday. |
| **Every read asks for the same candles:** the newest 499 rows. | So asks for one symbol and timeframe share one read, however many candles each needs. It gives up to `ExchangeCandles.Most` candles, which is 498, oldest first, and an asker takes the newest that it needs. There is no range of the past to ask for. |
| **Only closed candles are given** (FR-CYC-1). | Which candle is closed is Binance's word, and not the clock's: a row is closed when another follows it in the answer. The newest row is the candle in progress, and is never given. |
| **A candle ends when the next one begins.** | `ClosedAt` is a millisecond after the last one that Binance gives the candle, which is the close that a cycle starts at. No length is worked out, so a week is Binance's week. |
| **The newest candle says through when the candles reach.** | Just after a close Binance may not have begun the next candle. The newest row is then the candle that has closed by the clock of the service, and it is not given: the candles come one short. That is no failure. What came short is held for nobody, so a cycle that finds `Candles[^1].ClosedAt` before its close asks again, and that is a read again. What starts the cycles does that, once for all the agents that share the candles, a second apart, for half a minute after the close: see "The candle of the close" in [cycles.md](cycles.md). |
| **Experience asks for the candles after a call and after a vetoed plan.** | The job that scores the calls of the analysts and follows the shadow trades asks for the candles of each symbol and timeframe that it has something due for, once for all of it, and takes the ones that closed after a cycle (FR-EXP-3, FR-EXP-5). It asks for no range of the past: what the newest candles no longer reach is not scored. See "The job" in [experience.md](experience.md). |
| **Candles are whole or they are none.** | A row that does not read, and rows that are not in the order of their time, make the answer `Failed`. Candles that Binance does not have, during a halt, are a gap between two rows, and no failure. |

### Symbols

`GetSymbolsAsync` gives the symbols of the configuration file that a market has, in the order of the file, each with the rules that an order in it must keep.

```csharp
public sealed record ExchangeSymbol(
    string Symbol, string BaseAsset, string QuoteAsset, bool Trading,
    decimal PriceStep, decimal QuantityStep, decimal MinQuantity, decimal MaxQuantity, decimal MinValue,
    bool StopOrders, bool PairOrders, bool TakeProfitOrders);
```

| | Spot | Futures |
|---|---|---|
| Read from | `symbols[]` | `symbols[]`, where `contractType` is `PERPETUAL` |
| `Trading` | `status` is `TRADING` | The same |
| `PriceStep` | `tickSize` of `PRICE_FILTER` | The same |
| `QuantityStep`, `MinQuantity`, `MaxQuantity` | `stepSize`, `minQty`, `maxQty` of `LOT_SIZE` | The same |
| `MinValue` | `minNotional` of `NOTIONAL`, or of `MIN_NOTIONAL` | `notional` of `MIN_NOTIONAL` |
| `StopOrders` | `orderTypes` has `STOP_LOSS` | `orderTypes` has `STOP_MARKET` |
| `PairOrders` | `ocoAllowed` is true, and `orderTypes` has `STOP_LOSS` and `TAKE_PROFIT` | Never |
| `TakeProfitOrders` | `orderTypes` has `TAKE_PROFIT` | `orderTypes` has `TAKE_PROFIT_MARKET` |

- **A symbol that is not among them is one that the market does not have,** which is what filters the symbols that an agent can be given (FR-AGT-6): Agents asks it for the form of an agent and when a market or a symbol is saved. See "The symbols" in [agents.md](agents.md). The steps, the least quantity, and the least value are what an order is held to before it is placed (FR-RSK-4), and the quantity step and the most quantity what the size of an order is held to as the Risk Guard computes it (FR-RSK-5): see "The size of an order" in [risk-guard.md](risk-guard.md).
- **One call reads every symbol of a market.** Futures has no call for one symbol, and Spot answers a list with a symbol that it lacks with an error for the whole list. The answer of Spot is close to 7 megabytes, once an hour: it is asked without the permission sets of each symbol, with which it is more than twice that.
- **A list is whole or it is none.** A symbol of the file without one of its rules, or that is there twice, makes the answer `Failed`: a list without it would say that the market lacks it.
- **A symbol that takes no stop gets no entry.** `StopOrders` says whether Binance takes a stop in the symbol that is placed at the market when it is triggered, which is what protects a position (FR-RSK-7). A symbol that names no types of order takes none. See "When no entry is placed" in [orders.md](orders.md).
- **A symbol of Spot that takes no pair gets no entry either.** `PairOrders` says whether Binance takes the pair of a stop-loss and a take-profit in the symbol (FR-BIN-S2). A symbol that does not say takes none. On 18 November 2026 both symbols of the tracked file took it, on the real exchange and in the test environment.
- **Nor does a symbol of Futures that takes no take-profit.** `TakeProfitOrders` says whether Binance takes a take-profit in the symbol as an order of its own, which is placed at the market when it is triggered: on Futures it rests beside the stop-loss of a position (FR-BIN-F3).
- The rules of a market order, `MARKET_LOT_SIZE`, are not read.

### Following a price

**Prices update in the UI within 2 seconds, without a manual refresh** (FR-NTF-2). A page that shows a price follows it, and `IExchangePriceWatch` reads it for everyone who does:

```csharp
public interface IExchangePriceWatch
{
    IDisposable Follow(Market market, TradingMode mode, string symbol, ExchangePriceKind kind, Action<WatchedPrice> heard);

    WatchedPrice? Latest(Market market, TradingMode mode, string symbol, ExchangePriceKind kind);
}

public enum ExchangePriceKind { LastTraded, Mark }

public sealed record WatchedPrice(decimal Price, DateTimeOffset ReadAt);
```

| Rule | Detail |
|---|---|
| **A price that is followed is read once a second, for everyone who follows it.** | With `GetPriceAsync`, or `GetMarkPriceAsync` for the mark price, which only Futures has. Those reads are shared, so however many pages of however many users show a symbol, Binance is asked once. The watch waits a second after each read, and not a second from its start: what a read gave counts for a second, and a read that came a moment early would be answered with the price of the read before it. |
| **A price that nobody follows is not read.** | The reads begin with the first follower and end with the last. The gateway reads nothing ahead, as before. |
| **It is read only while the limit has room.** | `IExchangeLimits.HasRoom`, before each read. While the limit has none, nothing is read, and what follows keeps the price that it has. A page says when its price was read, so a price that could not be read for a while shows as the old one that it is. |
| **What a read costs is bounded by the symbols of the file.** | A price is of a symbol, on a market, in an environment: one read a second for each that some page shows. With the two symbols of the tracked file, on both markets of the real exchange, that is 240 of the 4200 that the service allows itself on Spot in a minute, and 120 of the 1680 on Futures. A file with many symbols, whose prices are all followed, comes to the half that `HasRoom` leaves it, and no further. |
| **A read that gives no price is left at that.** | Binance did not answer, or the symbol is one that the file no longer lists, which the position of an agent may still be in. The watch asks again a second later. A read that failed is shared for 5 seconds, as every read of the market data is. An error is said once, as a warning. |
| **What follows is told the price that the watch has, at once.** | And then each price as it is read, with when the read was sent. What a follower throws is logged, and reaches nobody else. |
| **A page that is written asks for what is known.** | `Latest` reads nothing: it answers with the price of the latest read, where someone follows it, and with none otherwise. So no page waits for Binance. |
| **It reads for the pages of this instance.** | It is no job, and nothing of it is stored (NFR-DAT-6). See "One instance" in [live-updates.md](live-updates.md). |
| **What it logs is of no request and of no user.** | The reads of a price begin on the connection of the first page that follows it, and go on for everyone who does. Nothing of that connection flows into them: their events carry no `RequestId` and no `UserId` (NFR-LOG-3). |

Which price a position is valued at is for what keeps the positions to say: see "The position on a page" in [orders.md](orders.md). `PriceWatchTests` holds the watch to the rules above, on a clock that the test moves, and `RequestLimitTests` holds `HasRoom` to its rule.

### Nothing is stored

The database is not a market-data warehouse (NFR-DAT-6). The gateway keeps what it read in memory, for this process, as the limiter counts and the watch on the clock watches, and writes none of it anywhere. There is no table for it: `SingleStoreTests` holds the database to the tables of the model, by name, and `CandleCacheTests` reads everything, over the close of a candle, and finds every row of every table as it was. What a cycle decided on is for the cycle to record, as a summary (FR-CYC-10).

## No funds are moved

Ecnanib never withdraws, deposits, or transfers funds, between the Spot and the Futures wallet of a user included (FR-BIN-9). A key that could withdraw is not even held: see "Binance permissions" in [credentials.md](credentials.md).

| Rule | Detail |
|---|---|
| **The gateway has no such operation.** | `IExchangeAdapter` has the six above, and each reads. `IExchangeMarketData` has five, and each reads, with a call of Binance's public API, which takes no credential and so can move nothing. `IExchangeOrders` has seven: an order, and a pair of them, buys or sells in the account that the credential is of, and moves nothing out of it, into it, or between its wallets, and the margin of a symbol is a setting of that account. `IExchangeAccountFeed` has five, which follow the events of an account, and only read. |
| **It can send Binance nothing but the calls of the list.** | See "The calls". `NoFundMovementTests` asks the gateway for everything it does, in each market and each environment, and holds what was sent to the four lists. It fails for an operation of any of the four interfaces that it does not ask, so an operation that is added is held to the lists too. |
| **A call of the account event feed names nothing.** | Each of the two carries the API key in its header, and no parameter at all: no order, no asset, no amount, and no address. On a connection of a feed the gateway sends the one request that subscribes to the events of an account, on Spot, and nothing on Futures: `NoFundMovementTests` holds it to that. |
| **Binance keeps the calls that move funds apart.** | Withdrawals, the addresses of deposits, and transfers between wallets are in its wallet API, under `/sapi`. The gateway makes one call there, which reads what a key may do. |
| A call about an order names one order, and nothing to move. | Each call that is no `GET` and is about an order carries the identifier of its order, or of its pair. None carries an amount of an asset to send, or an address to send it to. |
| **A call that sets the margin of a symbol names the symbol, and what it is to have.** | The margin type, or the leverage, and nothing else: no asset, no amount, and no address. `NoFundMovementTests` holds the two calls to those parameters. Binance has a call that adds margin to a position of isolated margin, or takes it out, which moves funds within the wallet of Futures: the gateway does not make it, and the service never adds margin on its own (FR-BIN-F5). `NoFundMovementTests` names that call, `/fapi/v1/positionMargin`, as one that is never sent, and holds what is sent about the margin of a position to two calls that read: its positions, and its income. |

## The log

Every call is in the log of outbound calls, as every call of a client of the host's factory is: see "Outbound calls" in [logging.md](logging.md).

```
Outbound call GET fapi.binance.com/fapi/v3/balance Succeeded with status 200 in 61.8 ms {"Usage": {"x-mbx-used-weight-1m": "5"}}
```

- **`Usage` holds what Binance has counted against the service.** Its Spot and its Futures API report it in headers that begin with `X-MBX-USED-WEIGHT` and `X-MBX-ORDER-COUNT`, one for each interval. Its wallet API reports it in headers that begin with `X-SAPI-USED`. The gateway registers all three with `OutboundCallLogOptions`.
- The address is logged without its query, which holds the signature, and no header is logged. So a read of market data is in the log by its path, and not by its symbol.
- **What the price of Futures says is logged, and the limiter does not go by it.** Binance's own clients say of `/fapi/v2/ticker/price` that what its answer has counted is not accurate, and it says 1 whatever was counted before. Every other answer the limiter goes by: see "The limiter".
- **A read of market data is one event, however many share it.** Whoever gets what was read without a call is in no log.

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

The account event feed is no call of a client of the host's factory, so the log of outbound calls does not have its connections. The gateway says them itself, under `Ecnanib.ExchangeGateway.Adapters.Binance.BinanceFeed`:

```
The account event feed of user {UserId} on {Market} {Environment} is followed, at {Host}, after {DurationMs} ms
The account event feed of user {UserId} on {Market} {Environment} could not be followed, at {Host}: {ExchangeFeedOutcome}
The account event feed of user {UserId} on {Market} {Environment} ended, at {Host}: {FeedEnd}
Binance answered the subscription to the account event feed on {Market} {Environment} with its error {BinanceCode}
```

- **An event names the user, the market, the environment, and the host,** and nothing else of the address: the key of a stream of Futures is in its path. No event holds a key, and none holds anything that Binance said of an account. The two calls of the feed of Futures are in the log of outbound calls, as every call is, and the key of a stream that Binance answers with is masked where that log writes a body, at the debug level: see "Masking" in [logging.md](logging.md).
- **`FeedEnd`** is `Closed`, when Binance closed the connection, `Expired`, when it said that the stream is over, `NotKept`, when nobody asked for the feed any more, `Stopped`, with the application, and `Broken`, which is a warning. A feed that could not be followed is a warning as well.

The watch on the clock says what it reads, under `Ecnanib.ExchangeGateway.Adapters.Binance.BinanceClock`:

```
The clock of the service differs from Binance's on Spot Live by -42 ms, read in 87.3 ms
The clock of the service is 612 ms from Binance's on Futures Live, which is more than the 500 ms that it may be. New entries are paused there
The clock of Binance on Spot Demo could not be read, and there is no reading of it that still counts. New entries are paused there until it is read
Binance refused a call on Futures Demo for the time that it carries. New entries are paused there until its clock is read again
The clock of the service is within 500 ms of Binance's on Futures Live again. New entries go on there
```

- **Every reading is in the log,** at the information level, with `Market` and `Environment`. `DriftMs` is how far the clock of the service is ahead of Binance's, in whole milliseconds, and below zero when it is behind. `RoundTripMs` is how long the answer took.
- **A pause is a warning, said once,** when it begins, and not with every reading while it lasts: the clock is further off than `ToleranceMs`, it could not be read for as long as a reading counts, or Binance refused a call for its time. Why a clock could not be read is with the call, in the log of outbound calls or in the events above.
- **That it is over is said once too,** at the information level, with the first reading that is within the tolerance again.

## Checking against Binance itself

The tests answer in Binance's place, with what its documentation says it sends. `LiveBinanceTests` asks Binance itself. It is skipped unless a credential is given for an environment, in the environment of the run, so the suite never reaches Binance by itself and no key is stored:

```
ECNANIB_LIVE_BINANCE_DEMO_KEY=... ECNANIB_LIVE_BINANCE_DEMO_SECRET=... \
  dotnet test --solution Ecnanib.slnx --filter-class "*LiveBinanceTests"
```

The variables are `ECNANIB_LIVE_BINANCE_<ENVIRONMENT>_KEY` and `ECNANIB_LIVE_BINANCE_<ENVIRONMENT>_SECRET`, with `DEMO` or `LIVE`. Give them for one environment or for both. For each that has both, these calls are made, at the addresses of the tracked configuration file. Every one of them reads, and costs request weight only, but for the last five, which place orders in the test environment. No order is placed on the real exchange, and the margin of a symbol is set in the test environment only. Two of the five place orders far from the price, and take them off again, and so does the one of the account event feed, which is the last row. The two before that buy a little at the market, on Spot and on Futures, and sell it again: they are the ones that are executed, with the funds of the test environment, which are none, and each is skipped when the account there does not cover it. The one of Futures is skipped as well when the account there is one that an agent would not start in:

| Call | What it must end as |
|---|---|
| The clock of each market. | `Succeeded`, with a time of now, and with what Binance has counted against the address, which the limiter reports then. |
| The balances of each market, with the credential that was given. | `Succeeded` |
| The check of the credential. | `Accepted`, and on the real exchange with whether it can withdraw and whether it is restricted to addresses. |
| The balances of each market, with a credential that Binance does not know. | `AuthenticationFailed` |
| The check of such a credential. | `Refused` |
| The candles of `BTCUSDT` on each market, of each length. | `Succeeded`, with candles of that length in the order of their time, the newest closed within its length of now, and for 2 of request weight each. |
| The price of `BTCUSDT` and the symbols of each market. | `Succeeded`, with a price, and with `BTCUSDT` among the symbols, with its rules. |
| The mark price and the open interest of `BTCUSDT` on Futures. | `Succeeded`, with a funding rate, and a next funding that is ahead. |
| The position of the account in `BTCUSDT` on Futures, with the credential that was given. | `Succeeded`: no position, or one with its prices, whichever the account holds. |
| The funding that the account received and paid in `BTCUSDT` on Futures in the last thirty days. | `Succeeded`: none, or rows of that time, each with a number of its own, and each at a whole hour, which a funding time of Binance is. |
| A stop of `BTCUSDT` on each market of the test environment, which buys a tenth above the price: asked about before it is placed, placed, asked about, taken off, taken off once more, and asked about again. | `NotFound`, then `Answered` as an order that rests, the same order when it is asked about, `Canceled` when it is taken off, `NotFound` when it is taken off once more, and `Canceled` when it is asked about after that. |
| An order at a price of `BTCUSDT` on Spot of the test environment, which buys a tenth below the price: placed, asked about, and taken off. | `Answered` as an order that rests, the same order when it is asked about, and `Canceled` when it is taken off, with nothing executed. |
| What an entry of an agent on Spot does, in the test environment: the least of `BTCUSDT` that orders can be for is bought at the market, a pair is placed for what the account holds of it, a tenth below and a tenth above the price, each of its orders is asked about, the pair is taken off, taken off once more, one of its orders is asked about again, and what was bought is sold at the market. | The purchase `Filled`, with its executions and their fees. The pair `Answered`, with both orders resting, and each found by its own identifier. Both `Canceled` when the pair is taken off, each under the number that it had. `NotFound` when it is taken off once more, and `Canceled` when one is asked about after that. The sale `Filled`. |
| What an entry of an agent on Futures does, in the test environment: how the account stands is read, `BTCUSDT` is given isolated margin and a leverage of 2, and given both once more, a little is bought at the market, the position is read with its margin, a stop-loss and a take-profit that only reduce the position are placed a tenth below and a tenth above the mark price, each is asked about, the take-profit is taken off, the position is closed at the market while the stop-loss rests, the stop-loss is asked about and taken off where it still rests, and the account is read again. | The account `Succeeded`, in one-way mode with one asset and no position in the symbol, or the test is skipped. The margin `Answered`, both times. The purchase `Filled`, at a price, after which the account holds a position, which is read with its margin: what was bought, its prices, and a liquidation price below the mark price. Both orders `Answered` and resting, each found by its own identifier. The take-profit `Canceled`, and the stop-loss resting still. The close `Filled`. The stop-loss known to Binance with nothing executed, whatever Binance made of it, which the run writes out. No position after that. |

| The account event feed of each market of the test environment: the account is followed, a stop of `BTCUSDT` is placed a tenth above the price, and taken off. | `Following`. A change of everything in the account as the feed begins, and a change of `BTCUSDT` when the stop is placed, and again when it is taken off, each within 20 seconds. |

The market data needs no credential. It is asked under the same condition all the same, so that no run reaches Binance that was not meant to.

The tests read the variables. The application reads none (NFR-CFG-2).

A key of the real exchange that works only from listed addresses is refused from any other, as a wrong one is. Allow the address of the machine at Binance for the run, or the check of the credential fails.

Run it when a call is added or changed, and when Binance changes its API. These things rest on documentation, or on less, until a run has shown them:

- That one credential of the test environment works at the addresses of both its markets. Binance's documentation of each sends the user to the same place for a key, and does not say so.
- That the test environment of Futures serves `/fapi/v1/accountConfig` and `/fapi/v3/balance`. Its documentation says that most calls work there.
- What Binance answers a wrong credential with. Its documentation lists `-1002`, `-1022`, `-2014`, and `-2015`, and describes no status 401. The error `-2008`, which its wallet API answers an unknown key with, is in no list of its errors.
- That the wallet API reports its weight as `X-SAPI-USED-IP-WEIGHT-1M` with an answer that succeeded.
- That Spot and Futures report theirs as `X-MBX-USED-WEIGHT-1M`, in both environments. Binance's documentation gives the name as a pattern, with the interval at its end.
- What Binance allows an address in a minute, in its test environment above all, and that it sends `Retry-After` with its signal on Futures. Neither is asked by a run: the limits are in what `exchangeInfo` answers, which the gateway reads for the symbols and not for the limits yet.
- That the test environment of Futures serves `/fapi/v3/positionRisk`, `/fapi/v1/marginType`, and `/fapi/v1/leverage`, and that its `/fapi/v1/accountConfig` says `multiAssetsMargin` beside `dualSidePosition`. An account that does not say both is not read, and gets no entry.
- That Binance answers a symbol which has isolated margin already with `-4046`, and the call that sets a leverage with that leverage. Another answer ends as `Rejected` or `Unknown`, and no entry is placed.
- That Binance takes a take-profit of Futures as a conditional order of the type `TAKE_PROFIT_MARKET`, with `workingType=MARK_PRICE` and `reduceOnly`, and what it answers one that would be triggered at once with. Its documentation of the call names `-2021` for another type of order, and its list of errors has `-4142` for a stop or a take-profit: either is a refusal.
- What becomes of a stop-loss or a take-profit that only reduces a position once the position is closed by another order. Binance's documentation does not say. What places the orders takes it off, and counts on nothing: an order that Binance has ended by then is asked about, and found over.
- That an order which only reduces a position is held to no least value. Binance's error for the least value says "unless you choose reduce only". It is what a part of an entry at a price is protected by, however small.
- That the test environment takes the calls of the orders: a stop of Spot as `STOP_LOSS`, and a stop of Futures as a conditional order, under `/fapi/v1/algoOrder`. Binance moved the stops of Futures there in December 2025, and its documentation of those calls names only two of the states that such an order can be in.
- What Binance answers when it is asked about an order that it does not have, above all about a conditional order of Futures. The gateway takes `-2013` and `-2011` for its word that there is none. Another answer ends as `Rejected`, which says nothing about the order: such an order would stay unknown, and its agent would get no cycle, until this is settled.
- What an order at the market answers with when it is executed on Futures, and what a stop of Futures says once it is triggered. The one order that a run has executed is of Spot.
- That the test environment of Spot takes an order at a price and the pair, and that Binance reports the orders of a pair that it takes off under `origClientOrderId`. Binance documents the calls for the real exchange, and says that its test environment has the same features. The run above shows them, and no run has been made yet.
- What Binance answers when it is asked to take off a pair that is over. The gateway takes `-2011` and `-2013` for its word that there is none, and what places the orders asks about both orders whatever the answer is.
- What becomes of the other order of a pair when one is triggered. Binance's documentation says that it ends, as cancelled "because the other order of the pair started working", and does not say which of its states that is. The gateway reads both `CANCELED` and `EXPIRED` as an order that is over, and what places the orders goes by neither. No run triggers an order.
- That the test environment of Futures serves `/fapi/v1/income`, and what a row of funding looks like there. Binance's documentation shows a row of another kind, and none of funding.
- When Binance writes what a position received or paid at a funding time, that the time of the row is the funding time itself, and that an answer of 1000 rows holds the oldest of the time that was asked for. What reads the funding of a position takes all three for granted: see "Funding" in [orders.md](orders.md). The run holds a row to a whole hour where the account has one, and settles the other two only for an account that held a position over a funding time.
- That `/fapi/v3/positionRisk` says `entryPrice`, `markPrice`, and `liquidationPrice` for a position of isolated margin, and writes a liquidation price of `0` where it names none. The run that buys on Futures reads the position that it opened.
- That the test environment of Futures serves its user data streams under `wss://demo-fstream.binance.com/private`, at `/ws/<key>`. Binance's documentation names that host as the address of the WebSockets of its test environment, and the path `/private` for the real exchange alone, where the addresses without it were taken out of service in April 2026. A feed that cannot be followed is a warning in the log, and the account is reconciled on the schedule.
- That Binance takes `userDataStream.subscribe.signature` with a key of the kind that a user creates by default, and with no login before it, in its test environment as on the real exchange. Its changelog of August 2025 says so, and the description of the request names no kind of key.
- Where the events of the feeds say their symbol. Binance's documentation shows `s` for `executionReport`, `listStatus`, and `TRADE_LITE`, and the object `o` of `ORDER_TRADE_UPDATE` and of `ALGO_UPDATE` and the positions `P` of `ACCOUNT_UPDATE` without their fields. An event that does not say its symbol where the gateway looks is taken as one about everything of the account, so what follows is a question more, and no change that is missed.
- What Binance counts a connection of a user data stream of Futures as, and how many it lets an address open. Its documentation says neither.
- That Binance begins the next candle as one closes, also when nothing trades. Its documentation does not say when. Where it begins a candle late, the candle before it is given late, and never early: see "Candles".

The calls of the market data were asked of Binance itself on 16 November 2026, on both markets, on the real exchange and in the test environment. Each ended as the table says. The run and what Binance answered beside it also showed what its documentation leaves open:

- The test environment serves every call of the market data, and `BTCUSDT` is a symbol of both its markets.
- The newest row of an answer of candles is the candle in progress.
- 499 rows of candles cost 2 of request weight on Futures, as on Spot.
- The whole list of the symbols of Spot is under 7 megabytes without the permission sets, and arrived in less than a second. The client of the gateway does not ask for it compressed.
- `/fapi/v2/ticker/price` answers that 1 was counted whatever was counted before it, which is why the limiter does not go by it: see "The log".

## Not built yet

- The operator status screen, which shows what `IExchangeLimits` and `IExchangeClock` say (FR-OPS-2).
- The limit on the orders of an account, which Binance counts for each account and not for the address. An order that Binance answers with its signal of too many ends as `NotSent`, as any call does. A protective order has no turn before other calls at the limiter either.
- The leverage brackets of a symbol of Futures, `/fapi/v1/leverageBracket`, by which Binance allows less leverage for a larger position. A leverage that Binance refuses ends as `Rejected`.
- The positions of an account in all its symbols, in one read. The margin of each open position is read with a call of its own, in its symbol. See "Not built yet" in [orders.md](orders.md).
- The list of three orders of Spot, in which Binance places a pair by itself when an entry at a price is executed. See "Not built yet" in [orders.md](orders.md).
- A page that asks for prices, and a data package that holds one. What has the Risk Guard check the plan of a cycle asks for the price that the symbol last traded at, and for the rules of its symbol, which it holds the plan to (FR-RSK-4): see "The facts" in [risk-guard.md](risk-guard.md). What starts the cycles asks for the candles of an agent's own timeframe through each close, and the data package of each cycle for the candles of the higher timeframes, and on Futures for the mark price, the funding rate, and the open interest (FR-CYC-2): see "The data package" in [cycles.md](cycles.md). An agent's timeframe is one of four, `Timeframe` in `Shared`: `CandleInterval` has a member of the same name for each, which `CandleIntervals.Of` gives, and the week as well, for context alone.
- A stream of prices. A price that a page follows is read once a second, which costs of the request budget, up to the half that the limiter leaves it: see "Following a price". Binance has market streams on a WebSocket, which cost no request weight.
- Reading the limits of "The limiter" from `exchangeInfo`, which the gateway now calls for the symbols.
- Listing the orders that rest in an account, and the positions of an account in all its symbols. What reconciles an agent asks about each order that has a row, by its identifier, and about the position of the account in the symbol of the agent (FR-BIN-7, FR-BIN-8): see "Reconciling" in [orders.md](orders.md).
- The account event feed for market data, and anything of an event but which symbol it is about. An event is a signal, and what changed is asked.
- Reading the prices of every symbol of a market with one call, where many are followed. Each price that is followed is a read of its own.
- A count of the connections that the account event feeds hold, for the operator status screen (FR-OPS-2).
- Noticing a connection of a feed that is dead without saying so before the network does. Binance asks every connection whether it is still there, and the gateway answers, but it does not ask Binance. Until the connection breaks, what changes in the account is found by the reconciliation that runs on a schedule.
- Breakers for repeated failures of Binance (NFR-REL-4). That a read of market data which failed is not made again for 5 seconds is none.
