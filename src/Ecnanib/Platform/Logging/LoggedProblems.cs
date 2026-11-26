using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace Ecnanib.Platform.Logging;

/// <summary>
/// Counts the errors and warnings that pass the log pipeline, by area and by minute, and says
/// what they add up to (FR-OPS-2). See <see cref="ILoggedProblems"/>.
/// </summary>
/// <remarks>
/// <para>
/// It stands in the pipeline beside the outputs, and is no output: it writes nothing anywhere,
/// and keeps a number for each minute of the last 24 hours and nothing of an event. So it holds
/// no secret, whatever is logged, and it needs no masking.
/// </para>
/// <para>
/// The area of an event is the namespace under <c>Ecnanib</c> of whatever logged it, which the
/// event carries as its source. The areas are the folders of the code, so there are few. Should
/// there ever be more than <see cref="MostAreas"/>, the rest count as outside the application:
/// what a source is called can never make this grow without end.
/// </para>
/// </remarks>
internal sealed class LoggedProblems(TimeProvider clock) : ILogEventSink, ILoggedProblems
{
    /// <summary>The most areas that are counted on their own.</summary>
    public const int MostAreas = 64;

    private const int MinutesOfAnHour = 60;
    private const int MinutesOfADay = 24 * 60;

    // What the source of an event of the application begins with.
    private const string ApplicationPrefix = "Ecnanib.";

    private readonly DateTimeOffset _since = clock.GetUtcNow();

    private readonly ConcurrentDictionary<string, Minutes> _areas = new(StringComparer.Ordinal);

    // What was logged outside the application's own namespaces.
    private readonly Minutes _outside = new();

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Warning)
        {
            return;
        }

        MinutesOf(AreaOf(logEvent)).Count(MinuteNow(), isError: logEvent.Level >= LogEventLevel.Error);
    }

    public LoggedProblemCounts Read()
    {
        var now = MinuteNow();
        var areas = new List<AreaProblems>();

        foreach (var (area, minutes) in _areas.OrderBy(area => area.Key, StringComparer.Ordinal))
        {
            Add(area, minutes);
        }

        Add(area: null, _outside);

        return new LoggedProblemCounts(_since, areas);

        void Add(string? area, Minutes minutes)
        {
            var sums = minutes.SumAt(now);

            if (sums.ErrorsLastDay > 0 || sums.WarningsLastDay > 0)
            {
                areas.Add(new AreaProblems(area, sums.ErrorsLastHour, sums.WarningsLastHour, sums.ErrorsLastDay, sums.WarningsLastDay));
            }
        }
    }

    private Minutes MinutesOf(string? area)
    {
        if (area is null)
        {
            return _outside;
        }

        if (_areas.TryGetValue(area, out var known))
        {
            return known;
        }

        // Two events at once may both find room for the last area, and add one more than the
        // limit says. It is a bound on memory, and not a count that anything relies on.
        return _areas.Count < MostAreas ? _areas.GetOrAdd(area, static _ => new Minutes()) : _outside;
    }

    /// <summary>
    /// The area of the application that logged an event: the first namespace under
    /// <c>Ecnanib</c> of its source. Null for an event of the framework or of a library.
    /// </summary>
    private static string? AreaOf(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var source)
            || source is not ScalarValue { Value: string name }
            || !name.StartsWith(ApplicationPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var end = name.IndexOf('.', ApplicationPrefix.Length);
        var area = end < 0 ? name[ApplicationPrefix.Length..] : name[ApplicationPrefix.Length..end];

        return area.Length == 0 ? null : area;
    }

    /// <summary>The minute that it is now, counted from a fixed moment, by the clock of the application.</summary>
    private long MinuteNow() => clock.GetUtcNow().ToUnixTimeSeconds() / 60;

    /// <summary>
    /// The counts of one area, for each minute of the last 24 hours: a ring, in which the place
    /// of a minute is taken again a day later.
    /// </summary>
    private sealed class Minutes
    {
        // Which minute each place is of, so that a place whose minute is a day old counts nothing.
        private readonly long[] _minute = new long[MinutesOfADay];
        private readonly int[] _errors = new int[MinutesOfADay];
        private readonly int[] _warnings = new int[MinutesOfADay];

        public void Count(long minute, bool isError)
        {
            var place = (int)(minute % MinutesOfADay);

            lock (_minute)
            {
                if (_minute[place] != minute)
                {
                    _minute[place] = minute;
                    _errors[place] = 0;
                    _warnings[place] = 0;
                }

                if (isError)
                {
                    _errors[place]++;
                }
                else
                {
                    _warnings[place]++;
                }
            }
        }

        public (int ErrorsLastHour, int WarningsLastHour, int ErrorsLastDay, int WarningsLastDay) SumAt(long now)
        {
            var (errorsOfHour, warningsOfHour, errorsOfDay, warningsOfDay) = (0, 0, 0, 0);

            lock (_minute)
            {
                for (var place = 0; place < MinutesOfADay; place++)
                {
                    // How long ago the minute of the place is. One that lies ahead, because the
                    // clock was set back since, is counted when its time comes again.
                    var ago = now - _minute[place];

                    if (ago is < 0 or >= MinutesOfADay)
                    {
                        continue;
                    }

                    errorsOfDay += _errors[place];
                    warningsOfDay += _warnings[place];

                    if (ago < MinutesOfAnHour)
                    {
                        errorsOfHour += _errors[place];
                        warningsOfHour += _warnings[place];
                    }
                }
            }

            return (errorsOfHour, warningsOfHour, errorsOfDay, warningsOfDay);
        }
    }
}
