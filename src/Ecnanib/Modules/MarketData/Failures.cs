using System.Text.RegularExpressions;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.MarketData.Contracts;

namespace Ecnanib.Modules.MarketData;

internal static partial class Failures
{
    /// <summary>Binance's code for a symbol it doesn't know.</summary>
    private const int InvalidSymbol = -1121;

    /// <summary>The adapter's failure in Market Data's terms, because the modules that use this one can't see the adapter.</summary>
    public static MarketDataException From(ExchangeException exception) => exception switch
    {
        { Failure: ExchangeFailure.Rejected, Code: InvalidSymbol } =>
            new MarketDataException(MarketDataFailure.UnknownSymbol, exception.Message, innerException: exception),
        { Failure: ExchangeFailure.Throttled or ExchangeFailure.Banned } =>
            new MarketDataException(MarketDataFailure.Throttled, exception.Message, exception.RetryAfter, exception),
        _ => new MarketDataException(MarketDataFailure.Unavailable, exception.Message, exception.RetryAfter, exception),
    };

    /// <summary>The shape of a Binance symbol, such as BTCUSDT.</summary>
    public static bool IsSymbol(string? symbol) => symbol is not null && SymbolShape().IsMatch(symbol);

    [GeneratedRegex("^[A-Z0-9]{2,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex SymbolShape();
}
