using System.Collections.Concurrent;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.ExchangeGateway;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>What builds a data package writes to the log, and the area it writes under.</summary>
internal static class DataPackagesLog
{
    public const string Area = "Ecnanib.DecisionEngine.DataPackages";

    public const string Built =
        "The data package of agent {AgentId} for the close at {ClosedAt} was built in {DurationMs} ms: {CandleCount} candles of " +
        "{Timeframe}, {ContextCandleCount} of {ContextTimeframes}, and {OrderCount} open orders";
}

/// <summary>
/// The application as the tests of the data package ask it: with what builds a package, on a
/// clock that the test sets, with an exchange of the test's that has all the market data, and
/// with a stand-in for what keeps positions and orders, where the test has one.
/// </summary>
internal sealed class PackagesApplication : IDisposable
{
    /// <param name="now">What the clock shows: the exchange has closed every candle that has closed by then.</param>
    /// <param name="holdings">What keeps positions and orders, when the test has something that does. Nothing does yet.</param>
    /// <param name="candles">How many candles of the agent's timeframe a package holds, when it is not what the file of the tests says.</param>
    /// <param name="contextCandles">How many candles of each higher timeframe, when it is not what the file of the tests says.</param>
    public PackagesApplication(DateTimeOffset now, StandInHoldings? holdings = null, int? candles = null, int? contextCandles = null)
    {
        Clock = new TestClock(now);
        Market = new StandInMarket(Clock);

        Application = new AccountsApplication(
            clock: Clock,
            settings: file =>
            {
                file = candles is { } own ? file.With("Trading:DataPackage:Candles", own) : file;

                return contextCandles is { } higher ? file.With("Trading:DataPackage:ContextCandles", higher) : file;
            },
            host: builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton<IExchangeMarketData>(Market);

                if (holdings is not null)
                {
                    services.AddSingleton<IAgentHoldings>(holdings);
                }
            }));
    }

    public AccountsApplication Application { get; }

    public TestClock Clock { get; }

    /// <summary>What stands for the market data of the exchange.</summary>
    public StandInMarket Market { get; }

    /// <summary>
    /// Builds the data package of a cycle of an agent for a close, as the cycle does: with the
    /// candles of the agent's own timeframe through the close, which what starts the cycles read.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="closedAt">The close that the cycle is for.</param>
    /// <param name="own">How many closed candles of the agent's timeframe the exchange gave.</param>
    public Task<DataPackage> BuildAsync(Agent agent, DateTimeOffset closedAt, int own = StandInMarket.Given) =>
        Application.DataPackages.BuildAsync(CycleOf(agent, closedAt, own), TestContext.Current.CancellationToken);

    /// <summary>A cycle of an agent for a close, as what starts the cycles hands it over.</summary>
    public static CycleStart CycleOf(Agent agent, DateTimeOffset closedAt, int own = StandInMarket.Given) =>
        new(
            Guid.CreateVersion7(),
            agent,
            Roles: [],
            closedAt,
            StandInMarket.Through(closedAt, CandleIntervals.Of(agent.Timeframe), own));

    public void Dispose() => Application.Dispose();
}

/// <summary>Agents as a cycle is handed them, which need no rows.</summary>
internal static class PackageAgents
{
    private static readonly DateTimeOffset Created = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>An agent that runs, with a name that says whose it is, and with settings that say every limit.</summary>
    public static Agent On(
        Market market,
        Timeframe timeframe,
        TradingMode mode = TradingMode.Demo,
        string symbol = "BTCUSDT",
        decimal allocatedCapital = 1000m) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "Ada Lovelace's swing agent",
            mode,
            market,
            symbol,
            quoteAsset: "USDT",
            timeframe,
            allocatedCapital,
            riskPerTradePercent: 1.5m,
            minRewardToRisk: 1.75m,
            leverageCap: 4,
            liquidationBuffer: 2.5m,
            dailyLossPercent: 5m,
            drawdownPercent: 20m,
            maxEntryDistancePercent: 0.8m,
            AgentState.Running,
            AgentStateCause.UserStarted,
            stateChangedAt: Created,
            createdAt: Created);
}

/// <summary>
/// Stands for the market data of the exchange where a data package is built from it: candles of
/// every length, the week included, and on Futures the mark price with its funding, and the open
/// interest. Unless a test says otherwise, the exchange has closed every candle that has closed
/// by the clock of the test, and every read succeeds. It keeps what it was asked.
/// </summary>
internal sealed class StandInMarket(TimeProvider clock) : IExchangeMarketData
{
    /// <summary>How many closed candles the exchange gives of a length, unless the test says otherwise.</summary>
    public const int Given = 150;

    /// <summary>The mark price that the exchange says, unless the test says otherwise.</summary>
    public const decimal MarkPrice = 61888.1m;

    /// <summary>The funding rate that the exchange says: 0.01 percent.</summary>
    public const decimal FundingRate = 0.0001m;

    /// <summary>The open interest that the exchange says.</summary>
    public const decimal OpenInterest = 82450.5m;

    private readonly ConcurrentQueue<AskedCandles> _askedCandles = new();
    private readonly ConcurrentQueue<AskedOfFutures> _askedOfFutures = new();

    /// <summary>What the exchange answers an ask for candles with, when it is not the candles through the latest close: null is those.</summary>
    public Func<AskedCandles, ExchangeCandles?>? Candles { get; set; }

    /// <summary>How reading the mark price ends, when it does not succeed.</summary>
    public ExchangeCallOutcome MarkPriceOutcome { get; set; } = ExchangeCallOutcome.Succeeded;

    /// <summary>How reading the open interest ends, when it does not succeed.</summary>
    public ExchangeCallOutcome OpenInterestOutcome { get; set; } = ExchangeCallOutcome.Succeeded;

    /// <summary>The asks for candles, in order.</summary>
    public IReadOnlyList<AskedCandles> Asked => [.. _askedCandles];

    /// <summary>The asks for what Futures adds, in order.</summary>
    public IReadOnlyList<AskedOfFutures> AskedFutures => [.. _askedOfFutures];

    /// <summary>When funding is next paid on the exchange: at the next multiple of eight hours.</summary>
    public DateTimeOffset NextFundingAt => LatestClose(clock.GetUtcNow(), TimeSpan.FromHours(8)).AddHours(8);

    /// <summary>
    /// A reading that succeeded: closed candles of a length, oldest first, of which the newest
    /// closed at the latest close of that length at or before a time. Each candle has prices and
    /// a volume of its own, which are the same in whatever reading it is.
    /// </summary>
    public static ExchangeCandles Through(DateTimeOffset at, CandleInterval interval, int candles = Given)
    {
        var length = BinanceCalls.LengthOf(interval);
        var through = LatestClose(at, length);

        return new ExchangeCandles(
            ExchangeCallOutcome.Succeeded,
            [.. Enumerable.Range(0, candles).Select(candle => CandleOf(through - (length * (candles - candle)), length))],
            at);
    }

    /// <summary>A reading that did not succeed.</summary>
    public static ExchangeCandles NoCandles(ExchangeCallOutcome outcome) => new(outcome, [], null);

    /// <summary>
    /// The latest close of a candle of a length at or before a time. The first day that ticks
    /// count from was a Monday, so this holds for a week too, which the exchange begins on one.
    /// </summary>
    public static DateTimeOffset LatestClose(DateTimeOffset at, TimeSpan length) =>
        new(at.UtcTicks - (at.UtcTicks % length.Ticks), TimeSpan.Zero);

    /// <summary>The latest close of a candle of a length of the exchange at or before a time.</summary>
    public static DateTimeOffset LatestClose(DateTimeOffset at, CandleInterval interval) =>
        LatestClose(at, BinanceCalls.LengthOf(interval));

    public Task<ExchangeCandles> GetCandlesAsync(
        Market market, TradingMode mode, string symbol, CandleInterval interval, CancellationToken cancellationToken)
    {
        var asked = new AskedCandles(market, mode, symbol, interval);
        _askedCandles.Enqueue(asked);

        return Task.FromResult(Candles?.Invoke(asked) ?? Through(clock.GetUtcNow(), interval));
    }

    public Task<ExchangeMarkPrice> GetMarkPriceAsync(TradingMode mode, string symbol, CancellationToken cancellationToken)
    {
        _askedOfFutures.Enqueue(new AskedOfFutures("mark price", mode, symbol));

        return Task.FromResult(MarkPriceOutcome == ExchangeCallOutcome.Succeeded
            ? new ExchangeMarkPrice(MarkPriceOutcome, MarkPrice, FundingRate, NextFundingAt, clock.GetUtcNow())
            : new ExchangeMarkPrice(MarkPriceOutcome, null, null, null, null));
    }

    public Task<ExchangeOpenInterest> GetOpenInterestAsync(TradingMode mode, string symbol, CancellationToken cancellationToken)
    {
        _askedOfFutures.Enqueue(new AskedOfFutures("open interest", mode, symbol));

        return Task.FromResult(OpenInterestOutcome == ExchangeCallOutcome.Succeeded
            ? new ExchangeOpenInterest(OpenInterestOutcome, OpenInterest, clock.GetUtcNow())
            : new ExchangeOpenInterest(OpenInterestOutcome, null, null));
    }

    public Task<ExchangePrice> GetPriceAsync(Market market, TradingMode mode, string symbol, CancellationToken cancellationToken) =>
        throw new NotSupportedException("What builds a data package asks for no price.");

    public Task<ExchangeSymbols> GetSymbolsAsync(Market market, TradingMode mode, CancellationToken cancellationToken) =>
        throw new NotSupportedException("What builds a data package asks for no symbols.");

    /// <summary>A candle that began at a moment, with prices and a volume that go by when it began.</summary>
    private static ExchangeCandle CandleOf(DateTimeOffset opened, TimeSpan length)
    {
        // The quarter hours since the first, so that no two candles of a length are alike.
        var own = opened.ToUnixTimeSeconds() / 900;
        var close = 60000m + (own % 97 * 12.5m) - (own % 13 * 30.25m);
        var open = close - 5m + (own % 3);

        return new ExchangeCandle(
            opened,
            opened + length,
            open,
            High: Math.Max(open, close) + 10m + (own % 7),
            Low: Math.Min(open, close) - 10m - (own % 5),
            close,
            Volume: 100.125m + (own % 17));
    }
}

/// <summary>One ask for what Futures adds: the mark price or the open interest of a symbol, in an environment.</summary>
internal sealed record AskedOfFutures(string What, TradingMode Mode, string Symbol);

/// <summary>
/// Stands for what keeps positions and orders, which is not built: it says of every agent what
/// the test gave it, and keeps which agents it was asked about.
/// </summary>
internal sealed class StandInHoldings(AgentHoldings holds) : IAgentHoldings
{
    private readonly ConcurrentQueue<Guid> _asked = new();

    /// <summary>The agents that it was asked about, in order.</summary>
    public IReadOnlyList<Guid> Asked => [.. _asked];

    public Task<AgentHoldings> OfAsync(Agent agent, CancellationToken cancellationToken)
    {
        _asked.Enqueue(agent.Id);

        return Task.FromResult(holds);
    }
}
