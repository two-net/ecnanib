namespace Ecnanib.Experience;

/// <summary>
/// The measure of each role (FR-EXP-3): how well a model did a role, by what the role is for.
/// See "The measure of each role" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>An analyst is measured by its calls.</b> The share of its calls that were not neutral whose
/// direction matched the price change over the next 4 closed candles, minus 50 percent: a model
/// that is right half of the time is at zero, which is what a coin is at.
/// </para>
/// <para>
/// <b>The Strategist is measured by its plans.</b> The average R of the plans that were
/// executed, which are the closed trades of the positions that they opened.
/// </para>
/// <para>
/// <b>The Risk Manager is measured by what it let through and what it kept out.</b> The average
/// R of the plans that it approved, minus the average R that the plans which it vetoed would
/// have produced, which their shadow trades say (FR-EXP-5). One that vetoes the plans which
/// would have lost is above zero.
/// </para>
/// <para>
/// Each is a rule only: it reads nothing. Demo and Live are scored separately (FR-EXP-9):
/// whoever asks hands over the samples of one mode.
/// </para>
/// </remarks>
public static class RoleMeasures
{
    /// <summary>What is taken from the share of matched calls: a model that is right half of the time is at zero.</summary>
    public const decimal Chance = 0.5m;

    /// <summary>
    /// The measure of an analyst: the share of its scored calls that matched, minus a half, as a
    /// fraction from -0.5 to 0.5. A call that was neutral is no sample, and is not handed over.
    /// </summary>
    /// <param name="matched">How many calls matched the price change.</param>
    /// <param name="missed">How many did not.</param>
    public static RoleMeasure OfAnalyst(int matched, int missed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(matched);
        ArgumentOutOfRangeException.ThrowIfNegative(missed);

        var calls = matched + (long)missed;

        return new RoleMeasure(calls == 0 ? null : (matched / (decimal)calls) - Chance, (int)Math.Min(calls, int.MaxValue));
    }

    /// <summary>The measure of the Strategist: the average R of its executed plans.</summary>
    /// <param name="resultsInR">The result in R of each closed trade that a plan of the model opened.</param>
    public static RoleMeasure OfStrategist(IReadOnlyCollection<decimal> resultsInR)
    {
        ArgumentNullException.ThrowIfNull(resultsInR);

        return new RoleMeasure(Mean(resultsInR), resultsInR.Count);
    }

    /// <summary>
    /// The measure of the Risk Manager: the average R of the plans that it approved, minus the
    /// average R that the plans which it vetoed would have produced. Its sample is every plan
    /// that it reviewed and that has a result, on either side.
    /// </summary>
    /// <remarks>
    /// <b>It is said only where both sides have a result.</b> Without a vetoed plan there is
    /// nothing to take away, and without an approved one nothing to take it from: the measure
    /// would be that of the Strategist, or of the market, and not of the reviews.
    /// </remarks>
    /// <param name="approvedInR">The result in R of each closed trade that an approved plan opened. A plan whose risk was reduced is approved.</param>
    /// <param name="vetoedInR">The result in R of the shadow trade of each vetoed plan.</param>
    public static RoleMeasure OfRiskManager(IReadOnlyCollection<decimal> approvedInR, IReadOnlyCollection<decimal> vetoedInR)
    {
        ArgumentNullException.ThrowIfNull(approvedInR);
        ArgumentNullException.ThrowIfNull(vetoedInR);

        var (approved, vetoed) = (Mean(approvedInR), Mean(vetoedInR));

        return new RoleMeasure(
            approved is { } passed && vetoed is { } keptOut ? Within(() => passed - keptOut) : null,
            approvedInR.Count + vetoedInR.Count,
            new ReviewedPlans(approvedInR.Count, vetoedInR.Count));
    }

    /// <summary>The mean of some results, or null for none, and where their sum is out of the range of a number.</summary>
    private static decimal? Mean(IReadOnlyCollection<decimal> results) =>
        results.Count == 0 ? null : Within(() => results.Sum() / results.Count);

    /// <summary>
    /// A number, or null where it is out of the range of one: a trade on which next to nothing
    /// was risked has a result of very many R. Nothing is said then, and nothing fails.
    /// </summary>
    private static decimal? Within(Func<decimal> number)
    {
        try
        {
            return number();
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}

/// <summary>The measure of a model in a role, from the samples of one slice (FR-EXP-3).</summary>
/// <param name="Value">
/// The measure, or null where it cannot be said: for no samples, for a Risk Manager that has
/// results on one side only, and where the results are out of the range of a number. For an
/// analyst it is a fraction, from -0.5 to 0.5. For the Strategist and the Risk Manager it is in R.
/// </param>
/// <param name="Samples">The sample size, n, which a strength score is damped by (FR-EXP-4).</param>
/// <param name="Reviewed">What the sample of a Risk Manager is made of, and null for every other role.</param>
public sealed record RoleMeasure(decimal? Value, int Samples, ReviewedPlans? Reviewed = null);

/// <summary>The plans that a Risk Manager reviewed and that have a result, on each side.</summary>
/// <param name="Approved">How many it approved: the closed trades with a result in R.</param>
/// <param name="Vetoed">How many it vetoed: the shadow trades with a result in R.</param>
public sealed record ReviewedPlans(int Approved, int Vetoed);
