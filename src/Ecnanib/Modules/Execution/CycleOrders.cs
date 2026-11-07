using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;
using ExchangeSide = Ecnanib.Modules.ExchangeAdapter.Contracts.OrderSide;
using TraceSide = Ecnanib.Modules.RiskEngine.Contracts.OrderSide;

namespace Ecnanib.Modules.Execution;

/// <summary>The orders a cycle placed, by the index on their cycle. Every query filters on the user and the agent (NFR-SEC-04).</summary>
internal sealed class CycleOrders(IDbContextFactory<EcnanibDbContext> contextFactory) : ICycleOrders
{
    public async Task<IReadOnlyList<CycleOrder>> ListAsync(Guid userId, Guid agentId, Guid cycleId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await context.Set<OrderRow>()
            .AsNoTracking()
            .Where(row => row.CycleId == cycleId && row.UserId == userId && row.AgentId == agentId)
            .OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.ClientOrderId)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(OrderOf)];
    }

    private static CycleOrder OrderOf(OrderRow row) => new(
        row.Id,
        row.Environment,
        row.Symbol,
        row.Leg,
        row.Type,
        row.Side == ExchangeSide.Buy ? TraceSide.Buy : TraceSide.Sell,
        row.Quantity,
        row.Price,
        row.StopPrice,
        row.ReduceOnly,
        row.Status,
        row.ExecutedQuantity,
        row.AveragePrice,
        row.ClientOrderId,
        row.ExchangeOrderId,
        row.ErrorCode,
        row.CreatedAt,
        row.SentAt,
        row.FilledAt,
        row.FinishedAt);
}
