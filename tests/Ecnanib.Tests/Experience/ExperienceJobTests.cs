using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// The metrics job: a final day is built once and kept after retention purges its trace (#50), an agent's metrics go
/// with it, and a user's metrics and trades are theirs alone (NFR-SEC-04).
/// </summary>
public sealed class ExperienceJobTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => ExperienceApp.Cancellation;

    [Fact]
    public async Task A_final_day_is_never_built_again_and_outlives_its_trace()
    {
        await using var app = new ExperienceApp(postgres);
        var (user, agent) = await app.UserAsync();
        var started = app.Clock.GetUtcNow().AddMinutes(-30);
        var day = DateOnly.FromDateTime(started.UtcDateTime);
        var cycle = await app.SeedCycleAsync(user.Id, agent, started, [SeedCall.Strategist("BUY"), SeedCall.Critic("approve")]);
        await app.SeedTradeAsync(user, agent, cycle, 4m, started.AddMinutes(20));
        await app.RefreshAsync();

        // A 1H day is final three candles and a quarter of an hour after it ends.
        app.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromHours(4));
        await app.RefreshAsync();

        Assert.True(await BuiltUntilAsync(app, user.Id) > day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        // Retention purges the trace, and a cycle the day never had can't be built into it.
        await Sql.ExecuteAsync(app.ConnectionString, $"DELETE FROM decision_cycles WHERE user_id = '{user.Id}'", Cancellation);
        await app.SeedCycleAsync(user.Id, agent, started.AddMinutes(5), [SeedCall.Strategist("HOLD")]);
        await app.RefreshAsync();

        var report = await app.ReadAsync(user.Id, from: day, to: day, timeframe: Timeframe.OneHour);
        var strategist = ExperienceApp.Row(report, AiRole.Strategist);

        Assert.False(report.Provisional);
        Assert.Equal(1, strategist.Calls.Sent);
        Assert.Equal(new(Holds: 0, Entries: 1, Exits: 0, Adjustments: 0), strategist.Strategist);
        Assert.Equal(4m, Assert.Single(strategist.Trades.Results).TotalPnl);
        Assert.Equal(0, report.UnattributedTrades);
    }

    [Fact]
    public async Task Todays_metrics_are_built_again_on_every_run()
    {
        await using var app = new ExperienceApp(postgres);
        var (user, agent) = await app.UserAsync();
        var now = app.Clock.GetUtcNow();

        await app.SeedCycleAsync(user.Id, agent, now.AddMinutes(-40), [SeedCall.Strategist("HOLD")]);
        await app.RefreshAsync();
        await app.SeedCycleAsync(user.Id, agent, now.AddMinutes(-20), [SeedCall.Strategist("HOLD")]);
        await app.RefreshAsync();
        await app.RefreshAsync();

        Assert.Equal(2, ExperienceApp.Row(await app.ReadAsync(user.Id), AiRole.Strategist).Calls.Sent);
    }

    [Fact]
    public async Task A_trade_whose_entry_cycle_is_unknown_counts_for_no_model()
    {
        await using var app = new ExperienceApp(postgres);
        var (user, agent) = await app.UserAsync();

        await app.SeedTradeAsync(user, agent, null, 3m, app.Clock.GetUtcNow().AddMinutes(-10));
        await app.RefreshAsync();

        var report = await app.ReadAsync(user.Id);

        Assert.Empty(report.Models);
        Assert.Equal(1, report.UnattributedTrades);
    }

    [Fact]
    public async Task Deleting_an_agent_deletes_its_metrics()
    {
        await using var app = new ExperienceApp(postgres);
        var (user, agent) = await app.Cycles.ActiveAgentAsync();
        await app.Cycles.RunAsync(user, agent);
        await app.RefreshAsync();

        Assert.NotEmpty((await app.ReadAsync(user.Id)).Models);
        Assert.NotEqual(0L, await CountAsync(app, "cycle_attributions", agent.Id));

        await app.Cycles.Agents.Agents.PauseAsync(user.Id, agent.Id, Cancellation);
        Assert.Equal(TransitionOutcome.Done, (await app.Cycles.Agents.Agents.ArchiveAsync(user.Id, agent.Id, Cancellation)).Outcome);
        Assert.Equal(TransitionOutcome.Done, (await app.Cycles.Agents.Agents.DeleteAsync(user.Id, agent.Id, Cancellation)).Outcome);

        Assert.Empty((await app.ReadAsync(user.Id)).Models);
        Assert.Equal(0L, await CountAsync(app, "performance_snapshots", agent.Id));
        Assert.Equal(0L, await CountAsync(app, "cycle_attributions", agent.Id));
    }

    [Fact]
    public async Task Another_users_metrics_and_trades_are_never_read()
    {
        await using var app = new ExperienceApp(postgres);
        var (owner, agent) = await app.UserAsync();
        var (stranger, _) = await app.UserAsync();
        var now = app.Clock.GetUtcNow();
        var cycle = await app.SeedCycleAsync(owner.Id, agent, now.AddMinutes(-30), [SeedCall.Strategist("BUY")]);
        await app.SeedTradeAsync(owner, agent, cycle, 4m, now.AddMinutes(-10));
        await app.RefreshAsync();

        var theirs = await app.ReadAsync(stranger.Id);
        var trades = await app.Journal.ListSettledAsync(
            stranger.Id, new SettledTradeQuery(TradingEnvironment.Demo, now.AddDays(-1), now.AddDays(1)), Cancellation);

        Assert.Empty(theirs.Models);
        Assert.Equal(0, theirs.UnattributedTrades);
        Assert.Empty(trades);
        Assert.Single((await app.ReadAsync(owner.Id)).Models);
    }

    [Fact]
    public async Task The_journal_lists_settled_trades_in_one_environment_with_their_entrys_cycle()
    {
        await using var app = new ExperienceApp(postgres);
        var (user, agent) = await app.UserAsync();
        var now = app.Clock.GetUtcNow();
        var cycle = await app.SeedCycleAsync(user.Id, agent, now.AddMinutes(-50), [SeedCall.Strategist("BUY")]);

        var settled = await app.SeedTradeAsync(user, agent, cycle, 4m, now.AddMinutes(-40), rMultiple: 1.5m);
        await app.SeedTradeAsync(user, agent, cycle, 2m, now.AddMinutes(-30), settled: false);
        await app.SeedTradeAsync(user, agent, cycle, 9m, now.AddMinutes(-20), TradingEnvironment.Live);
        var orphan = await app.SeedTradeAsync(user, agent, null, -1m, now.AddMinutes(-10));

        var trades = await app.Journal.ListSettledAsync(
            user.Id, new SettledTradeQuery(TradingEnvironment.Demo, now.AddHours(-1), now), Cancellation);

        Assert.Equal([settled, orphan], trades.Select(trade => trade.Id));
        Assert.Equal(cycle, trades[0].EntryCycleId);
        Assert.Equal(1.5m, trades[0].RMultiple);
        Assert.Equal(TradingEnvironment.Demo, trades[0].Environment);
        Assert.Null(trades[1].EntryCycleId);
    }

    public static TheoryData<string, string> InvalidSettings => new()
    {
        { "Experience:AnalystHorizonCandles", "0" },
        { "Experience:AnalystHorizonCandles", "31" },
        { "Experience:AnalystHorizonCandles", "three" },
        { "Experience:UsdQuoteAssets:0", "usdt" },
        { "Experience:UsdQuoteAssets:0", "US DOLLAR" },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task An_invalid_setting_stops_startup_and_is_named(string key, string value)
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), settings: new Dictionary<string, string?> { [key] = value });

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains(key.Split(':')[0] + ":" + key.Split(':')[1], exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Npgsql reads a timestamp with time zone as a UTC <see cref="DateTime"/>.</summary>
    private static Task<DateTime> BuiltUntilAsync(ExperienceApp app, Guid userId) =>
        Sql.ScalarAsync<DateTime>(
            app.ConnectionString, $"SELECT built_until FROM experience_progress WHERE user_id = '{userId}' AND timeframe = 'OneHour'", Cancellation);

    private static Task<long> CountAsync(ExperienceApp app, string table, Guid agentId) =>
        Sql.ScalarAsync<long>(app.ConnectionString, $"SELECT count(*) FROM {table} WHERE agent_id = '{agentId}'", Cancellation);
}
