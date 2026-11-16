using Ecnanib.Accounts;
using Ecnanib.Credentials;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Agents;

/// <summary>
/// Switches the agents of users between Demo and Live (FR-AGT-7). See docs/agents.md.
/// </summary>
/// <remarks>
/// <para>
/// A switch is one transaction, which starts by locking the row of the account, as what changes
/// the settings of an agent does, as its controls do, and as what changes a key does. So the
/// agent that a switch finds under the lock is the agent until the transaction ends: it is not
/// started meanwhile, and the Live key is not deleted or refused between the question about it
/// and the switch.
/// </para>
/// <para>
/// Everything that a switch to Live takes is asked again here, under the lock, whatever a page
/// showed or asked: the state of the agent, the Live key, the symbol, the proof of who the user
/// is, and the confirmation of the risk. The switch is recorded in the audit trail in the same
/// transaction (NFR-LOG-7).
/// </para>
/// <para>
/// The exchange is asked which symbols the markets have before the transaction, and never
/// inside it: a call that waits for the exchange would hold the lock for as long. Both markets
/// are asked about, so the answer covers the agent as it is found under the lock.
/// </para>
/// </remarks>
internal sealed class AgentModes(
    IDbContextFactory<EcnanibDbContext> contexts,
    IReauthentication reauthentication,
    IKeyValidation keys,
    IExchangeMarketData marketData,
    IEnumerable<IAgentExposure> exposures,
    BinanceSettings binance,
    TimeProvider clock,
    ILogger<AgentModes> logger) : IAgentModes
{
    public async Task<ModeSwitchResult?> BlockOfAsync(HttpContext request, Guid agentId, TradingMode to)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        var symbols = await AgentSymbols.ReadAsync(marketData, binance, to, cancellationToken);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Asked for by owner: an agent of another user is not found, like one that does not exist.
        var agent = await context.Set<Agent>()
            .AsNoTracking()
            .OwnedBy(userId)
            .SingleOrDefaultAsync(agent => agent.Id == agentId, cancellationToken);

        if (agent is null)
        {
            return new ModeSwitchResult(ModeSwitchOutcome.NotFound);
        }

        // What a page is told, of the agent as it is now. The switch asks again under the lock.
        return (await BlockAsync(context, agent, to, symbols, cancellationToken)).Block;
    }

    public Task<ModeSwitchResult> SwitchToLiveAsync(HttpContext request, Guid agentId, bool riskConfirmed) =>
        SwitchAsync(request, agentId, TradingMode.Live, riskConfirmed);

    public Task<ModeSwitchResult> SwitchToDemoAsync(HttpContext request, Guid agentId) =>
        SwitchAsync(request, agentId, TradingMode.Demo, riskConfirmed: false);

    private async Task<ModeSwitchResult> SwitchAsync(HttpContext request, Guid agentId, TradingMode to, bool riskConfirmed)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        // Before the transaction, and of both markets: the agent is read under the lock.
        var symbols = await AgentSymbols.ReadAsync(marketData, binance, to, cancellationToken);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (!await StoredAgents.LockAccountAsync(context, userId, cancellationToken))
        {
            // The account was deleted, on another browser, since this request was signed in.
            return new ModeSwitchResult(ModeSwitchOutcome.NotFound);
        }

        // Asked for by owner, and read under the lock: this is the agent until the transaction ends.
        var agent = await context.Set<Agent>()
            .AsNoTracking()
            .OwnedBy(userId)
            .SingleOrDefaultAsync(agent => agent.Id == agentId, cancellationToken);

        if (agent is null)
        {
            return new ModeSwitchResult(ModeSwitchOutcome.NotFound);
        }

        var (block, quoteAsset) = await BlockAsync(context, agent, to, symbols, cancellationToken);

        if (block is { Outcome: ModeSwitchOutcome.Switched })
        {
            // The form was sent twice, or sent from two browsers: the agent is in the mode that
            // was asked for, whatever became of it since. Nothing is recorded again.
            return block;
        }

        if (block is not null)
        {
            return Refused(block);
        }

        if (to == TradingMode.Live)
        {
            // Being signed in is not enough for an agent that trades with real money: its user
            // has just proved again who they are, on this browser (FR-ACC-4). Asked under the
            // lock, so the answer holds until the agent is switched.
            if (!await reauthentication.IsRecentAsync(context, request, SensitiveAction.SwitchAgentToLive))
            {
                return Refused(new ModeSwitchResult(ModeSwitchOutcome.ProofNeeded));
            }

            // Their own confirmation, given with this form, and for no other (FR-AGT-7).
            if (!riskConfirmed)
            {
                return Refused(new ModeSwitchResult(ModeSwitchOutcome.RiskNotConfirmed));
            }
        }

        // The mode, and the asset that the symbol is priced in there. The state of the agent
        // stays as it is: a draft or a stopped agent, which its user starts.
        await context.Set<Agent>()
            .OwnedBy(userId)
            .Where(stored => stored.Id == agentId)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(stored => stored.Mode, to)
                    .SetProperty(stored => stored.QuoteAsset, quoteAsset),
                cancellationToken);

        // A switch of the mode is money-relevant, and is recorded with it (NFR-LOG-7).
        await AuditTrail.RecordAsync(
            context,
            userId,
            to == TradingMode.Live ? AuditAction.AgentSwitchedToLive : AuditAction.AgentSwitchedToDemo,
            AuditTarget.Agent(agentId),
            clock.GetUtcNow(),
            AuditOrigin.Of(request),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("User {UserId} switched agent {AgentId} from {PreviousTradingMode} to {TradingMode}", userId, agentId, agent.Mode, to);

        return new ModeSwitchResult(ModeSwitchOutcome.Switched);

        ModeSwitchResult Refused(ModeSwitchResult refusal)
        {
            logger.LogInformation(
                "User {UserId} was refused switching agent {AgentId} to {TradingMode}: {ModeSwitchOutcome}",
                userId,
                agentId,
                to,
                refusal.Outcome);

            return refusal;
        }
    }

    /// <summary>
    /// What stands in the way of switching an agent to a mode, apart from the proof and the
    /// confirmation, with the asset that its symbol is priced in there when nothing does.
    /// </summary>
    private async Task<(ModeSwitchResult? Block, string QuoteAsset)> BlockAsync(
        EcnanibDbContext context, Agent agent, TradingMode to, IReadOnlyList<MarketSymbols> symbols, CancellationToken cancellationToken)
    {
        if (agent.Mode == to)
        {
            return (new ModeSwitchResult(ModeSwitchOutcome.Switched), agent.QuoteAsset);
        }

        // The mode goes by the rule of the market: in Draft, or Stopped with nothing open (FR-AGT-8).
        if (!await exposures.StructureCanChangeAsync(context, agent, cancellationToken))
        {
            return (new ModeSwitchResult(ModeSwitchOutcome.StructureLocked), agent.QuoteAsset);
        }

        // A valid Live key that may trade on the market of the agent (FR-AGT-7): what a start
        // asks, asked of the Live key. Binance is asked nothing.
        if (to == TradingMode.Live
            && await keys.BlockForAsync(agent.UserId, TradingMode.Live, agent.Market, cancellationToken) is { } keyBlock)
        {
            return (new ModeSwitchResult(ModeSwitchOutcome.KeyBlocks, keyBlock), agent.QuoteAsset);
        }

        // Each market has symbols of its own in each environment. A symbol is not taken on
        // trust: while the exchange gives no answer, nothing is switched (FR-AGT-6).
        var (known, quoteAsset) = symbols.QuoteAssetOf(agent.Market, agent.Symbol);

        if (!known)
        {
            return (new ModeSwitchResult(ModeSwitchOutcome.SymbolsUnknown), agent.QuoteAsset);
        }

        return quoteAsset is null
            ? (new ModeSwitchResult(ModeSwitchOutcome.SymbolNotOnMarket), agent.QuoteAsset)
            : (null, quoteAsset);
    }
}
