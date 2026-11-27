namespace Ecnanib.Platform.Performance;

/// <summary>
/// How long the server took to answer the screens that it showed lately, held against the
/// target: typical screens respond within 300 ms at the 95th percentile, measured at the server
/// (NFR-PRF-1). It is for the operator status screen. See "Screen response times" in
/// docs/performance.md.
/// </summary>
/// <remarks>
/// The times are kept in memory, by this instance, of the screens that it answered itself since
/// it started. Nothing of them is written to the database (NFR-DAT-7), and a restart begins them
/// again. A time holds nothing of a request: a screen is told by its route as the application
/// declares it, and never by the address that was asked for, so no id of an agent or of anything
/// else of a user is kept (FR-OPS-5).
/// </remarks>
public interface IScreenTimes
{
    /// <summary>
    /// The times now: of the last hour and of the last 24 hours, each to the minute, for all
    /// screens together and for every screen that was shown in that time.
    /// </summary>
    ScreenTimesReading Read();
}

/// <summary>What the response time of a screen is held to (NFR-PRF-1).</summary>
public static class ScreenTarget
{
    /// <summary>How many answers of a hundred are to be within <see cref="Within"/>: the 95th percentile.</summary>
    public const int Percent = 95;

    /// <summary>How long an answer may take at the most, for <see cref="Percent"/> of a hundred.</summary>
    public static readonly TimeSpan Within = TimeSpan.FromMilliseconds(300);
}

/// <summary>The response times of the screens that this instance answered lately.</summary>
/// <param name="MeasuredSince">
/// Since when this instance measures: when it started. Where that is less than a day ago, or less
/// than an hour, a figure is of the time since then.
/// </param>
/// <param name="All">Every screen together. Its route is null.</param>
/// <param name="Screens">
/// The screens that were shown in the last 24 hours, by route. One that was not shown is not
/// among them. Should there ever be more routes than are told apart, the rest are one entry, the
/// last, whose route is null.
/// </param>
public sealed record ScreenTimesReading(DateTimeOffset MeasuredSince, ScreenTimes All, IReadOnlyList<ScreenTimes> Screens);

/// <summary>The response times of one screen, or of several together.</summary>
/// <param name="Route">
/// The route of the screen as the application declares it, such as <c>/agents/{Id:guid}/state</c>,
/// with the names of its parameters and none of their values. Null for several screens together.
/// </param>
/// <param name="LastHour">The answers of the last 60 minutes.</param>
/// <param name="LastDay">The answers of the last 24 hours.</param>
public sealed record ScreenTimes(string? Route, AnswerTimes LastHour, AnswerTimes LastDay);

/// <summary>How long the answers of a span of time took.</summary>
/// <param name="Count">How many times a screen was shown.</param>
/// <param name="WithinTarget">How many of them were answered within <see cref="ScreenTarget.Within"/>, counted exactly.</param>
/// <param name="Percentile">
/// The 95th percentile: <see cref="ScreenTarget.Percent"/> of a hundred answers took no longer
/// than this. Times are counted in steps, so it is the end of the step that the percentile lies
/// in: the answers took that long at the most, and may have taken less. Null when nothing was
/// shown, and when the percentile lies beyond the longest time that is told apart, which
/// <see cref="BeyondLongest"/> says.
/// </param>
/// <param name="BeyondLongest">Whether the percentile lies beyond the longest time that is told apart, which is a minute.</param>
public sealed record AnswerTimes(long Count, long WithinTarget, TimeSpan? Percentile, bool BeyondLongest)
{
    /// <summary>
    /// Whether the answers are at the target: <see cref="ScreenTarget.Percent"/> of a hundred or
    /// more were within <see cref="ScreenTarget.Within"/>. Null when nothing was shown: it is not
    /// known then, and is neither met nor missed.
    /// </summary>
    public bool? MeetsTarget => Count == 0 ? null : WithinTarget * 100 >= Count * ScreenTarget.Percent;
}
