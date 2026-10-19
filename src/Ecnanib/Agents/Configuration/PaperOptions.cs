using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Agents.Configuration;

/// <summary>
/// The <c>Paper</c> section (Appendix D "paper"): the simulated account every paper agent starts with and the fee and
/// slippage rates applied to simulated fills (FR-AGT-11). Percentages: 0.1 means 0.1%.
/// </summary>
public sealed class PaperOptions
{
    public const string SectionName = "Paper";

    /// <summary>Quote-asset balance each paper agent starts with; its equity is this plus its realized paper P&amp;L.</summary>
    public decimal StartingBalance { get; set; } = 10_000m;

    public decimal SpotMakerFeePercent { get; set; } = 0.1m;

    public decimal SpotTakerFeePercent { get; set; } = 0.1m;

    public decimal FuturesMakerFeePercent { get; set; } = 0.02m;

    public decimal FuturesTakerFeePercent { get; set; } = 0.05m;

    /// <summary>Applied against the trader on market and stop fills: a buy fills this much above the price, a sell this much below.</summary>
    public decimal SlippagePercent { get; set; } = 0.05m;

    /// <summary>Fee as a fraction of notional for a fill that rested on the book (limit entries and targets).</summary>
    public decimal MakerFeeRate(MarketType market) => (market == MarketType.Spot ? SpotMakerFeePercent : FuturesMakerFeePercent) / 100m;

    /// <summary>Fee as a fraction of notional for a fill that took liquidity (market entries, stops, closes).</summary>
    public decimal TakerFeeRate(MarketType market) => (market == MarketType.Spot ? SpotTakerFeePercent : FuturesTakerFeePercent) / 100m;

    public decimal SlippageRate => SlippagePercent / 100m;
}
