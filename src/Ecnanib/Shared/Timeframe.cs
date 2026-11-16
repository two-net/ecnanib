namespace Ecnanib.Shared;

/// <summary>
/// The candle length that an agent works on (FR-AGT-3). It is chosen for each agent, and is what
/// the agent's cycles, its data, and its prompts go by. A member is stored by its name, so
/// renaming one changes stored data and needs a migration.
/// </summary>
public enum Timeframe
{
    /// <summary>15 minutes: <c>15m</c>.</summary>
    FifteenMinutes,

    /// <summary>1 hour: <c>1H</c>.</summary>
    OneHour,

    /// <summary>4 hours: <c>4H</c>.</summary>
    FourHours,

    /// <summary>1 day: <c>1D</c>.</summary>
    OneDay,
}

/// <summary>
/// The codes of the timeframes, as the specification and the configuration file write them:
/// <c>15m</c>, <c>1H</c>, <c>4H</c>, and <c>1D</c>. A prompt for a timeframe is stored under its
/// code, and so is its stale limit in the file.
/// </summary>
public static class Timeframes
{
    /// <summary>The four timeframes, shortest first.</summary>
    public static IReadOnlyList<Timeframe> All { get; } = Enum.GetValues<Timeframe>();

    /// <summary>The code of a timeframe.</summary>
    public static string Code(this Timeframe timeframe) => timeframe switch
    {
        Timeframe.FifteenMinutes => "15m",
        Timeframe.OneHour => "1H",
        Timeframe.FourHours => "4H",
        Timeframe.OneDay => "1D",
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "The timeframe has no code."),
    };

    /// <summary>The timeframe that a code names, or null when it names none. A code is written as it is: <c>1H</c>, and not <c>1h</c>.</summary>
    public static Timeframe? WithCode(string? code) =>
        All.Cast<Timeframe?>().SingleOrDefault(timeframe => string.Equals(timeframe!.Value.Code(), code, StringComparison.Ordinal));

    /// <summary>
    /// How long a candle of a timeframe lasts. Each length divides a day into equal parts, and is
    /// a multiple of the shortest, so every close of a longer timeframe is a close of the
    /// shortest as well.
    /// </summary>
    public static TimeSpan Length(this Timeframe timeframe) => timeframe switch
    {
        Timeframe.FifteenMinutes => TimeSpan.FromMinutes(15),
        Timeframe.OneHour => TimeSpan.FromHours(1),
        Timeframe.FourHours => TimeSpan.FromHours(4),
        Timeframe.OneDay => TimeSpan.FromDays(1),
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "The timeframe has no length."),
    };

    /// <summary>
    /// The timeframes of which a candle closes at a time, shortest first (FR-CYC-1). Candles
    /// close on UTC boundaries: at every multiple of the length of the timeframe since midnight
    /// in UTC. So a quarter hour closes a candle of 15 minutes, the hour one of an hour as well,
    /// and midnight one of each. A time between two quarter hours closes none.
    /// </summary>
    public static IReadOnlyList<Timeframe> ClosingAt(DateTimeOffset time) =>
        [.. All.Where(timeframe => time.UtcTicks % timeframe.Length().Ticks == 0)];
}
