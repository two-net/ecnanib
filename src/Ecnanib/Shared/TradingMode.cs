namespace Ecnanib.Shared;

/// <summary>Whether trading uses Binance's test environment or the user's real account.</summary>
public enum TradingMode
{
    /// <summary>Binance's test environment, with no real funds.</summary>
    Demo,

    /// <summary>The user's real account.</summary>
    Live,
}
