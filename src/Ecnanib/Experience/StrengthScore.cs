namespace Ecnanib.Experience;

/// <summary>
/// The strength score of a model in a role: the measure of the role, damped toward zero for a
/// small sample (FR-EXP-4). It is measure × n ÷ (n + damping), where n is the sample size. See
/// "The strength score" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is a rule only:</b> it reads nothing, and says the same for the same numbers.
/// </para>
/// <para>
/// <b>A small sample says little, so it is given little.</b> A measure of 10 samples counts for a
/// third of itself where the damping is 20, and one of 180 for nine tenths. A score keeps the
/// sign of its measure, and is never further from zero than the measure is.
/// </para>
/// </remarks>
public static class StrengthScore
{
    /// <summary>The strength score of a measure: measure × n ÷ (n + damping).</summary>
    /// <param name="measure">The measure of the role, in its own unit: see <see cref="RoleMeasures"/>.</param>
    /// <param name="samples">The sample size, n.</param>
    /// <param name="damping">The constant that damps the score: <c>Experience:Damping</c>.</param>
    public static decimal Of(decimal measure, int samples, int damping)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(samples);
        ArgumentOutOfRangeException.ThrowIfNegative(damping);

        // No sample and no damping divide nothing: a measure of no samples says nothing.
        if (samples + (long)damping == 0)
        {
            return 0m;
        }

        var dampedBy = samples + (long)damping;

        try
        {
            // As the specification writes it, so that a score is exact wherever the division is.
            return measure * samples / dampedBy;
        }
        catch (OverflowException)
        {
            // A measure near the end of the range of a number cannot be multiplied by its
            // sample. The sample over itself and the damping is 1 at the most, so the score is
            // in the range wherever the measure is.
            return measure * (samples / (decimal)dampedBy);
        }
    }

    /// <summary>
    /// Whether a sample is enough for a score to be said: a slice of fewer samples shows "not
    /// enough data", and is recommended from by nothing (FR-EXP-4).
    /// </summary>
    /// <param name="samples">The sample size, n.</param>
    /// <param name="minSamples">The sample size below which a slice has no score: <c>Experience:MinSamples</c>.</param>
    public static bool IsEnough(int samples, int minSamples) => samples >= minSamples;
}
