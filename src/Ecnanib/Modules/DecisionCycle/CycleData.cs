using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>
/// What the Risk Engine needs that the cycle reads from Binance and the records: the pair, the market at a candle's close,
/// the agent's account, which reading reconciles, and its trading record. A cycle reads them for its own close, and an
/// approval again for its cycle's, so the engine's second look is on prices and an account as they are now (FR-APR-03).
/// </summary>
internal sealed class CycleData(
    IExchangeSymbols symbols,
    IMarketData marketData,
    IAgentAccounts accounts,
    ITradeJournal journal,
    IDbContextFactory<EcnanibDbContext> contextFactory,
    TimeProvider time,
    ILogger<CycleData> logger)
{
    /// <summary>
    /// The pair, the market at <paramref name="candleClose"/> and the account. Binance's limit is waited out once, when that
    /// fits before <paramref name="deadline"/>.
    /// </summary>
    public async Task<Collected> CollectAsync(
        Guid userId,
        AgentRecord agent,
        DateTimeOffset candleClose,
        DateTimeOffset deadline,
        JobContext context,
        CancellationToken cancellationToken)
    {
        var agentSettings = agent.Settings;
        ExchangeSymbolInfo? symbol;
        MarketSnapshot snapshot;

        try
        {
            symbol = await symbols.FindAsync(agentSettings.Market, agentSettings.Environment, agentSettings.Symbol, cancellationToken);

            if (symbol is null)
            {
                logger.LogWarning("Binance no longer lists the agent's pair {Symbol}", agentSettings.Symbol);

                return Collected.Failed(CycleFailure.MarketData);
            }

            snapshot = await SnapshotAsync(agentSettings, candleClose, deadline, context, cancellationToken);
        }
        catch (MarketDataException exception)
        {
            logger.LogWarning("Binance couldn't give the market for the cycle: {Failure}", exception.Failure);

            return Collected.Failed(CycleFailure.MarketData);
        }

        // During maintenance Binance can have no candle for a time, and an analysis of the one before would be stale.
        if (snapshot.Candles.Candles.Count == 0 || snapshot.Candles.Candles[^1].OpenTime != snapshot.Candles.LastOpenTime)
        {
            logger.LogWarning("Binance has no candle for the close at {CandleClose}", candleClose);

            return Collected.Failed(CycleFailure.MarketData);
        }

        if (agentSettings.ExchangeCredentialId is not { } credentialId)
        {
            logger.LogWarning("The agent has no exchange key, so its account can't be read");

            return Collected.Failed(CycleFailure.Account);
        }

        try
        {
            var account = await accounts.ReadAsync(
                userId, agent.Id, credentialId, agentSettings.Market, symbol.Symbol, symbol.BaseAsset, symbol.QuoteAsset, cancellationToken);

            return new Collected(symbol, snapshot, account, null);
        }
        catch (AgentAccountException exception)
        {
            logger.LogWarning("The agent's account couldn't be read: {Failure}", exception.Failure);

            return Collected.Failed(CycleFailure.Account);
        }
    }

    /// <summary>
    /// The agent's results from its trade journal (#36), for the loss limits, the entries-per-day cap and the cooldown
    /// (FR-RISK-02, FR-RISK-08). Its streak counts from when it last became Active, so a resumed agent doesn't pause
    /// again at once. The cooldown counts the cycles before <paramref name="candleClose"/> that started after the losing
    /// trade closed and have finished, so the cycle whose reconciliation found the loss isn't one of them, and neither is
    /// the one at <paramref name="candleClose"/>. An approval reads it for its cycle's close, which is stricter: a loss
    /// since its cycle started leaves no cooldown cycles at all.
    /// </summary>
    public async Task<TradingRecord> RecordAsync(Guid userId, AgentRecord agent, DateTimeOffset candleClose, CancellationToken cancellationToken)
    {
        var results = await journal.ReadResultsAsync(
            userId, agent.Id, agent.Settings.Environment, time.GetUtcNow(), agent.StatusChangedAt, cancellationToken);
        int? cyclesSinceLoss = null;

        if (results.LastLossClosedAt is { } lossClosedAt)
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            cyclesSinceLoss = await context.Set<DecisionCycleRow>()
                .CountAsync(
                    cycle => cycle.UserId == userId && cycle.AgentId == agent.Id
                        && cycle.StartedAt > lossClosedAt
                        && cycle.CandleClose < candleClose
                        && cycle.Outcome != null && cycle.Outcome != CycleOutcome.Interrupted,
                    cancellationToken);
        }

        return new TradingRecord(results.RealizedToday, results.RealizedThisWeek, results.ConsecutiveLosses, results.EntriesToday, cyclesSinceLoss);
    }

    /// <summary>The market at the candle's close. Binance's limit is waited out once, when that fits before the deadline.</summary>
    private async Task<MarketSnapshot> SnapshotAsync(
        AgentSettings agentSettings, DateTimeOffset candleClose, DateTimeOffset deadline, JobContext context, CancellationToken cancellationToken)
    {
        var marketRequest = new MarketDataRequest(
            agentSettings.Market, agentSettings.Environment, agentSettings.Symbol, agentSettings.Timeframe, candleClose);

        try
        {
            return await marketData.CollectAsync(marketRequest, cancellationToken);
        }
        catch (MarketDataException exception) when (exception is { Failure: MarketDataFailure.Throttled, RetryAfter: { } wait }
            && time.GetUtcNow() + wait < deadline)
        {
            logger.LogInformation("Binance is limiting requests, so the cycle waits {RetryAfterMs:0} ms and tries again", wait.TotalMilliseconds);

            // Waiting is safe to abandon, so it ends when shutdown begins.
            await Task.Delay(wait, time, context.Stopping);

            return await marketData.CollectAsync(marketRequest, cancellationToken);
        }
    }
}

/// <summary>What <see cref="CycleData.CollectAsync"/> read, or why it couldn't.</summary>
internal sealed record Collected(ExchangeSymbolInfo? Symbol, MarketSnapshot? Snapshot, AgentAccount? Account, CycleFailure? Failure)
{
    public static Collected Failed(CycleFailure failure) => new(null, null, null, failure);
}
