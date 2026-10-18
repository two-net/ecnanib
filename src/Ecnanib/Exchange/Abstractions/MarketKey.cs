namespace Ecnanib.Exchange.Abstractions;

/// <summary>A symbol on one market in one environment, e.g. BTCUSDT on production USD-M futures.</summary>
public readonly record struct MarketKey(ExchangeEnvironment Environment, MarketType Market, string Symbol)
{
    public string Symbol { get; } = !string.IsNullOrWhiteSpace(Symbol)
        ? Symbol.Trim().ToUpperInvariant()
        : throw new ArgumentException("Symbol is required.", nameof(Symbol));

    public override string ToString() => $"{Environment}/{Market}/{Symbol}";
}
