# Exchange

Every call to Binance goes through the Exchange Gateway: one adapter behind a common interface (SRS 3.1, NFR-ARC-3). Binance is the only exchange. The gateway reaches its Spot market and its USDⓈ-margined perpetual Futures market, each on the real exchange and in Binance's test environment, with Binance's official public APIs, at the four addresses of the configuration file (FR-BIN-1, FR-BIN-2).

A call that needs a credential is signed with the user's own, for the environment that is asked. The gateway places orders, each with the identifier that its caller gave it, and says of each call whether there is an order, so that none is sent twice (FR-BIN-6). It has no operation that withdraws, deposits, or transfers funds (FR-BIN-9). Every call passes one limiter, for all users, because Binance counts its limits on requests against the address of the service (FR-BIN-4). The clock of the service is held against Binance's, and new entries pause while it is off (FR-BIN-5). Market data is read once and shared by all who need it, and is held in memory for a short time and nowhere else (FR-BIN-3, NFR-DAT-6). Every call is logged with its duration, its outcome, and the request weight that Binance reports (NFR-LOG-5).

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
}

public sealed record ExchangeTime(ExchangeCallOutcome Outcome, DateTimeOffset? ServerTime);

public sealed record ExchangeBalances(ExchangeCallOutcome Outcome, IReadOnlyList<ExchangeBalance> Balances);

public sealed record ExchangeBalance(string Asset, decimal Total, decimal Available);
```

| Rule | Detail |
|---|---|
| **A caller names a market and an environment, and nothing of Binance.** | `Market` is `Spot` or `Futures`, and `TradingMode` is `Demo` or `Live`. The adapter is private to the gateway, and so are the paths, the parameters, and the errors of Binance. What a call gives is a type of the gateway. |
| **Every operation of this interface reads.** | The clock of a market, what a credential may do, and what an account holds. The orders have an interface of their own: see "The orders" and "No funds are moved". |
| **What an account holds is asked before an entry on Spot.** | `AgentOrders` of the Decision Engine asks `GetBalancesAsync` for the asset that the symbol of an entry is priced in, and skips an entry that the account does not cover (FR-BIN-S3). See "The quote balance" in [orders.md](orders.md). |
| **The credential comes with the call.** | See "The credential". |
| **A call with a credential says whose it is.** | `userId` is the user that the credential belongs to. It is for the limiter, which gives the users whose calls wait their turns, and it is not sent to Binance. An empty one is no call. See "The limiter". |
| **A call is made once.** | The gateway does not call again after a failure. Whether a call is made again is its caller's to decide. |
| The check of a key answers with a `KeyVerdict`. | It is older than the outcomes below, and says the same: see "Validating a key" in [credentials.md](credentials.md). |

An operation ends with an error in two cases, neither of which is Binance's doing: the caller cancelled, or what it was given is no call, which is one without a key or without a user, or for a market or an environment that does not exist. Everything else is an outcome.

Candles, prices, and the rules of the symbols are not read through this interface, where a call is made once for whoever makes it. They have an interface of their own, `IExchangeMarketData`, behind which a read is shared: see "Market data".

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

These are all the calls that the gateway makes. Each is a `GET`, but for the calls that place an order or take one off the exchange, which are in the second table.

| What | Call | Signed | Weight |
|---|---|---|---|
| The clock of Spot | `/api/v3/time` | No | 1 |
| The clock of Futures | `/fapi/v1/time` | No | 1 |
| What a key may do, on the real exchange | `/sapi/v1/account/apiRestrictions`, at the address of Spot | Yes | 1 |
| The account of Spot: what it holds, and whether it may trade | `/api/v3/account?omitZeroBalances=true` | Yes | 20 |
| Whether the account of Futures may trade, in the test environment | `/fapi/v1/accountConfig` | Yes | 5 |
| What the wallet of Futures holds | `/fapi/v3/balance` | Yes | 5 |
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
| Places a stop on Futures | `POST /fapi/v1/algoOrder?algoType=CONDITIONAL` | 0 |
| What became of a stop of Futures | `GET /fapi/v1/algoOrder` | 1 |
| Takes a stop of Futures off the exchange | `DELETE /fapi/v1/algoOrder` | 1 |
| The executions of an order of Futures | `GET /fapi/v1/userTrades?symbol=…&orderId=…` | 5 |

| Rule | Detail |
|---|---|
| **The list is in one place.** | `Adapters/Binance/BinanceEndpoint.cs` has one value for each call: the market whose address serves it, its method, its path, its query, its weight, and the limit that Binance counts it against. Nothing outside that class can make one. |
| **Every parameter is in the query,** also of a call that is no `GET`. | Binance takes them there. No call has a body, and the log of outbound calls leaves the query out. |
| **Every call passes one place.** | `Adapters/Binance/BinanceCall.cs` makes the exchange: the limiter, the address, the signature, the time limit, and what the answer means. It takes a `BinanceEndpoint` and nothing else. `NoFundMovementTests` fails when another type of the gateway takes a client from the host's factory or sends with one. |
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
- **`Binance:LimitSharePercent` is the part of each limit that the service allows itself.** The rest is left for what the limiter cannot see.
- The numbers are in `BinanceLimit`, beside the weights, and are what Binance documents. Its documentation gives them as what `exchangeInfo` answers, and says nothing of its test environment, which is taken to count as the real exchange does.

| Rule | Detail |
|---|---|
| **Any sixty seconds carry at most the part.** | A fifth of what a minute allows may go at once, and the rest flows in evenly: at 70 percent, 336 of the Futures limit at once and 22.4 more in each second. So there is soon room again after a burst, and no minute is spent in its first second. Binance does not document whether its minute is one of the clock or the last sixty seconds, and this holds for both. |
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
| **There are three kinds of order.** | `Market` is executed at once. `Stop` rests until the price reaches its trigger, and is then placed at the market: `STOP_LOSS` on Spot, and `STOP_MARKET` on Futures. `Limit` is executed at its price or a better one, and what is not executed at once rests until it is, or until it is taken off: `LIMIT`, good until it is taken off, on both markets (FR-BIN-S2). `Price` of a request is the trigger of a stop, or the price of a limit order. |
| **The pair of Spot is one list of two orders.** | A take-profit above the price and a stop-loss below it, which both sell the same quantity. Each is a stop: it is placed at the market when the price reaches its trigger, `TAKE_PROFIT` and `STOP_LOSS`, and Binance ends the other then (FR-BIN-S2). Binance calls the list an OCO. `PlacePairAsync` places it with one call, and Binance takes both orders or neither. It takes no market: only Spot has such a pair, as only Futures has a mark price. |
| **A pair carries three identifiers.** | One for each order, and one for the pair, which Binance keeps apart from those of the orders: `belowClientOrderId`, `aboveClientOrderId`, and `listClientOrderId`. A pair whose three are not different, or not what Binance takes, is an error of the caller, and nothing is sent. |
| **Each order of a pair is an order like any other.** | It is asked about with `FindAsync`, by its own identifier. `CancelPairAsync` takes both off with one call, by the identifier of the pair, and Binance reports each under the identifier that it carried: `origClientOrderId`, where `clientOrderId` is the identifier of the call that took it off. An answer that does not say both orders does not read. |
| **Binance takes a pair while the price is between its two triggers.** | It refuses one whose stop-loss is at or above the price, or whose take-profit is at or below it, as it refuses an order that would be triggered at once. That is a refusal: there is neither order. |
| **A stop of Futures is a conditional order.** | Binance takes it with calls of its own, under `/fapi/v1/algoOrder`, and answers one at the call of the other orders with its error `-4120`. It gives it an id of its own until it is triggered. Then it becomes an order, and `ExchangeOrderId` is the number of that order, which its executions are read with. |
| **An order that only closes is sent to Futures as one that only reduces.** | `Closes` is `reduceOnly=true` there, so that the order opens nothing when the position is gone already. Spot has no positions: a sale there sells what the account holds. So what sells on Spot sells the quantity that its agent bought, and no more: see "The stop-loss" in [orders.md](orders.md). |
| **The answer says what became of the order.** | An order at the market, and one at a price, is asked for with `newOrderRespType=FULL` on Spot, which has its executions with their fees, and `RESULT` on Futures. A stop and a pair are asked for with `RESULT`: nothing of them is executed when they are placed. |
| **A call is made once.** | Nothing here places an order again. |
| **A call that changes something cannot be cancelled once it is sent.** | Cancelling ends the wait at the limiter, with the error of a cancelled call, and then nothing was sent. From the moment that the call is sent it runs to its answer or to its time limit, whatever its caller does: a call that was dropped half way would leave nobody knowing whether Binance took the order. |

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
- It reads the whole account. What is free of the asset that a symbol is priced in is what an entry on Spot is held to, where the order is placed (FR-BIN-S3): see "The quote balance" in [orders.md](orders.md). The margin of Futures is not asked yet (FR-BIN-F6).

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
| **Nothing is read that nobody asked for.** | There is no job that reads ahead. The gateway may use no other component, so it does not know which agents run, and whatever starts a cycle asks: the job of the Decision Engine does, at each close, for the symbols and timeframes of the agents that run then. Each cycle that starts then asks for the rest of its data package: the candles of the higher timeframes, and on Futures the mark price and the open interest. See [cycles.md](cycles.md). |
| **A read is the service's own call.** | It needs no credential, and takes the turn of the service at the limiter, in one line with the reads of the clock: see "The limiter". It is in the log of outbound calls once, however many share it. |
| **Each market has data of its own in each environment.** | A read goes to the address of its market in its environment, as every call does. An agent in the test environment decides on the candles, the prices, and the rules of the environment that its orders go to. |
| **Only the symbols of `Binance:Symbols` are served.** | Any other symbol is no call: it ends with an error, and nothing is sent. So what is held has a bound, and no text of a caller reaches Binance. |
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
| **Candles are whole or they are none.** | A row that does not read, and rows that are not in the order of their time, make the answer `Failed`. Candles that Binance does not have, during a halt, are a gap between two rows, and no failure. |

### Symbols

`GetSymbolsAsync` gives the symbols of the configuration file that a market has, in the order of the file, each with the rules that an order in it must keep.

```csharp
public sealed record ExchangeSymbol(
    string Symbol, string BaseAsset, string QuoteAsset, bool Trading,
    decimal PriceStep, decimal QuantityStep, decimal MinQuantity, decimal MaxQuantity, decimal MinValue,
    bool StopOrders, bool PairOrders);
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

- **A symbol that is not among them is one that the market does not have,** which is what filters the symbols that an agent can be given (FR-AGT-6): Agents asks it for the form of an agent and when a market or a symbol is saved. See "The symbols" in [agents.md](agents.md). The steps, the least quantity, and the least value are what an order is held to before it is placed (FR-RSK-4), and the quantity step and the most quantity what the size of an order is held to as the Risk Guard computes it (FR-RSK-5): see "The size of an order" in [risk-guard.md](risk-guard.md).
- **One call reads every symbol of a market.** Futures has no call for one symbol, and Spot answers a list with a symbol that it lacks with an error for the whole list. The answer of Spot is close to 7 megabytes, once an hour: it is asked without the permission sets of each symbol, with which it is more than twice that.
- **A list is whole or it is none.** A symbol of the file without one of its rules, or that is there twice, makes the answer `Failed`: a list without it would say that the market lacks it.
- **A symbol that takes no stop gets no entry.** `StopOrders` says whether Binance takes a stop in the symbol that is placed at the market when it is triggered, which is what protects a position (FR-RSK-7). A symbol that names no types of order takes none. See "When no entry is placed" in [orders.md](orders.md).
- **A symbol of Spot that takes no pair gets no entry either.** `PairOrders` says whether Binance takes the pair of a stop-loss and a take-profit in the symbol (FR-BIN-S2). A symbol that does not say takes none. On 18 November 2026 both symbols of the tracked file took it, on the real exchange and in the test environment.
- The rules of a market order, `MARKET_LOT_SIZE`, are not read.

### Nothing is stored

The database is not a market-data warehouse (NFR-DAT-6). The gateway keeps what it read in memory, for this process, as the limiter counts and the watch on the clock watches, and writes none of it anywhere. There is no table for it: `SingleStoreTests` holds the database to the tables of the model, by name, and `CandleCacheTests` reads everything, over the close of a candle, and finds every row of every table as it was. What a cycle decided on is for the cycle to record, as a summary (FR-CYC-10).

## No funds are moved

Ecnanib never withdraws, deposits, or transfers funds, between the Spot and the Futures wallet of a user included (FR-BIN-9). A key that could withdraw is not even held: see "Binance permissions" in [credentials.md](credentials.md).

| Rule | Detail |
|---|---|
| **The gateway has no such operation.** | `IExchangeAdapter` has the three above, and each reads. `IExchangeMarketData` has five, and each reads, with a call of Binance's public API, which takes no credential and so can move nothing. `IExchangeOrders` has six: an order, and a pair of them, buys or sells in the account that the credential is of, and moves nothing out of it, into it, or between its wallets. |
| **It can send Binance nothing but the calls of the list.** | See "The calls". `NoFundMovementTests` asks the gateway for everything it does, in each market and each environment, and holds what was sent to the two lists. It fails for an operation of any of the three interfaces that it does not ask, so an operation that is added is held to the lists too. |
| **Binance keeps the calls that move funds apart.** | Withdrawals, the addresses of deposits, and transfers between wallets are in its wallet API, under `/sapi`. The gateway makes one call there, which reads what a key may do. |
| A call about an order names one order, and nothing to move. | Each call that is no `GET` carries the identifier of its order, or of its pair. None carries an amount of an asset to send, or an address to send it to. |

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

The variables are `ECNANIB_LIVE_BINANCE_<ENVIRONMENT>_KEY` and `ECNANIB_LIVE_BINANCE_<ENVIRONMENT>_SECRET`, with `DEMO` or `LIVE`. Give them for one environment or for both. For each that has both, these calls are made, at the addresses of the tracked configuration file. Every one of them reads, and costs request weight only, but for the last three, which place orders in the test environment. No order is placed on the real exchange. Two of the three place orders far from the price, and take them off again. The last buys a little at the market on Spot and sells it again: it is the one that is executed, with the funds of the test environment, which are none, and it is skipped when the account there does not cover it:

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
| A stop of `BTCUSDT` on each market of the test environment, which buys a tenth above the price: asked about before it is placed, placed, asked about, taken off, taken off once more, and asked about again. | `NotFound`, then `Answered` as an order that rests, the same order when it is asked about, `Canceled` when it is taken off, `NotFound` when it is taken off once more, and `Canceled` when it is asked about after that. |
| An order at a price of `BTCUSDT` on Spot of the test environment, which buys a tenth below the price: placed, asked about, and taken off. | `Answered` as an order that rests, the same order when it is asked about, and `Canceled` when it is taken off, with nothing executed. |
| What an entry of an agent on Spot does, in the test environment: the least of `BTCUSDT` that orders can be for is bought at the market, a pair is placed for what the account holds of it, a tenth below and a tenth above the price, each of its orders is asked about, the pair is taken off, taken off once more, one of its orders is asked about again, and what was bought is sold at the market. | The purchase `Filled`, with its executions and their fees. The pair `Answered`, with both orders resting, and each found by its own identifier. Both `Canceled` when the pair is taken off, each under the number that it had. `NotFound` when it is taken off once more, and `Canceled` when one is asked about after that. The sale `Filled`. |

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
- That the test environment takes the calls of the orders: a stop of Spot as `STOP_LOSS`, and a stop of Futures as a conditional order, under `/fapi/v1/algoOrder`. Binance moved the stops of Futures there in December 2025, and its documentation of those calls names only two of the states that such an order can be in.
- What Binance answers when it is asked about an order that it does not have, above all about a conditional order of Futures. The gateway takes `-2013` and `-2011` for its word that there is none. Another answer ends as `Rejected`, which says nothing about the order: such an order would stay unknown, and its agent would get no cycle, until this is settled.
- What an order at the market answers with when it is executed on Futures, and what a stop of Futures says once it is triggered. The one order that a run has executed is of Spot.
- That the test environment of Spot takes an order at a price and the pair, and that Binance reports the orders of a pair that it takes off under `origClientOrderId`. Binance documents the calls for the real exchange, and says that its test environment has the same features. The run above shows them, and no run has been made yet.
- What Binance answers when it is asked to take off a pair that is over. The gateway takes `-2011` and `-2013` for its word that there is none, and what places the orders asks about both orders whatever the answer is.
- What becomes of the other order of a pair when one is triggered. Binance's documentation says that it ends, as cancelled "because the other order of the pair started working", and does not say which of its states that is. The gateway reads both `CANCELED` and `EXPIRED` as an order that is over, and what places the orders goes by neither. No run triggers an order.
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
- The orders that only Futures has: an order for the take-profit, and the margin, the leverage, and the trigger on the mark price (FR-BIN-F2, FR-BIN-F3). The gateway places an order at a price there, and nothing uses it yet. See "Not built yet" in [orders.md](orders.md).
- The list of three orders of Spot, in which Binance places a pair by itself when an entry at a price is executed. See "Not built yet" in [orders.md](orders.md).
- A page that asks for prices, and a data package that holds one. What has the Risk Guard check the plan of a cycle asks for the price that the symbol last traded at, and for the rules of its symbol, which it holds the plan to (FR-RSK-4): see "The facts" in [risk-guard.md](risk-guard.md). What starts the cycles asks for the candles of an agent's own timeframe through each close, and the data package of each cycle for the candles of the higher timeframes, and on Futures for the mark price, the funding rate, and the open interest (FR-CYC-2): see "The data package" in [cycles.md](cycles.md). An agent's timeframe is one of four, `Timeframe` in `Shared`: `CandleInterval` has a member of the same name for each, which `CandleIntervals.Of` gives, and the week as well, for context alone.
- Prices that change on a screen within two seconds (FR-NTF-2). A price is read when it is asked for, and counts for a second: a screen that asked every second for every symbol would spend much of the budget.
- Reading the limits of "The limiter" from `exchangeInfo`, which the gateway now calls for the symbols.
- The feed of account events, and reconciliation (FR-BIN-7, FR-BIN-8).
- Breakers for repeated failures of Binance (NFR-REL-4). That a read of market data which failed is not made again for 5 seconds is none.
