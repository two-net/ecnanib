using Ecnanib.Shared;

namespace Ecnanib.Experience;

/// <summary>
/// Which mode's experience something goes by when it was not told: Live data where there is
/// enough, otherwise Demo data, and it says which was used (FR-EXP-9). See "Live or Demo" in
/// docs/experience.md.
/// </summary>
/// <remarks>
/// Demo and Live are kept and scored separately, and nothing is added up over both (FR-BIN-11):
/// the rule chooses one of the two, and never mixes them. It takes sample sizes, and not trades,
/// so that whatever counts something else than trades goes by the same rule.
/// </remarks>
public static class ExperienceBasis
{
    /// <summary>The mode to go by: Live when it has the minimum sample, and Demo otherwise.</summary>
    /// <param name="liveSamples">How many samples there are in Live.</param>
    /// <param name="demoSamples">How many samples there are in Demo.</param>
    /// <param name="minSamples">How many samples are enough: <c>Experience:MinSamples</c>.</param>
    public static ModeBasis Choose(int liveSamples, int demoSamples, int minSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(liveSamples);
        ArgumentOutOfRangeException.ThrowIfNegative(demoSamples);

        return liveSamples >= minSamples
            ? new ModeBasis(TradingMode.Live, Enough: true)
            : new ModeBasis(TradingMode.Demo, Enough: demoSamples >= minSamples);
    }
}

/// <summary>The mode whose experience is gone by, which is said wherever it is used (FR-EXP-9).</summary>
/// <param name="Mode">The mode that was chosen.</param>
/// <param name="Enough">Whether that mode has the minimum sample. Demo is chosen also when neither has.</param>
public sealed record ModeBasis(TradingMode Mode, bool Enough);
