using Ecnanib.Agents;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The equity of each agent of a user, with what the agent made in the UTC day and what it
/// holds, for the dashboard: total equity across agents, profit and loss for the day, and open
/// positions, per mode (FR-RPT-1). See docs/dashboard.md, and "The equity of an agent" in
/// docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is the equity that the loss limits are held to, by the same rule:</b> the capital that
/// its user allocated to the agent, plus its net profit or loss to date, with what its open
/// position would make now. <see cref="AgentResults"/> works the result out from the rows, and
/// <see cref="EquityMarks.At"/> says the result as the UTC day began.
/// </para>
/// <para>
/// <b>It asks the exchange nothing.</b> An open position is valued at the price that the watch
/// has, which is one that a page follows: see <see cref="IExchangePriceWatch.Latest"/>. Where
/// the watch has none, the equity of the agent is not known, and is said as that, and never as
/// a number: a page never waits for the exchange, and never guesses.
/// </para>
/// <para>
/// <b>It is of the mode that each agent is in, in the asset that its symbol is priced in.</b>
/// What an agent did in Demo is never combined with what it does in Live (FR-BIN-11).
/// </para>
/// <para>
/// <b>The agents of a user are read at one moment,</b> in one snapshot: a stop-loss that is
/// executed between two reads is in all of the answer, or in none of it.
/// </para>
/// <para>
/// It is asked for the user that a page hands over, which is the one who is signed in: the
/// Decision Engine may not use Accounts. It only reads, and writes no mark and no equity: that
/// is for what watches the loss limits. See <see cref="LossLimitWatch"/>.
/// </para>
/// </remarks>
public interface IAgentEquities
{
    /// <summary>The equity of each agent of a user, oldest agent first.</summary>
    /// <param name="userId">The user whose agents they are.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    Task<IReadOnlyList<AgentEquity>> OfUserAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>The equity of an agent at a moment, with what it holds.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="Mode">The mode that the agent is in, which the figures are of.</param>
/// <param name="Asset">The asset that the symbol of the agent is priced in, which the figures are in.</param>
/// <param name="Capital">The capital that its user allocated to the agent.</param>
/// <param name="Equity">
/// Its equity: the capital, plus its net profit or loss to date, with unrealized profit and loss.
/// Null where it is not known: the price of its open position is not known, or an order that
/// closed a part of a position says no price that it was executed at yet.
/// </param>
/// <param name="MadeToday">
/// What the agent made in the UTC day: its equity now, less its equity as the day began, which
/// is the first that was worked out in the day. Null where its equity is not known.
/// </param>
/// <param name="Position">Its open position, with the protection that rests on the exchange for it, or null when it holds none.</param>
/// <param name="Price">The price that the open position is valued at, as the watch last read it, or null when it holds none, or no price is known.</param>
public sealed record AgentEquity(
    Guid AgentId,
    TradingMode Mode,
    string Asset,
    decimal Capital,
    decimal? Equity,
    decimal? MadeToday,
    OpenPosition? Position,
    WatchedPrice? Price)
{
    /// <summary>What the open position would make at its price, before fees and funding, or null when the agent holds none, or no price is known.</summary>
    public decimal? Unrealized => Position is { } position && Price is { } price ? position.UnrealizedAt(price.Price) : null;
}

/// <summary>What the agents of one mode add up to, in one asset.</summary>
/// <param name="Mode">The mode.</param>
/// <param name="Asset">The asset that the amounts are in.</param>
/// <param name="Agents">How many agents are counted.</param>
/// <param name="Capital">The capital that their user allocated to them.</param>
/// <param name="Equity">Their equity, or null when the equity of one of them is not known.</param>
/// <param name="MadeToday">What they made in the UTC day, or null when that of one of them is not known.</param>
/// <param name="PricedAt">When the oldest of the prices that their open positions are valued at was read, or null when none of them is valued at a price.</param>
public sealed record EquityTotal(
    TradingMode Mode, string Asset, int Agents, decimal Capital, decimal? Equity, decimal? MadeToday, DateTimeOffset? PricedAt);

/// <summary>
/// The one rule for what the equities of agents add up to (FR-RPT-1): each mode on its own, and
/// each asset on its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Demo and Live are never added up</b> (FR-BIN-11), and neither are two assets: an amount
/// in one is no amount in the other. So a total is of one mode and one asset.
/// </para>
/// <para>
/// <b>A total that has a part which is not known is not known.</b> It is not the sum of the
/// parts that are: that would be a number which is wrong, and looks right.
/// </para>
/// </remarks>
public static class EquityTotals
{
    /// <summary>What the equities add up to, for each mode and asset that an agent is of, by the mode and then by the name of the asset.</summary>
    public static IReadOnlyList<EquityTotal> Of(IEnumerable<AgentEquity> equities)
    {
        ArgumentNullException.ThrowIfNull(equities);

        return
        [
            .. equities
                .GroupBy(equity => (equity.Mode, equity.Asset))
                .OrderBy(group => group.Key.Mode)
                .ThenBy(group => group.Key.Asset, StringComparer.Ordinal)
                .Select(group => new EquityTotal(
                    group.Key.Mode,
                    group.Key.Asset,
                    group.Count(),
                    group.Sum(equity => equity.Capital),
                    group.All(equity => equity.Equity is not null) ? group.Sum(equity => equity.Equity!.Value) : null,
                    group.All(equity => equity.MadeToday is not null) ? group.Sum(equity => equity.MadeToday!.Value) : null,
                    group.Where(equity => equity.Price is not null).Select(equity => (DateTimeOffset?)equity.Price!.ReadAt).Min())),
        ];
    }
}

/// <summary>Works out the equity of each agent of a user from the rows, and the prices that are followed. See <see cref="IAgentEquities"/>.</summary>
internal sealed class AgentEquities(IDbContextFactory<EcnanibDbContext> contexts, IExchangePriceWatch prices, TimeProvider clock)
    : IAgentEquities
{
    public async Task<IReadOnlyList<AgentEquity>> OfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var read = new List<(Agent Agent, AgentRows Rows, OpenPosition? Position)>();

        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        await using (await context.BeginSnapshotAsync(cancellationToken))
        {
            // Asked for by owner: the agents of another user are not found, and neither is
            // anything of theirs. A user has few agents, and each is asked about in turn.
            var agents = await context.Set<Agent>()
                .AsNoTracking()
                .OwnedBy(userId)
                .OrderBy(agent => agent.CreatedAt)
                .ThenBy(agent => agent.Id)
                .ToListAsync(cancellationToken);

            foreach (var agent in agents)
            {
                var rows = await AgentResults.RowsAsync(context, agent, cancellationToken);

                // The protection of the position as it rests on the exchange, by the rule of
                // the page of the agent: the price alone protects nothing.
                var position = rows.Open is { } open
                    ? OpenPositions.Of(open, await RestingOrders.OfAsync(context, agent.Id, cancellationToken))
                    : null;

                read.Add((agent, rows, position));
            }
        }

        var now = clock.GetUtcNow();

        return
        [
            .. read.Select(one =>
            {
                var (agent, rows, position) = one;

                // The price that the watch has, when a page follows it. Nothing is asked of the
                // exchange for it, and a price that nobody follows is no error: it is not known.
                var price = position is null
                    ? null
                    : prices.Latest(position.Market, position.Mode, position.Symbol, PositionValues.PriceOf(position.Market));

                var result = rows.At(price?.Price);

                if (!result.Valued)
                {
                    return new AgentEquity(
                        agent.Id, agent.Mode, agent.QuoteAsset, agent.AllocatedCapital, Equity: null, MadeToday: null, position, price);
                }

                // The result as the UTC day began, as what watches the loss limits would say it
                // now: the daily loss limit of the agent is counted from the same.
                var (_, atDayStart, _) = EquityMarks.At(rows.Marks, result.Net, agent.AllocatedCapital, agent.QuoteAsset, now);

                return new AgentEquity(
                    agent.Id,
                    agent.Mode,
                    agent.QuoteAsset,
                    agent.AllocatedCapital,
                    agent.AllocatedCapital + result.Net,
                    result.Net - atDayStart,
                    position,
                    price);
            }),
        ];
    }
}
