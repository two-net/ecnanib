using Ecnanib.Database.Entities;
using Ecnanib.Logging;
using Ecnanib.Logging.Abstractions;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging.Live;

/// <summary>FR-LOG-07 against PostgreSQL: the log of one agent, scoped to its owner, filtered and paged.</summary>
[Trait("Category", "Live")]
public class LogReaderLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public LogReaderLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RowsAreScopedFilteredPagedAndLinkedToTheirDeliberation()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "log@example.com");
        var other = await AgentsLiveSeed.NewUserAsync(_fixture, "log-other@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var foreign = await AgentsLiveSeed.NewAgentAsync(_fixture, other.Id, "ETH", AgentState.Running, symbol: "ETHUSDT");
        var decision = await AgentsLiveSeed.NewDecisionAsync(_fixture, agent, new DateTimeOffset(2026, 1, 4, 22, 0, 0, TimeSpan.Zero));
        var now = AgentsLiveSeed.Now;
        await using (var db = _fixture.CreateContext())
        {
            db.AddRange(
                Row(now.AddMinutes(-5), LogLevel.Information, "Ecnanib.Agents.Services.AgentService", "Agent started", user.Id, agent.Id),
                Row(now.AddMinutes(-4), LogLevel.Debug, "Ecnanib.Agents.Services.AgentScheduler", "Agent is tracked on candles:production:spot:btcusdt:1h", null, agent.Id),
                Row(now.AddMinutes(-3), LogLevel.Warning, "Ecnanib.Council.Services.Council", "Council RiskManager: timed out after 100% of the budget", user.Id, agent.Id, decision.Id),
                Row(now.AddMinutes(-2), LogLevel.Error, "Ecnanib.Execution.Services.Live.ProtectionPlacer", "Protective order rejected", user.Id, agent.Id, exception: "ExchangeRejectedException: -2010"),
                Row(now.AddMinutes(-1), LogLevel.Information, "Ecnanib.Agents.Services.AgentService", "Agent paused", other.Id, agent.Id),
                Row(now, LogLevel.Information, "Ecnanib.Agents.Services.AgentService", "Agent started", other.Id, foreign.Id));
            await db.SaveChangesAsync(Ct);
        }

        var (provider, _, _, _, _) = AgentsLiveSeed.Build(_fixture, configure: s => s.AddLogReader());
        await using var _ = provider;
        var reader = provider.GetRequiredService<ILogReader>();

        var all = await reader.QueryAsync(user.Id, new LogQuery(agent.Id), Ct);

        Assert.NotNull(all);
        Assert.Equal("BTC", all.AgentName);
        // The user's rows and the ones without a user, newest first; another user's row on the same agent is not the user's to see.
        Assert.Equal(["Protective order rejected", "Council RiskManager: timed out after 100% of the budget", "Agent is tracked on candles:production:spot:btcusdt:1h", "Agent started"], all.Entries.Select(e => e.Message));
        Assert.False(all.HasMore);
        Assert.Equal(decision.EvaluationId, all.Entries[1].EvaluationId);
        Assert.Equal("ExchangeRejectedException: -2010", all.Entries[0].Exception);
        Assert.Contains("\"seed\"", all.Entries[0].ContextJson);
        Assert.Contains("true", all.Entries[0].ContextJson);

        var warnings = await reader.QueryAsync(user.Id, new LogQuery(agent.Id, MinLevel: LogLevel.Warning), Ct);
        Assert.Equal([LogLevel.Error, LogLevel.Warning], warnings!.Entries.Select(e => e.Level));

        var component = await reader.QueryAsync(user.Id, new LogQuery(agent.Id, Component: "Ecnanib.Agents.Services.AgentService"), Ct);
        Assert.Equal(["Agent started"], component!.Entries.Select(e => e.Message));

        // A literal percent sign and the case-insensitive match.
        var search = await reader.QueryAsync(user.Id, new LogQuery(agent.Id, Search: "100% OF"), Ct);
        Assert.Single(search!.Entries, e => e.Level == LogLevel.Warning);
        Assert.Empty((await reader.QueryAsync(user.Id, new LogQuery(agent.Id, Search: "100_ OF"), Ct))!.Entries);

        var window = await reader.QueryAsync(user.Id, new LogQuery(agent.Id, From: now.AddMinutes(-4), To: now.AddMinutes(-2)), Ct);
        Assert.Equal([LogLevel.Warning, LogLevel.Debug], window!.Entries.Select(e => e.Level));

        var first = await reader.QueryAsync(user.Id, new LogQuery(agent.Id, PageSize: 3), Ct);
        var second = await reader.QueryAsync(user.Id, new LogQuery(agent.Id, Page: 2, PageSize: 3), Ct);
        Assert.Equal(3, first!.Entries.Count);
        Assert.True(first.HasMore);
        Assert.Equal(["Agent started"], second!.Entries.Select(e => e.Message));
        Assert.False(second.HasMore);

        Assert.Equal(["Ecnanib.Agents.Services.AgentScheduler", "Ecnanib.Agents.Services.AgentService", "Ecnanib.Council.Services.Council", "Ecnanib.Execution.Services.Live.ProtectionPlacer"], await reader.ComponentsAsync(user.Id, agent.Id, Ct));

        Assert.Null(await reader.QueryAsync(other.Id, new LogQuery(agent.Id), Ct));
        Assert.Null(await reader.QueryAsync(user.Id, new LogQuery(Guid.NewGuid()), Ct));
        Assert.Empty(await reader.ComponentsAsync(other.Id, agent.Id, Ct));

        await using (var cleanup = _fixture.CreateContext())
            await cleanup.LogEntries.ExecuteDeleteAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id, other.Id);
    }

    private static LogEntry Row(DateTimeOffset at, LogLevel level, string component, string message, Guid? userId, Guid agentId, Guid? decisionId = null, string? exception = null) => new()
    {
        Timestamp = at, Level = level, Component = component, Message = message, UserId = userId, AgentId = agentId, DecisionId = decisionId, Exception = exception, Context = LiveSeed.Json("""{"seed":true}"""),
    };
}
