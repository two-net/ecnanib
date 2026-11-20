using Ecnanib.Shared;

namespace Ecnanib.Platform.Settings;

/// <summary>The <c>Experience</c> section: lessons, strength scores, shadow trades, and the platform-wide board.</summary>
/// <param name="LessonsPerPrompt">The most recent lessons a prompt may include (FR-EXP-7).</param>
/// <param name="MinSamples">
/// The sample size below which a slice shows "not enough data" (FR-EXP-4). A track record of
/// fewer trades is marked as a small sample (FR-EXP-11), and Live data is enough to go by from
/// this many samples on (FR-EXP-9).
/// </param>
/// <param name="Damping">The constant that damps a strength score toward zero for small samples (FR-EXP-4).</param>
/// <param name="ShadowFeePercent">The standard fees that a shadow trade is charged (FR-EXP-5).</param>
/// <param name="Board">When a slice may appear on the platform-wide board.</param>
public sealed record ExperienceSettings(
    int LessonsPerPrompt,
    int MinSamples,
    int Damping,
    ShadowFeeSettings ShadowFeePercent,
    ExperienceBoardSettings Board)
{
    internal static ExperienceSettings Read(SettingsReader file) =>
        new(
            file.WholeNumber("Experience:LessonsPerPrompt", minimum: 0),
            file.WholeNumber("Experience:MinSamples"),
            file.WholeNumber("Experience:Damping", minimum: 0),
            ShadowFeeSettings.Read(file),
            new ExperienceBoardSettings(
                file.WholeNumber("Experience:Board:MinUsers"),
                file.WholeNumber("Experience:Board:MinTrades")));
}

/// <summary>
/// The standard fees of a shadow trade (FR-EXP-5): what an order costs on each market, in percent
/// of what it is worth. A shadow trade is charged it twice, for its entry and for its exit. See
/// "Shadow trades" in docs/experience.md.
/// </summary>
/// <remarks>
/// A shadow trade has no execution that says a fee, so the rate is a setting. The tracked file
/// has what Binance charges an account with no discount for an order that takes: a stop-loss
/// does, and so does an entry at the market.
/// </remarks>
/// <param name="Spot">The fee of an order on the Spot market, in percent.</param>
/// <param name="Futures">The fee of an order on the Futures market, in percent.</param>
public sealed record ShadowFeeSettings(decimal Spot, decimal Futures)
{
    // A fee is no negative number, and one above this is a mistake in the file.
    private const decimal Least = 0m;
    private const decimal Most = 1m;

    /// <summary>The fee of an order on a market, as the fraction of what the order is worth: 0.001 for 0.1 percent.</summary>
    public decimal RateOf(Market market) => market switch
    {
        Market.Spot => Spot / 100m,
        Market.Futures => Futures / 100m,
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, "The market has no standard fee."),
    };

    internal static ShadowFeeSettings Read(SettingsReader file) =>
        new(
            file.Number("Experience:ShadowFeePercent:Spot", Least, Most),
            file.Number("Experience:ShadowFeePercent:Futures", Least, Most));
}

/// <summary>The fewest users and trades that must contribute to a slice of the board (FR-EXP-10).</summary>
public sealed record ExperienceBoardSettings(int MinUsers, int MinTrades);
