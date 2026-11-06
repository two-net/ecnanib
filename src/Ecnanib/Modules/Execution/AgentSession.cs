using System.Text.Json;
using System.Text.Json.Serialization;
using Ecnanib.Kernel;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.MarketData.Contracts;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// What one pass over an agent's orders needs: its exchange key, opened for this pass only and disposed after
/// (FR-KEY-09), and its pair's filters. An agent's market, environment, pair and key can't change while it has orders
/// (FR-AGT-03), so every order of it uses these.
/// </summary>
internal sealed class AgentSession(
    Guid userId,
    Guid agentId,
    Guid credentialId,
    Market market,
    TradingEnvironment environment,
    ExchangeKey key,
    ExchangeSymbolInfo symbol) : IDisposable
{
    public Guid UserId { get; } = userId;

    public Guid AgentId { get; } = agentId;

    public Guid CredentialId { get; } = credentialId;

    public Market Market { get; } = market;

    public TradingEnvironment Environment { get; } = environment;

    public ExchangeKey Key { get; } = key;

    /// <summary>The pair, with its filters as Ecnanib last read them. <see cref="RefreshSymbolAsync"/> reads them again.</summary>
    public ExchangeSymbolInfo Symbol { get; private set; } = symbol;

    public bool Futures => Market == Market.Futures;

    /// <summary>After Binance rejected an order on a filter (FR-RISK-04).</summary>
    public async Task RefreshSymbolAsync(IExchangeSymbols symbols, CancellationToken cancellationToken)
    {
        await symbols.RefreshAsync(Market, Environment, cancellationToken);
        Symbol = await symbols.FindAsync(Market, Environment, Symbol.Symbol, cancellationToken) ?? Symbol;
    }

    public void Dispose()
    {
        Key.ApiKey.Dispose();
        Key.Secret.Dispose();
    }
}

/// <summary>Opens <see cref="AgentSession"/>s.</summary>
internal sealed class AgentSessions(ICredentialKeys keys, IExchangeSymbols symbols)
{
    /// <exception cref="AgentSessionException">The key can't be used, or Binance no longer lists the pair.</exception>
    public async Task<AgentSession> OpenAsync(
        Guid userId,
        Guid agentId,
        Guid credentialId,
        Market market,
        TradingEnvironment environment,
        string symbol,
        CancellationToken cancellationToken)
    {
        var info = await symbols.FindAsync(market, environment, symbol, cancellationToken)
            ?? throw new AgentSessionException(AgentSessionFailure.SymbolMissing, $"Binance no longer lists {symbol}.");

        ExchangeKey key;

        try
        {
            key = await keys.OpenExchangeKeyAsync(userId, credentialId, market, cancellationToken);
        }
        catch (CredentialUnusableException exception)
        {
            throw new AgentSessionException(AgentSessionFailure.KeyUnusable, $"Exchange key {credentialId} can't be used: {exception.Reason}", exception);
        }

        return new AgentSession(userId, agentId, credentialId, market, environment, key, info);
    }
}

internal enum AgentSessionFailure
{
    KeyUnusable,
    SymbolMissing,
}

internal sealed class AgentSessionException(AgentSessionFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public AgentSessionFailure Failure { get; } = failure;
}

/// <summary>Execution's JSON: enums by name, as the decision trace writes them.</summary>
internal static class ExecutionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
