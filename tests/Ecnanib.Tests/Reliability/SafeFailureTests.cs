using System.Net;
using Ecnanib.Agents;
using Ecnanib.AiProviderGateway;
using Ecnanib.Credentials;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Persistence;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Ecnanib.Tests.DecisionEngine;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.RiskGuard;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Reliability;

/// <summary>
/// Failures are safe: any internal error, timeout, or dependency outage results in no new entry
/// (NFR-REL-2). Each test here has one thing fail on the way from the close of a candle to the
/// entry, and none of them ends with an entry: a cycle that is missing anything decides nothing,
/// a plan that cannot be checked is rejected, and an entry that cannot be covered, protected, or
/// written down is not sent. What each part does with a failure is in the tests of that part.
/// See "Safe failure" in docs/reliability.md.
/// </summary>
public class SafeFailureTests
{
    private const string NotEntered = "No entry was placed for agent {AgentId} for the close at {ClosedAt}: {NoEntryReason}";

    /// <summary>A quarter hour in the middle of a week, which closes a candle of 15 minutes and of no other length.</summary>
    private static readonly DateTimeOffset Quarter = new(2026, 11, 18, 10, 15, 0, TimeSpan.Zero);

    // The stale limit of 15m in the file of the tests, in seconds.
    private const int StaleSeconds = 100;

    [Theory]
    [InlineData(Market.Spot)]
    [InlineData(Market.Futures)]
    public async Task With_nothing_failing_the_plan_of_the_team_is_handed_on_to_be_placed(Market market)
    {
        // What every test below takes something away from: a cycle that has all it needs
        // decides to open a position, and its plan goes on to what places the orders.
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var agent = application.Agents!.Give(Guid.NewGuid(), market: market);

        await application.RunAsync(Quarter).Within();

        Assert.Equal(CycleOutcome.Approved, application.Records.Of(agent).Cycle.Outcome);
        Assert.Equal(agent, Assert.Single(application.Orders.Placed).AgentId);
    }

    public static TheoryData<string> WhatACycleCanBeMissing =>
    [
        "the candle of the close",
        "the candles of a higher timeframe",
        "the mark price",
        "the open interest",
        "the equity of the agent",
        "the key of the user",
        "a key that can be read",
        "the price that an entry is held to",
        "the rules of the symbol",
        "a decision in time",
        "whether a global halt is on",
    ];

    [Theory]
    [MemberData(nameof(WhatACycleCanBeMissing))]
    public async Task A_cycle_that_is_missing_something_places_no_entry(string missing)
    {
        // The candle of the close is waited for for half a minute by the clock, so the cycle
        // that never gets it starts after that, and the others at the close.
        var now = missing == "the candle of the close" ? Quarter.AddSeconds(31) : Quarter;
        var holdings = missing == "the equity of the agent"
            ? new StandInHoldings(new AgentHoldings(1000m, Position: null, Orders: [], EquityAtDayStart: 1000m, PeakEquity: 1000m, Valued: false))
            : null;

        using var application = new CyclesApplication(
            now,
            buildsPackages: true,
            holdings: holdings,
            further: missing == "whether a global halt is on" ? services => services.AddSingleton<IGlobalHalt, HaltThatCannotBeRead>() : null);
        var market = application.Market!;

        // The mark price and the open interest are of the Futures market alone.
        var agent = application.Agents!.Give(
            Guid.NewGuid(), market: missing is "the mark price" or "the open interest" ? Market.Futures : Market.Spot);

        switch (missing)
        {
            case "the candle of the close":
                market.Candles = asked => asked.Interval == CandleInterval.FifteenMinutes ? StandInMarket.NoCandles(ExchangeCallOutcome.TimedOut) : null;
                break;

            case "the candles of a higher timeframe":
                market.Candles = asked => asked.Interval == CandleInterval.OneHour ? StandInMarket.NoCandles(ExchangeCallOutcome.Failed) : null;
                break;

            case "the mark price":
                market.MarkPriceOutcome = ExchangeCallOutcome.TimedOut;
                break;

            case "the open interest":
                market.OpenInterestOutcome = ExchangeCallOutcome.Failed;
                break;

            case "the key of the user":
                application.Team!.Keys.Outcome = (_, _) => KeyHandOverOutcome.NoKey;
                break;

            case "a key that can be read":
                application.Team!.Keys.Outcome = (_, _) => KeyHandOverOutcome.Unreadable;
                break;

            case "the price that an entry is held to":
                market.PriceOutcome = ExchangeCallOutcome.TimedOut;
                break;

            case "the rules of the symbol":
                market.SymbolsOutcome = ExchangeCallOutcome.Failed;
                break;

            case "a decision in time":
                // The team has decided when the price is read for the Risk Guard: by then the
                // stale limit of the timeframe is over, as after providers that were slow.
                market.AsThePriceIsRead = () => application.Clock.Set(Quarter.AddSeconds(StaleSeconds + 1));
                break;
        }

        await application.RunAsync(Quarter).Within();

        // Nothing was handed on to be placed, whatever the cycle is recorded as: one that never
        // started has no record, one that could not go on failed, and one whose plan was held
        // to something that is not known had it rejected.
        Assert.Empty(application.Orders.Placed);
        Assert.DoesNotContain(application.Records.Stored, stored => stored.Cycle.AgentId == agent && stored.Cycle.NoEntry is not null);
    }

    // Every way that a call of a role ends without a result, and an error that leaves the gateway.
    private static readonly string[] WaysToGiveNoResult =
        ["fails", "times out twice", "answers nothing that can be used twice", "is refused its key", "has no such model", "is paused for its provider", "is paused for its key", "throws"];

    public static TheoryData<Role, string> RolesThatGiveNoResult => new MatrixTheoryData<Role, string>(
        [Role.MarketAnalyst, Role.Strategist, Role.RiskManager], WaysToGiveNoResult);

    [Theory]
    [MemberData(nameof(RolesThatGiveNoResult))]
    public async Task A_cycle_whose_role_gives_no_result_places_no_entry(Role role, string how)
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var agent = application.Agents!.Give(Guid.NewGuid());

        Arrange(application.Models, role, how);

        await application.RunAsync(Quarter).Within();

        // The cycle ended with no decision, or failed when the error was none that the gateway
        // names: either way it handed nothing on, and the cycle of the next close starts anew.
        Assert.Empty(application.Orders.Placed);
        Assert.Equal(how == "throws" ? CycleOutcome.Failed : CycleOutcome.NoDecision, application.Records.Of(agent).Cycle.Outcome);
    }

    [Theory]
    [InlineData("fails")]
    [InlineData("times out twice")]
    [InlineData("is paused for its provider")]
    public async Task Only_the_Sentiment_Analyst_may_be_missing_and_its_report_is_then_marked_unavailable(string how)
    {
        // The one failure after which a cycle goes on, as the specification says of this role
        // alone (FR-TEAM-9): the Strategist is told that there is no sentiment, and gives it no
        // weight. The plan is then checked and placed as any other, within the same hard limits.
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var agent = application.Agents!.Give(Guid.NewGuid());

        Arrange(application.Models, Role.SentimentAnalyst, how);

        await application.RunAsync(Quarter).Within();

        Assert.Equal(CycleOutcome.Approved, application.Records.Of(agent).Cycle.Outcome);
        Assert.Single(application.Orders.Placed);
        Assert.Single(application.Written(TeamLog.Area, TeamLog.SentimentUnavailable));
    }

    public static TheoryData<Market, string> WhatAnEntryCanBeMissing => new()
    {
        { Market.Spot, "the rules of the symbol" },
        { Market.Spot, "what the account holds" },
        { Market.Spot, "an exchange that answers" },
        { Market.Spot, "a clock that was read" },
        { Market.Spot, "a database that takes the order" },
        { Market.Futures, "the rules of the symbol" },
        { Market.Futures, "how the account is set" },
        { Market.Futures, "what the account holds" },
        { Market.Futures, "the margin of the symbol" },
        { Market.Futures, "the leverage of the symbol" },
        { Market.Futures, "an exchange that answers" },
        { Market.Futures, "a clock that was read" },
        { Market.Futures, "a database that takes the order" },
    };

    [Theory]
    [MemberData(nameof(WhatAnEntryCanBeMissing))]
    public async Task A_plan_that_the_Risk_Guard_accepted_is_not_entered_when_what_places_it_is_missing_something(Market market, string missing)
    {
        // What places the orders, as the application has it, on a Binance of the test's.
        using var application = new OrdersApplication(
            further: missing == "a database that takes the order"
                ? services =>
                {
                    var real = services.Last(service => service.ServiceType == typeof(IAgentTrading)).ImplementationType!;

                    services.AddSingleton<IAgentTrading>(provider =>
                        new TradingWithoutItsDatabase((IAgentTrading)ActivatorUtilities.CreateInstance(provider, real)));
                }
                : null);
        var agent = await application.GiveAgentAsync(market: market);
        var plan = market == Market.Spot ? GuardCases.OpenLong : GuardCases.OpenLongOnFutures;

        switch (missing)
        {
            case "the rules of the symbol":
                application.Binance.Instead = request => request.RequestUri!.AbsolutePath.EndsWith("/exchangeInfo", StringComparison.Ordinal)
                    ? throw new HttpRequestException("The connection was refused.")
                    : null;
                break;

            case "how the account is set":
                application.Application.Providers.FuturesAccount = (_, _) => ExchangeFuturesAccount.Without(ExchangeCallOutcome.TimedOut);
                break;

            case "what the account holds":
                application.Application.Providers.Balances = (_, _) => new ExchangeBalances(ExchangeCallOutcome.Failed, []);
                break;

            case "the margin of the symbol":
                application.Binance.Instead = request => request.RequestUri!.AbsolutePath == "/fapi/v1/marginType"
                    ? StandInSystem.Status(HttpStatusCode.ServiceUnavailable)
                    : null;
                break;

            case "the leverage of the symbol":
                application.Binance.Instead = request => request.RequestUri!.AbsolutePath == "/fapi/v1/leverage"
                    ? throw new TaskCanceledException("Binance took too long.")
                    : null;
                break;

            case "an exchange that answers":
                application.Outages.Out = true;
                break;

            case "a clock that was read":
                application.Entries.Paused = true;
                break;
        }

        if (missing == "a database that takes the order")
        {
            // The error ends what places the orders, as it ends the cycle: with nothing sent.
            await Assert.ThrowsAsync<InvalidOperationException>(() => application.PlaceAsync(agent, plan));
        }
        else
        {
            await application.PlaceAsync(agent, plan);

            // And it says why, for the record of the cycle.
            Assert.Single(application.Application.Events(), said => said.Template == NotEntered);
        }

        // No entry was sent to Binance, and none is written that something could send later.
        Assert.Empty(application.Binance.Asked);
        Assert.Empty(await application.OrdersOfAsync(agent.Id));
        Assert.Empty(await application.PositionsOfAsync(agent.Id));
    }

    /// <summary>Has the next calls of a role end without a result, in one of the ways that a call does.</summary>
    private static void Arrange(StandInModels models, Role role, string how)
    {
        AiCallResult Paused(AiCallOutcome outcome) => new(outcome, null, null, TimeSpan.Zero);

        switch (how)
        {
            case "fails":
                models.Says(role, StandInModels.Failed);
                break;

            case "times out twice":
                models.Says(role, StandInModels.TimedOut, StandInModels.TimedOut);
                break;

            case "answers nothing that can be used twice":
                models.Says(role, StandInModels.Invalid, StandInModels.Invalid);
                break;

            case "is refused its key":
                models.Says(role, StandInModels.Refused);
                break;

            case "has no such model":
                models.Says(role, StandInModels.NotFound);
                break;

            case "is paused for its provider":
                models.Says(role, Paused(AiCallOutcome.ProviderPaused));
                break;

            case "is paused for its key":
                models.Says(role, Paused(AiCallOutcome.KeyPaused));
                break;

            case "throws":
                models.Says(role, _ => throw new InvalidOperationException("Something that nothing expected."));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(how), how, null);
        }
    }

    /// <summary>Stands for a global halt that cannot be read, as when the database does not answer.</summary>
    private sealed class HaltThatCannotBeRead : IGlobalHalt
    {
        public Task<bool> IsOnAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Whether the global halt is on could not be read.");
    }

    /// <summary>
    /// Stands for Agents where the database fails inside the transaction that writes an entry:
    /// the state of the agent, which is read there under its lock, cannot be read.
    /// </summary>
    private sealed class TradingWithoutItsDatabase(IAgentTrading agents) : IAgentTrading
    {
        public Task<AgentState?> StateUnderLockAsync(EcnanibDbContext context, Guid userId, Guid agentId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The database did not answer.");

        public Task<bool> RequireAttentionAsync(Guid userId, Guid agentId, AgentStateCause cause, CancellationToken cancellationToken) =>
            agents.RequireAttentionAsync(userId, agentId, cause, cancellationToken);
    }
}
