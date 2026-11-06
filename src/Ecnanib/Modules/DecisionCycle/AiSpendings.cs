using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>
/// Sums agents' AI calls from the trace, where every billed call is, because each is saved as its role finishes.
/// A call counts in the period its cycle started in, which the cycles' index on user, agent and start finds.
/// </summary>
internal sealed class AiSpendings(IDbContextFactory<EcnanibDbContext> contextFactory, TimeProvider time) : IAiSpending
{
    public async Task<IReadOnlyDictionary<Guid, AiSpend>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await SumAsync(context, userId, null, time.GetUtcNow(), cancellationToken);
    }

    public async Task<AiSpend> FindAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await FindAsync(context, userId, agentId, time.GetUtcNow(), cancellationToken);
    }

    /// <summary>The agent's spend in the day and month <paramref name="now"/> falls in.</summary>
    public static async Task<AiSpend> FindAsync(
        EcnanibDbContext context, Guid userId, Guid agentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var spend = await SumAsync(context, userId, agentId, now, cancellationToken);

        return spend.GetValueOrDefault(agentId) ?? AiSpend.None(now);
    }

    /// <summary>One query for the month, with the day as a filtered sum of it.</summary>
    private static async Task<Dictionary<Guid, AiSpend>> SumAsync(
        EcnanibDbContext context, Guid userId, Guid? agentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var day = BudgetPeriods.DayStart(now);
        var month = BudgetPeriods.MonthStart(now);

        var cycles = context.Set<DecisionCycleRow>().Where(cycle => cycle.UserId == userId && cycle.StartedAt >= month);

        if (agentId is { } id)
        {
            cycles = cycles.Where(cycle => cycle.AgentId == id);
        }

        var sums = await cycles
            .Join(
                context.Set<AiCallRow>(),
                cycle => cycle.Id,
                call => call.CycleId,
                (cycle, call) => new { cycle.AgentId, cycle.StartedAt, call.Cost, Tokens = call.InputTokens + call.OutputTokens })
            .GroupBy(call => call.AgentId)
            .Select(calls => new
            {
                AgentId = calls.Key,
                DayUsd = calls.Where(call => call.StartedAt >= day).Sum(call => call.Cost),
                DayTokens = calls.Where(call => call.StartedAt >= day && call.Cost == null).Sum(call => call.Tokens),
                MonthUsd = calls.Sum(call => call.Cost),
                MonthTokens = calls.Where(call => call.Cost == null).Sum(call => call.Tokens),
            })
            .ToListAsync(cancellationToken);

        return sums.ToDictionary(
            sum => sum.AgentId,
            sum => new AiSpend(now, sum.DayUsd ?? 0m, sum.DayTokens, sum.MonthUsd ?? 0m, sum.MonthTokens));
    }
}
