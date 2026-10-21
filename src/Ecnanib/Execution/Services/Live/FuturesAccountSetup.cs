using System.Collections.Concurrent;
using Ecnanib.Exchange.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Execution.Services.Live;

/// <summary>
/// FR-EXE-11 before the first futures order of an agent in this process: the account must be in one-way position mode
/// (hedge mode is a fault with guidance, A-06), and the symbol takes the agent's margin type and leverage while no
/// position is open on it (Binance refuses the change otherwise; then the settings are verified and a mismatch
/// warned). Remembered per agent and setting; forgotten when the exchange reports a leverage change or refuses an
/// order for the position mode.
/// </summary>
internal sealed class FuturesAccountSetup
{
    public const string HedgeModeGuidance = "Hedge mode is enabled on this Binance Futures account; switch it to one-way position mode in the Binance Futures preferences (Position Mode) and start the agent again.";

    private readonly ConcurrentDictionary<Guid, (int Leverage, MarginType Margin)> _ready = new();
    private readonly ILogger<FuturesAccountSetup> _logger;

    public FuturesAccountSetup(ILogger<FuturesAccountSetup> logger)
    {
        _logger = logger;
    }

    public async Task EnsureAsync(LiveAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Futures is not { } futures)
            return;
        var agent = account.Agent;
        var leverage = Math.Max(1, agent.Leverage ?? 1);
        var margin = agent.MarginType ?? MarginType.Isolated;
        if (_ready.TryGetValue(agent.Id, out var done) && done == (leverage, margin))
            return;

        var mode = await futures.GetPositionModeAsync(cancellationToken).ConfigureAwait(false);
        if (mode == PositionMode.Hedge)
            throw new ExecutionFaultException(HedgeModeGuidance);

        var positions = await futures.GetPositionsAsync(agent.Symbol, cancellationToken).ConfigureAwait(false);
        var open = positions.FirstOrDefault(p => p.IsOpen);
        if (open is not null)
        {
            if (open.Leverage != leverage || open.MarginType != margin)
                _logger.LogWarning("Agent {AgentId} has an open {Symbol} position at {Leverage}x {Margin}; its own {Wanted}x {WantedMargin} apply once the symbol is flat", agent.Id, agent.Symbol, open.Leverage, open.MarginType, leverage, margin);
        }
        else
        {
            await futures.SetMarginTypeAsync(agent.Symbol, margin, cancellationToken).ConfigureAwait(false);
            var applied = await futures.SetLeverageAsync(agent.Symbol, leverage, cancellationToken).ConfigureAwait(false);
            if (applied != leverage)
                throw new ExecutionFaultException($"Binance applied leverage {applied}x instead of {leverage}x on {agent.Symbol}; set a leverage the symbol allows and start the agent again.");
        }

        _ready[agent.Id] = (leverage, margin);
        _logger.LogInformation("Agent {AgentId} futures account ready: one-way mode, {Symbol} at {Leverage}x {Margin}", agent.Id, agent.Symbol, leverage, margin);
    }

    public void Invalidate(Guid agentId) => _ready.TryRemove(agentId, out _);

    /// <summary>-4061: the order's position side does not match the account's mode, which means hedge mode was switched on after the check.</summary>
    public static bool IsHedgeModeError(Exception exception) =>
        exception is ExchangeException { Kind: ExchangeErrorKind.IncorrectState, ExchangeCode: -4061 };
}
