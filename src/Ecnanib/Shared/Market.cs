namespace Ecnanib.Shared;

/// <summary>The Binance market an agent trades on.</summary>
public enum Market
{
    /// <summary>Buying and selling the asset itself.</summary>
    Spot,

    /// <summary>USDⓈ-margined perpetual contracts.</summary>
    Futures,
}
