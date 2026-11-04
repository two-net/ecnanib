using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ecnanib.Modules.ExchangeAdapter;

/// <summary>
/// Binance's JSON. Prices and quantities arrive as strings, so they're read from strings into decimal,
/// never through a binary floating-point type (DATA-R-02).
/// </summary>
internal static class BinanceJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = false,
    };

    /// <exception cref="JsonException">The body isn't what the endpoint returns.</exception>
    public static T Read<T>(byte[] body) =>
        JsonSerializer.Deserialize<T>(body, Options) ?? throw new JsonException($"Binance returned null for {typeof(T).Name}.");
}

/// <summary>The body of an error response.</summary>
internal sealed class ErrorBody
{
    public int? Code { get; init; }

    public string? Msg { get; init; }
}

internal sealed class ServerTimeBody
{
    public long ServerTime { get; init; }
}

internal sealed class ExchangeInfoBody
{
    public long ServerTime { get; init; }

    public List<RateLimitBody> RateLimits { get; init; } = [];

    public List<SymbolBody> Symbols { get; init; } = [];
}

internal sealed class RateLimitBody
{
    public string RateLimitType { get; init; } = "";

    public string Interval { get; init; } = "";

    public int IntervalNum { get; init; }

    public int Limit { get; init; }
}

internal sealed class SymbolBody
{
    public string Symbol { get; init; } = "";

    public string Status { get; init; } = "";

    public string BaseAsset { get; init; } = "";

    public string QuoteAsset { get; init; } = "";

    public string? ContractType { get; init; }

    public List<string> OrderTypes { get; init; } = [];

    /// <summary>Each filter has its own fields, named by filterType.</summary>
    public List<JsonElement> Filters { get; init; } = [];
}

internal sealed class SpotAccountBody
{
    public bool CanTrade { get; init; }

    public List<SpotBalanceBody> Balances { get; init; } = [];
}

internal sealed class SpotBalanceBody
{
    public string Asset { get; init; } = "";

    public decimal Free { get; init; }

    public decimal Locked { get; init; }
}

internal sealed class FuturesAccountBody
{
    public decimal TotalMarginBalance { get; init; }

    public decimal AvailableBalance { get; init; }

    public List<FuturesAssetBody> Assets { get; init; } = [];
}

internal sealed class FuturesAssetBody
{
    public string Asset { get; init; } = "";

    public decimal WalletBalance { get; init; }

    public decimal AvailableBalance { get; init; }
}

/// <summary>An order on Spot or Futures, from placing, querying or cancelling it. Fields one market lacks stay null.</summary>
internal sealed class OrderBody
{
    public string Symbol { get; init; } = "";

    public long OrderId { get; init; }

    public string ClientOrderId { get; init; } = "";

    public string Status { get; init; } = "";

    public string Type { get; init; } = "";

    public string Side { get; init; } = "";

    public decimal? Price { get; init; }

    public decimal OrigQty { get; init; }

    public decimal ExecutedQty { get; init; }

    /// <summary>Spot, with Binance's spelling.</summary>
    public decimal? CummulativeQuoteQty { get; init; }

    /// <summary>Futures.</summary>
    public decimal? AvgPrice { get; init; }

    public decimal? StopPrice { get; init; }

    public long? UpdateTime { get; init; }

    public long? TransactTime { get; init; }
}

/// <summary>A Futures conditional order, from Binance's algo order service.</summary>
internal sealed class AlgoOrderBody
{
    public long AlgoId { get; init; }

    public string ClientAlgoId { get; init; } = "";

    public string Symbol { get; init; } = "";

    public string Side { get; init; } = "";

    public string OrderType { get; init; } = "";

    public string AlgoStatus { get; init; } = "";

    public decimal Quantity { get; init; }

    public decimal? TriggerPrice { get; init; }

    public decimal? Price { get; init; }

    /// <summary>The order it became once triggered. Binance documents it as a string, so either form is read.</summary>
    public JsonElement? ActualOrderId { get; init; }

    public decimal? ActualQty { get; init; }

    public decimal? ActualPrice { get; init; }

    public long? UpdateTime { get; init; }

    public long? CreateTime { get; init; }
}
