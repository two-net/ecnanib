namespace Ecnanib.Modules.Experience;

/// <summary>
/// The bands a call's latency is counted in, so a period's median and 95th percentile can be read from the sum of its
/// days (FR-EXP-02). Each band's upper edge is a quarter above the last, from 100 ms to about 8 minutes, and the last
/// band takes everything slower. A percentile is the upper edge of the band it falls in, so it's at most a quarter too
/// high. Stored counts are by position, so the edges never change.
/// </summary>
internal static class LatencyBands
{
    public const int Count = 40;

    /// <summary>The upper edge of every band but the last, in milliseconds.</summary>
    private static readonly long[] Edges = [.. Enumerable.Range(0, Count - 1).Select(band => (long)Math.Round(100 * Math.Pow(1.25, band)))];

    public static long[] Empty() => new long[Count];

    public static int Of(TimeSpan latency)
    {
        var milliseconds = (long)Math.Ceiling(latency.TotalMilliseconds);
        var band = Array.BinarySearch(Edges, milliseconds);

        return band >= 0 ? band : ~band;
    }

    /// <summary>
    /// The latency below which <paramref name="fraction"/> of the counted calls fell, at its band's upper edge, or null
    /// with none counted. The last band has no upper edge, so it gives its lower one.
    /// </summary>
    public static TimeSpan? Percentile(IReadOnlyList<long> counts, decimal fraction)
    {
        var total = counts.Sum();

        if (total == 0)
        {
            return null;
        }

        var rank = (long)Math.Ceiling(total * fraction);
        var seen = 0L;

        for (var band = 0; band < counts.Count; band++)
        {
            seen += counts[band];

            if (seen >= rank)
            {
                return TimeSpan.FromMilliseconds(Edges[Math.Min(band, Edges.Length - 1)]);
            }
        }

        return TimeSpan.FromMilliseconds(Edges[^1]);
    }

    /// <summary>Adds <paramref name="counts"/> into <paramref name="total"/>, which a row from an older build may not match in length.</summary>
    public static void Add(long[] total, IReadOnlyList<long> counts)
    {
        for (var band = 0; band < Math.Min(total.Length, counts.Count); band++)
        {
            total[band] += counts[band];
        }
    }
}
