namespace Ecnanib.Kernel;

/// <summary>
/// Where an agent trades, and which Binance servers a credential belongs to (CON-12, FR-KEY-02). Demo
/// prices resemble Live ones but aren't the same (EXT-BIN-08), so the two are never mixed. Rows will
/// store the name, so a value is never renamed.
/// </summary>
public enum TradingEnvironment
{
    Demo,
    Live,
}
