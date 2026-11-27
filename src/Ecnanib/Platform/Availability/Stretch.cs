namespace Ecnanib.Platform.Availability;

/// <summary>
/// A stretch of time: from a moment, which is of it, to a later one, which is not. It is what
/// the availability of a month is worked out from: when the service was up, and when maintenance
/// was announced for. See <see cref="MonthAvailability.Of"/>.
/// </summary>
/// <param name="From">Its first moment.</param>
/// <param name="Until">The moment that it ends before.</param>
public readonly record struct Stretch(DateTimeOffset From, DateTimeOffset Until)
{
    /// <summary>How long it is, and no time when it ends before it begins.</summary>
    public TimeSpan Length => Until > From ? Until - From : TimeSpan.Zero;
}

/// <summary>
/// What the measure of availability does with stretches of time: it puts them together, cuts
/// them to a month, and takes one kind of them out of another. See <see cref="MonthAvailability"/>.
/// </summary>
internal static class Stretches
{
    /// <summary>
    /// The same time as stretches that follow each other with time between them: those that
    /// overlap or touch are one, and one of no length is left out.
    /// </summary>
    public static IReadOnlyList<Stretch> Joined(IEnumerable<Stretch> stretches)
    {
        var joined = new List<Stretch>();

        foreach (var next in stretches.Where(stretch => stretch.Until > stretch.From).OrderBy(stretch => stretch.From))
        {
            if (joined.Count > 0 && next.From <= joined[^1].Until)
            {
                if (next.Until > joined[^1].Until)
                {
                    joined[^1] = joined[^1] with { Until = next.Until };
                }
            }
            else
            {
                joined.Add(next);
            }
        }

        return joined;
    }

    /// <summary>What of joined stretches lies in a stretch.</summary>
    public static IReadOnlyList<Stretch> Within(IReadOnlyList<Stretch> joined, Stretch within) =>
        [
            .. joined
                .Select(stretch => new Stretch(Later(stretch.From, within.From), Earlier(stretch.Until, within.Until)))
                .Where(stretch => stretch.Until > stretch.From),
        ];

    /// <summary>What of joined stretches lies in other joined stretches.</summary>
    public static IReadOnlyList<Stretch> Within(IReadOnlyList<Stretch> joined, IReadOnlyList<Stretch> within) =>
        [.. within.SelectMany(stretch => Within(joined, stretch))];

    /// <summary>What is left of a stretch without joined stretches.</summary>
    public static IReadOnlyList<Stretch> Without(Stretch whole, IReadOnlyList<Stretch> joined)
    {
        var left = new List<Stretch>();
        var from = whole.From;

        foreach (var taken in Within(joined, whole))
        {
            if (taken.From > from)
            {
                left.Add(new Stretch(from, taken.From));
            }

            from = taken.Until;
        }

        if (whole.Until > from)
        {
            left.Add(new Stretch(from, whole.Until));
        }

        return left;
    }

    /// <summary>How long stretches are together.</summary>
    public static TimeSpan Length(IEnumerable<Stretch> stretches) =>
        stretches.Aggregate(TimeSpan.Zero, (length, stretch) => length + stretch.Length);

    private static DateTimeOffset Later(DateTimeOffset one, DateTimeOffset other) => one > other ? one : other;

    private static DateTimeOffset Earlier(DateTimeOffset one, DateTimeOffset other) => one < other ? one : other;
}
