using Ecnanib.Agents.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Agents.Services;

/// <summary>
/// The daily loss limit (FR-EXE-09). Today's realized loss is −Σ <c>orders.realized_pnl</c> of the agent's exit fills
/// of the current UTC day (a partial counts the day it fills) plus the entry fees paid today, so a day's sum is the
/// trades' net P&amp;L; the base is the venue's equity before today's realized
/// P&amp;L, so a day's limit does not shrink as it is spent and unrealized P&amp;L never counts. When the loss reaches
/// the limit a Running agent is paused with one atomic conditional update, no gate taken, until the next UTC day; the
/// scheduler's minute tick lifts the pause.
/// </summary>
internal sealed class DailyLossLimit : IDailyLossLimit
{
    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IOrderVenues _venues;
    private readonly IAgentEvents _events;
    private readonly TimeProvider _time;
    private readonly ILogger<DailyLossLimit> _logger;

    public DailyLossLimit(IDbContextFactory<EcnanibDbContext> factory, IOrderVenues venues, IAgentEvents events, TimeProvider time, ILogger<DailyLossLimit> logger)
    {
        _factory = factory;
        _venues = venues;
        _events = events;
        _time = time;
        _logger = logger;
    }

    public async Task<DailyLossStatus> CheckAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var nextDay = dayStart.AddDays(1);

        Agent? agent;
        decimal realizedToday;
        await using (var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == agentId, cancellationToken).ConfigureAwait(false);
            if (agent is null)
                return new DailyLossStatus(false, 0m, 0m, 0m, false);
            // Exit fills carry their own fee inside realized_pnl; the entry fees paid today are the other realized cost.
            realizedToday = await db.Orders
                .Where(o => o.AgentId == agentId && o.RealizedPnl != null && o.FilledAt != null && o.FilledAt >= dayStart && o.FilledAt < nextDay)
                .SumAsync(o => o.RealizedPnl ?? 0m, cancellationToken).ConfigureAwait(false);
            realizedToday -= await db.Orders
                .Where(o => o.AgentId == agentId && o.Purpose == OrderPurpose.Entry && o.Fee != null && o.FilledAt != null && o.FilledAt >= dayStart && o.FilledAt < nextDay)
                .SumAsync(o => o.Fee ?? 0m, cancellationToken).ConfigureAwait(false);
        }

        var lossToday = Math.Max(0m, -realizedToday);
        var limitPercent = agent.RiskLimits.DailyLossLimitPercent;
        if (lossToday == 0m)
            return new DailyLossStatus(false, 0m, 0m, limitPercent, false);

        var equity = await _venues.For(agent.Mode).GetEquityAsync(agent, cancellationToken).ConfigureAwait(false);
        var baseEquity = equity - realizedToday;
        var limitAmount = baseEquity > 0 ? baseEquity * limitPercent / 100m : 0m;
        var reached = limitAmount > 0 && lossToday >= limitAmount;
        if (!reached)
            return new DailyLossStatus(false, lossToday, limitAmount, limitPercent, false);

        var paused = await PauseAsync(agent, lossToday, limitPercent, nextDay, now, cancellationToken).ConfigureAwait(false);
        return new DailyLossStatus(true, lossToday, limitAmount, limitPercent, paused);
    }

    private async Task<bool> PauseAsync(Agent agent, decimal lossToday, decimal limitPercent, DateTimeOffset resumeAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (agent.State != AgentState.Running)
            return false;
        var reason = $"Daily loss limit of {limitPercent:0.##}% reached (−{lossToday:0.##}); paused until {resumeAt:yyyy-MM-dd HH:mm} UTC.";
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Agents
            .Where(a => a.Id == agent.Id && a.State == AgentState.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.State, AgentState.Paused)
                .SetProperty(a => a.PauseCause, PauseCause.DailyLoss)
                .SetProperty(a => a.AutoResumeAt, resumeAt)
                .SetProperty(a => a.StateReason, reason)
                .SetProperty(a => a.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (rows == 0)
            return false;

        db.AgentChanges.Add(ChangeSet.State(agent.Id, now, AgentState.Running, AgentState.Paused, reason));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("Agent {AgentId} paused on its daily loss limit: {Reason}", agent.Id, reason);
        _events.Publish(new AgentStateChanged(agent.Id, agent.UserId, now, AgentState.Paused, reason));
        return true;
    }
}
