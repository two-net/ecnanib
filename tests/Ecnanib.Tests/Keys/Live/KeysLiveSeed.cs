using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Tests.Database.Live;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Keys.Live;

/// <summary>Users and agents for the keys live tests; keys themselves go through the service under test.</summary>
internal static class KeysLiveSeed
{
    public static async Task<User> NewUserAsync(DatabaseLiveFixture fixture, string email, CancellationToken ct)
    {
        await using var db = fixture.CreateContext();
        var user = LiveSeed.NewUser(email);
        db.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    public static async Task<Agent> NewAgentAsync(DatabaseLiveFixture fixture, Guid userId, string name, AgentMode mode, AgentState state, Guid? exchangeKeyId = null, params (AiProvider Provider, bool Enabled)[] council)
    {
        await using var db = fixture.CreateContext();
        var agent = new Agent
        {
            UserId = userId,
            Name = name,
            MarketType = Ecnanib.Exchange.Abstractions.MarketType.UsdFutures,
            Symbol = "BTCUSDT",
            Timeframe = Ecnanib.Exchange.Abstractions.CandleInterval.OneHour,
            StrategyId = "trend-pullback",
            StrategyVersion = 1,
            Mode = mode,
            ExchangeKeyId = exchangeKeyId,
            State = state,
            PauseCause = state == AgentState.Paused ? PauseCause.User : null,
            StateReason = state == AgentState.Paused ? "Paused by the user." : null,
            CreatedAt = LiveSeed.Now,
            UpdatedAt = LiveSeed.Now,
        };
        var role = CouncilRole.MarketAnalyst;
        foreach (var (provider, enabled) in council)
            agent.CouncilAssignments.Add(new CouncilAssignment { Role = role++, Provider = provider, Model = provider.ToString().ToLowerInvariant() + "-test", Enabled = enabled });
        db.Add(agent);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return agent;
    }

    public static async Task<Agent> AgentAsync(DatabaseLiveFixture fixture, Guid agentId, CancellationToken ct)
    {
        await using var db = fixture.CreateContext();
        return await db.Agents.AsNoTracking().SingleAsync(a => a.Id == agentId, ct);
    }

    public static async Task DeleteUsersAsync(DatabaseLiveFixture fixture, CancellationToken ct, params Guid[] userIds)
    {
        await using var db = fixture.CreateContext();
        await db.Users.Where(u => userIds.Contains(u.Id)).ExecuteDeleteAsync(ct);
    }
}
