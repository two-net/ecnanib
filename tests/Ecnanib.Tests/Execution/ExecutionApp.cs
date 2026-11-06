using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Risk;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Execution;

/// <summary>
/// <see cref="AgentApp"/> with a <see cref="FakeOrderBook"/> behind Binance's order endpoints, and entries the Risk
/// Engine sized on the fake's own BTCUSDT filters. Tests execute them through <see cref="IOrderExecution"/>, as the
/// decision cycle does, and read what was recorded with SQL. The agent's watch runs as it would in production.
/// </summary>
internal sealed class ExecutionApp : IAsyncDisposable
{
    /// <summary>The fake's BTCUSDT, which market orders fill at.</summary>
    public const decimal Price = 60_000m;

    public const decimal Stop = 59_800m;

    public const decimal Target = 60_400m;

    public ExecutionApp(PostgresFixture postgres, Dictionary<string, string?>? settings = null, string? connectionString = null)
    {
        // The sweep runs only when a test runs it.
        Agents = new AgentApp(postgres, settings, connectionString: connectionString, manualJobs: ["OrderWatchSweep"]);
        Book = new FakeOrderBook(Agents.Binance, Agents.Clock) { Price = Price };
    }

    public static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public AgentApp Agents { get; }

    public FakeOrderBook Book { get; }

    public TestClock Clock => Agents.Clock;

    public string ConnectionString => Agents.ConnectionString;

    public IOrderExecution Execution => Agents.App.Services.GetRequiredService<IOrderExecution>();

    /// <summary>A user with keys, and an Autonomous agent on BTCUSDT, which needn't be Active to execute.</summary>
    public async Task<(AgentApp.User User, AgentRecord Agent)> AgentAsync(Market market = Market.Spot)
    {
        var user = await Agents.UserAsync();
        var agent = await Agents.CreateAsync(user.Id, AgentApp.Ready(user, market: market) with { ExecutionMode = ExecutionMode.Autonomous });

        return (user, agent);
    }

    /// <summary>A long the engine sized at <see cref="Price"/>, with its stop at <see cref="Stop"/> and a target unless left out.</summary>
    public static SizedOrder Long(Market market, EntryType type = EntryType.Market, decimal? limit = null, bool target = true) =>
        RiskHarness.Evaluate(
            new OrderIntent
            {
                Action = market == Market.Spot ? IntentAction.Buy : IntentAction.OpenLong,
                EntryType = type,
                LimitPrice = limit,
                StopPrice = Stop,
                TargetPrice = target ? Target : null,
            },
            Inputs(market)).Passes();

    /// <summary>A short on Futures, its stop above and target below.</summary>
    public static SizedOrder Short() =>
        RiskHarness.Evaluate(
            new OrderIntent { Action = IntentAction.OpenShort, EntryType = EntryType.Market, StopPrice = 60_200m, TargetPrice = 59_600m },
            Inputs(Market.Futures)).Passes();

    /// <summary>CANCEL_PENDING, which the engine passes while the agent has an unfilled entry.</summary>
    public static SizedOrder CancelPending(Market market) =>
        RiskHarness.Evaluate(new OrderIntent { Action = IntentAction.CancelPending }, Inputs(market) with { HasPendingEntry = true }).Passes();

    /// <summary>What the fake's exchange information says of BTCUSDT, at <see cref="Price"/>.</summary>
    public static RiskInputs Inputs(Market market) => market == Market.Spot
        ? RiskHarness.Spot() with
        {
            Filters = new ExchangeFilters(
                true,
                new TickRule(0.01m, 1_000_000m, 0.01m),
                new LotRule(0.00001m, 9000m, 0.00001m),
                new LotRule(0m, 127.44699891m, 0m),
                new NotionalRule(5m, 9_000_000m),
                new PercentPriceRule(5m, 0.2m, 5m, 0.2m)),
            Prices = new MarketPrices(Price - 0.01m, Price, Price),
            Atr = 100m,
        }
        : RiskHarness.Futures() with
        {
            Filters = new ExchangeFilters(
                true,
                new TickRule(261.10m, 809_484m, 0.10m),
                new LotRule(0.0001m, 1000m, 0.0001m),
                new LotRule(0.0001m, 120m, 0.0001m),
                new NotionalRule(50m, null),
                new PercentPriceRule(1.05m, 0m, 0m, 0.95m)),
            Prices = new MarketPrices(Price - 0.1m, Price, Price, Price),
            Atr = 100m,
        };

    public ExecutionRequest Request(
        AgentApp.User user,
        AgentRecord agent,
        SizedOrder order,
        DateTimeOffset? close = null,
        int? ttl = null,
        DateTimeOffset? deadline = null) =>
        new(
            user.Id,
            agent.Id,
            Guid.NewGuid(),
            close ?? LastClose,
            user.ExchangeKey,
            agent.Settings.Market,
            agent.Settings.Environment,
            agent.Settings.Symbol,
            agent.Settings.Timeframe,
            order,
            deadline ?? Clock.GetUtcNow() + TimeSpan.FromMinutes(10))
        {
            LimitTtlCandles = ttl,
        };

    /// <summary>The last hourly close.</summary>
    public DateTimeOffset LastClose
    {
        get
        {
            var now = Clock.GetUtcNow();
            return new DateTimeOffset(now.UtcTicks - (now.UtcTicks % TimeSpan.TicksPerHour), TimeSpan.Zero);
        }
    }

    public Task<ExecutionResult> ExecuteAsync(ExecutionRequest request) => Execution.ExecuteAsync(request, Cancellation);

    /// <summary>The agent's orders, oldest first, as leg, type, side, status and quantity.</summary>
    public Task<List<string>> OrdersAsync(Guid agentId) =>
        Sql.ColumnAsync(
            ConnectionString,
            $"""
            SELECT concat_ws(' ', leg, type, side, status, trim_scale(quantity)::text) FROM orders
            WHERE agent_id = '{agentId}' ORDER BY created_at, client_order_id
            """,
            Cancellation);

    /// <summary>The agent's protections, as sequence, status and quantity.</summary>
    public Task<List<string>> ProtectionsAsync(Guid agentId) =>
        Sql.ColumnAsync(
            ConnectionString,
            $"SELECT concat_ws(' ', sequence, status, trim_scale(quantity)::text) FROM protections WHERE agent_id = '{agentId}' ORDER BY created_at, sequence",
            Cancellation);

    public Task<List<string>> NotificationsAsync(Guid userId) =>
        Sql.ColumnAsync(ConnectionString, $"SELECT type FROM notifications WHERE user_id = '{userId}' ORDER BY id", Cancellation);

    /// <summary>How many of the agent's orders have each status.</summary>
    public Task<long> CountAsync(Guid agentId, string where) =>
        Sql.ScalarAsync<long>(ConnectionString, $"SELECT count(*) FROM orders WHERE agent_id = '{agentId}' AND {where}", Cancellation);

    public static Task UntilAsync(Func<Task<bool>> condition) => LoggedApp.UntilAsync(async () => await condition());

    public ValueTask DisposeAsync() => Agents.DisposeAsync();
}
