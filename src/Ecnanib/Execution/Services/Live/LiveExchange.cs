using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Execution.Services.Live;

/// <summary>An agent's exchange access for one call: its key, the spot or futures client and the symbol's filters.</summary>
internal sealed record LiveAccount(Agent Agent, ExchangeKeyHandle Key, ISpotAccountClient? Spot, IFuturesAccountClient? Futures, SymbolInfo Symbol)
{
    public IExchangeAccountClient Client => (IExchangeAccountClient?)Spot ?? Futures!;

    public ExchangeEnvironment Environment => Key.Environment;

    public MarketType Market => Agent.MarketType;

    public bool IsFutures => Futures is not null;
}

/// <summary>
/// Resolves a live agent's key (FR-KEY-05, AI-03) and symbol into the clients the venue calls. A key that no longer
/// resolves or a symbol that left the listing is <see cref="ExecutionUnavailableException"/>: the action fails and the
/// agent's state stands, as a Stop reports it today.
/// </summary>
internal sealed class LiveExchange
{
    private readonly IExchangeClientFactory _factory;
    private readonly IKeyResolver _keys;
    private readonly ISymbolCatalog _symbols;
    private readonly ILogger<LiveExchange> _logger;

    public LiveExchange(IExchangeClientFactory factory, IKeyResolver keys, ISymbolCatalog symbols, ILogger<LiveExchange> logger)
    {
        _factory = factory;
        _keys = keys;
        _symbols = symbols;
        _logger = logger;
    }

    public async Task<LiveAccount> ForAsync(Agent agent, ExchangeKeyHandle? handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        handle ??= await ResolveAsync(agent, cancellationToken).ConfigureAwait(false);
        var symbol = await _symbols.FindAsync(handle.Environment, agent.MarketType, agent.Symbol, cancellationToken).ConfigureAwait(false)
            ?? throw new ExecutionUnavailableException($"{agent.Symbol} is not listed on {agent.MarketType} {handle.Environment}.");
        return agent.MarketType == MarketType.Spot
            ? new LiveAccount(agent, handle, _factory.GetSpotClient(handle), null, symbol)
            : new LiveAccount(agent, handle, null, _factory.GetFuturesClient(handle), symbol);
    }

    public async Task<ExchangeKeyHandle> ResolveAsync(Agent agent, CancellationToken cancellationToken)
    {
        if (agent.Mode != AgentMode.Live)
            throw new ExecutionUnavailableException("The agent is not a live agent.");
        if (agent.ExchangeKeyId is not { } keyId)
            throw new ExecutionUnavailableException("The agent has no Binance key.");
        var resolution = await _keys.ResolveExchangeKeyAsync(agent.UserId, keyId, agent.MarketType, cancellationToken).ConfigureAwait(false);
        if (!resolution.IsResolved || resolution.Handle is null)
            throw new ExecutionUnavailableException(resolution.Reason ?? "The agent's Binance key cannot be used.");
        return resolution.Handle;
    }

    /// <summary>The last public price of a symbol, null when it cannot be read (a fee asset without a quote pair).</summary>
    public async Task<decimal?> PriceAsync(ExchangeEnvironment environment, MarketType market, string symbol, CancellationToken cancellationToken)
    {
        try
        {
            var tick = await _factory.GetPublicData(environment, market).GetPriceAsync(symbol, cancellationToken).ConfigureAwait(false);
            return tick.Price > 0 ? tick.Price : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The price of {Symbol} on {Market} {Environment} could not be read", symbol, market, environment);
            return null;
        }
    }
}
