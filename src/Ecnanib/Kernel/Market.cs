namespace Ecnanib.Kernel;

/// <summary>
/// A Binance market (CON-12). The exchange adapter, credentials, agents and orders all use it. Rows
/// will store the name, so a value is never renamed.
/// </summary>
public enum Market
{
    Spot,

    /// <summary>USDⓈ-M Futures.</summary>
    Futures,
}
