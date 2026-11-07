using System.Globalization;
using Ecnanib.Kernel;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.Experience.Contracts;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.DecisionCycle;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// <see cref="CycleApp"/> with priced models, so each call of <see cref="FakeAiProvider.Completion"/>'s 1,000 input
/// and 200 output tokens costs $2, and the metrics job for the test to run. Cycles are real, or seeded straight into
/// the trace, with trades seeded into the journal, for figures a real cycle can't easily give. The clock starts on an
/// hour whose sample candles rise for a while, so a bullish call made then hits.
/// </summary>
internal sealed class ExperienceApp : IAsyncDisposable
{
    public const string Provider = AgentApp.Provider;

    public const string Model = AgentApp.Model;

    /// <summary>What each call costs at these prices.</summary>
    public const decimal CallCost = 2m;

    public ExperienceApp(PostgresFixture postgres, Dictionary<string, string?>? settings = null)
    {
        var all = new Dictionary<string, string?>
        {
            ["Catalog:Overrides:0:Provider"] = AgentApp.Provider,
            ["Catalog:Overrides:0:Model"] = AgentApp.Model,
            ["Catalog:Overrides:0:InputPricePerMillion"] = "1000",
            ["Catalog:Overrides:0:OutputPricePerMillion"] = "5000",
        };

        foreach (var (key, value) in settings ?? [])
        {
            all[key] = value;
        }

        Cycles = new CycleApp(postgres, all, manualJobs: ["ExperienceMetricsJob"]);
        ToRisingHour();
    }

    public static CancellationToken Cancellation => CycleApp.Cancellation;

    public CycleApp Cycles { get; }

    public TestClock Clock => Cycles.Clock;

    public string ConnectionString => Cycles.ConnectionString;

    public IExperienceMetrics Metrics => Cycles.Agents.App.Services.GetRequiredService<IExperienceMetrics>();

    public ITradeJournal Journal => Cycles.Agents.App.Services.GetRequiredService<ITradeJournal>();

    public DateOnly Today => DateOnly.FromDateTime(Clock.GetUtcNow().UtcDateTime);

    /// <summary>Runs the metrics job, which must succeed.</summary>
    public async Task RefreshAsync() => Assert.Equal(JobStatus.Succeeded, await Cycles.Agents.RunJobAsync("ExperienceMetricsJob"));

    /// <summary>The user's metrics in <paramref name="environment"/>, from a week ago to today unless given.</summary>
    public Task<ExperienceReport> ReadAsync(
        Guid userId,
        TradingEnvironment environment = TradingEnvironment.Demo,
        DateOnly? from = null,
        DateOnly? to = null,
        Market? market = null,
        Timeframe? timeframe = null,
        string? symbol = null) =>
        Metrics.ReadAsync(userId, new ExperienceQuery(environment, from ?? Today.AddDays(-7), to ?? Today, market, timeframe, symbol), Cancellation);

    /// <summary>The report's one row for the model in the role.</summary>
    public static RoleModelMetrics Row(ExperienceReport report, AiRole role, string provider = Provider, string model = Model) =>
        Assert.Single(report.Models, row => row.Role == role && row.Provider == provider && row.ModelId == model);

    /// <summary>
    /// A finished cycle of the agent's, started at <paramref name="startedAt"/>, with its calls, in their order. Its
    /// candle closed at the start of that minute, so two cycles of an agent's never share one. Returns its ID.
    /// </summary>
    public async Task<Guid> SeedCycleAsync(
        Guid userId,
        Guid agentId,
        DateTimeOffset startedAt,
        IReadOnlyList<SeedCall> calls,
        TradingEnvironment environment = TradingEnvironment.Demo,
        Market market = Market.Spot,
        Timeframe timeframe = Timeframe.OneHour,
        string symbol = AgentApp.Symbol)
    {
        var id = Guid.NewGuid();
        var close = new DateTimeOffset(startedAt.Year, startedAt.Month, startedAt.Day, startedAt.Hour, startedAt.Minute, 0, TimeSpan.Zero);

        await Sql.ExecuteAsync(
            ConnectionString,
            $"""
            INSERT INTO decision_cycles (id, user_id, agent_id, market, environment, symbol, timeframe, candle_close, catch_up, deadline,
                started_at, finished_at, outcome, input_tokens, output_tokens)
            VALUES ('{id}', '{userId}', '{agentId}', '{market}', '{environment}', '{symbol}', '{timeframe}', '{At(close)}', false,
                '{At(close.AddMinutes(10))}', '{At(startedAt)}', '{At(startedAt.AddMinutes(1))}', 'Hold', 0, 0)
            """,
            Cancellation);

        foreach (var call in calls)
        {
            await Sql.ExecuteAsync(
                ConnectionString,
                $"""
                INSERT INTO ai_calls (cycle_id, role, provider, model_id, fallback, attempt, status, failure, output, templates, input_tokens,
                    output_tokens, cost, latency, provider_attempts, truncated, started_at)
                VALUES ('{id}', '{call.Role}', '{call.Provider}', '{call.Model}', {(call.Fallback ? "true" : "false")}, {call.Attempt},
                    '{call.Status}', {Text(call.Failure)}, {(call.Output is null ? "NULL" : $"'{call.Output}'::jsonb")}, '[]'::jsonb, 1000, 200,
                    {(call.Cost is { } cost ? cost.ToString(CultureInfo.InvariantCulture) : "NULL")},
                    make_interval(secs => {call.Latency.TotalSeconds.ToString(CultureInfo.InvariantCulture)}), 1,
                    {(call.Truncated ? "true" : "false")}, '{At(startedAt)}')
                """,
                Cancellation);
        }

        return id;
    }

    /// <summary>
    /// A closed trade of the agent's, settled unless told otherwise, whose entry order cites <paramref name="entryCycleId"/>,
    /// or has no order at all when it's null.
    /// </summary>
    public async Task<Guid> SeedTradeAsync(
        AgentApp.User user,
        Guid agentId,
        Guid? entryCycleId,
        decimal pnl,
        DateTimeOffset closedAt,
        TradingEnvironment environment = TradingEnvironment.Demo,
        string quoteAsset = "USDT",
        string symbol = AgentApp.Symbol,
        decimal? rMultiple = null,
        bool settled = true,
        decimal exitQuantity = 0.001m,
        TimeSpan? held = null)
    {
        var id = Guid.NewGuid();
        var entry = Guid.NewGuid();
        var openedAt = closedAt - (held ?? TimeSpan.FromHours(1));

        if (entryCycleId is { } cycle)
        {
            await Sql.ExecuteAsync(
                ConnectionString,
                $"""
                INSERT INTO orders (id, user_id, agent_id, cycle_id, origin, candle_close, credential_id, market, environment, symbol, leg,
                    client_order_id, type, side, quantity, reduce_only, status, executed_quantity, resolved, settles_at, created_at, updated_at)
                VALUES ('{entry}', '{user.Id}', '{agentId}', '{cycle}', 'Council', '{At(openedAt)}', '{user.ExchangeKey}', 'Spot', '{environment}',
                    '{symbol}', 'Entry', '{entry:N}', 'Market', 'Buy', 0.001, false, 'Filled', 0.001, true, '{At(openedAt)}', '{At(openedAt)}',
                    '{At(openedAt)}')
                """,
                Cancellation);
        }

        await Sql.ExecuteAsync(
            ConnectionString,
            $"""
            INSERT INTO trades (id, user_id, agent_id, entry_id, credential_id, market, environment, symbol, base_asset, quote_asset, side, status,
                opened_at, closed_at, quantity, entry_price, initial_stop, exit_quantity, exit_price, unpriced_quantity, fees, funding, realized_pnl,
                r_multiple, exit_reason, settled_at, settle_attempts, created_at, updated_at)
            VALUES ('{id}', '{user.Id}', '{agentId}', '{entry}', '{user.ExchangeKey}', 'Spot', '{environment}', '{symbol}', 'BTC', '{quoteAsset}',
                'Long', 'Closed', '{At(openedAt)}', '{At(closedAt)}', 0.001, 60000, 59900, {Number(exitQuantity)}, 59900, 0, 0, 0, {Number(pnl)},
                {(rMultiple is { } r ? Number(r) : "NULL")}, 'StopFilled', {(settled ? $"'{At(closedAt)}'" : "NULL")}, 0, '{At(closedAt)}',
                '{At(closedAt)}')
            """,
            Cancellation);

        return id;
    }

    /// <summary>A user with a Spot BTCUSDT 1H agent, which stays a Draft: seeded cycles don't need it Active.</summary>
    public async Task<(AgentApp.User User, Guid AgentId)> UserAsync()
    {
        var user = await Cycles.Agents.UserAsync();
        var agent = await Cycles.Agents.CreateAsync(user.Id, AgentApp.Ready(user));

        return (user, agent.Id);
    }

    public ValueTask DisposeAsync() => Cycles.DisposeAsync();

    private static string At(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);

    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(string? value) => value is null ? "NULL" : $"'{value}'";

    /// <summary>
    /// Five minutes past the next hour whose index since the epoch is 1 more than a multiple of 40. The candle that
    /// closed then opened on a multiple of 40, where the sample candles' closes start rising by 1 an hour for 39 hours.
    /// </summary>
    private void ToRisingHour()
    {
        var now = Clock.GetUtcNow();
        var hour = now.ToUnixTimeSeconds() / 3600;
        var next = hour + 1 + ((41 - ((hour + 1) % 40)) % 40);

        Clock.Advance(DateTimeOffset.FromUnixTimeSeconds(next * 3600) + TimeSpan.FromMinutes(5) - now);
    }
}

/// <summary>One AI call of a seeded cycle, answered by the test model, at $2, unless told otherwise.</summary>
internal sealed record SeedCall(
    AiRole Role,
    string Status = "Answered",
    string? Failure = null,
    string? Output = null,
    double LatencySeconds = 1,
    decimal? Cost = ExperienceApp.CallCost,
    string Provider = AgentApp.Provider,
    string Model = AgentApp.Model,
    bool Fallback = false,
    int Attempt = 1,
    bool Truncated = false)
{
    public TimeSpan Latency => TimeSpan.FromSeconds(LatencySeconds);

    public static SeedCall Strategist(string action) => new(AiRole.Strategist, Output: $$"""{"action":"{{action}}"}""");

    public static SeedCall Critic(string verdict) => new(AiRole.RiskCritic, Output: $$"""{"verdict":"{{verdict}}"}""");

    public static SeedCall Technical(string trend) => new(AiRole.TechnicalAnalyst, Output: $$"""{"trend":"{{trend}}"}""");

    public static SeedCall Sentiment(decimal score) =>
        new(AiRole.SentimentAnalyst, Output: $$"""{"score":{{score.ToString(CultureInfo.InvariantCulture)}}}""");
}
