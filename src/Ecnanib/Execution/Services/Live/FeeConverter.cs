using Ecnanib.Exchange.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Execution.Services.Live;

/// <summary>
/// Fees in the quote asset (FR-EXE-12): as reported when charged in the quote asset, converted at the fill price when
/// charged in the base asset (a spot buy), through the public price of <c>FEEASSET+quote</c> for any other asset (BNB
/// discounts), and 0 with a warning when no such pair exists.
/// </summary>
internal sealed class FeeConverter
{
    private readonly LiveExchange _exchange;
    private readonly ILogger<FeeConverter> _logger;

    public FeeConverter(LiveExchange exchange, ILogger<FeeConverter> logger)
    {
        _exchange = exchange;
        _logger = logger;
    }

    public async Task<decimal> ToQuoteAsync(UserTrade fill, LiveAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fill);
        ArgumentNullException.ThrowIfNull(account);
        if (fill.Fee == 0m)
            return 0m;
        var asset = fill.FeeAsset.ToUpperInvariant();
        if (asset.Length == 0 || asset == account.Symbol.QuoteAsset.ToUpperInvariant())
            return fill.Fee;
        if (asset == account.Symbol.BaseAsset.ToUpperInvariant())
            return fill.Fee * fill.Price;
        var price = await _exchange.PriceAsync(account.Environment, account.Market, asset + account.Symbol.QuoteAsset, cancellationToken).ConfigureAwait(false);
        if (price is > 0)
            return fill.Fee * price.Value;
        _logger.LogWarning("A fee of {Fee} {Asset} on {Symbol} could not be converted to {Quote}; it is recorded as 0", fill.Fee, asset, fill.Symbol, account.Symbol.QuoteAsset);
        return 0m;
    }
}
