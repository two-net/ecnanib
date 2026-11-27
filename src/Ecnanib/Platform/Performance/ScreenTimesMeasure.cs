using System.Collections.Concurrent;

namespace Ecnanib.Platform.Performance;

/// <summary>
/// Counts how long the screens took to answer, by route and by minute, and says what the times
/// add up to (NFR-PRF-1). See <see cref="IScreenTimes"/>.
/// </summary>
/// <remarks>
/// <para>
/// A time is counted in a step, and not kept: for each route there is a number for each step of
/// each minute of the last 24 hours, and nothing of a request. One step ends at the target
/// exactly, so whether the target is met is counted exactly, and the percentile is the end of
/// the step that it lies in.
/// </para>
/// <para>
/// The routes are those that the application declares, so there are few. Should there ever be
/// more than <see cref="MostScreens"/>, the rest are counted together: what a route is called
/// can never make this grow without end.
/// </para>
/// </remarks>
internal sealed class ScreenTimesMeasure(TimeProvider clock) : IScreenTimes
{
    /// <summary>The most routes that are counted on their own.</summary>
    public const int MostScreens = 128;

    private const int MinutesOfAnHour = 60;
    private const int MinutesOfADay = 24 * 60;

    // Where each step ends, in milliseconds. A time is counted in the first step that it is no
    // longer than, and one that is longer than the last is counted beyond them all.
    private static readonly TimeSpan[] Ends =
    [
        .. new[] { 5, 10, 20, 30, 40, 50, 75, 100, 150, 200, 250, 300, 400, 500, 750, 1000, 1500, 2000, 3000, 5000, 10_000, 30_000, 60_000 }
            .Select(milliseconds => TimeSpan.FromMilliseconds(milliseconds)),
    ];

    // The steps, and one more for what is beyond the last of them.
    private static readonly int Steps = Ends.Length + 1;

    // The step that ends at the target: an answer in it, or in one before it, is within the target.
    private static readonly int TargetStep = Array.IndexOf(Ends, ScreenTarget.Within);

    private readonly DateTimeOffset _since = clock.GetUtcNow();

    private readonly ConcurrentDictionary<string, Minutes> _screens = new(StringComparer.Ordinal);

    // The screens beyond the most that are counted on their own.
    private readonly Minutes _others = new();

    /// <summary>Counts one answer of a screen.</summary>
    /// <param name="route">The route of the screen, as the application declares it.</param>
    /// <param name="took">How long the server took to answer.</param>
    public void Count(string route, TimeSpan took) => MinutesOf(route).Count(MinuteNow(), StepOf(took));

    public ScreenTimesReading Read()
    {
        var now = MinuteNow();
        var screens = new List<ScreenTimes>();
        var (allOfHour, allOfDay) = (new long[Steps], new long[Steps]);

        foreach (var (route, minutes) in _screens.OrderBy(screen => screen.Key, StringComparer.Ordinal))
        {
            Add(route, minutes);
        }

        Add(route: null, _others);

        return new ScreenTimesReading(_since, new ScreenTimes(null, Of(allOfHour), Of(allOfDay)), screens);

        void Add(string? route, Minutes minutes)
        {
            var (ofHour, ofDay) = minutes.SumAt(now);

            if (ofDay.Sum() == 0)
            {
                return;
            }

            for (var step = 0; step < Steps; step++)
            {
                allOfHour[step] += ofHour[step];
                allOfDay[step] += ofDay[step];
            }

            screens.Add(new ScreenTimes(route, Of(ofHour), Of(ofDay)));
        }
    }

    /// <summary>What the counts of the steps add up to.</summary>
    private static AnswerTimes Of(long[] counts)
    {
        var count = counts.Sum();

        if (count == 0)
        {
            return new AnswerTimes(0, 0, null, BeyondLongest: false);
        }

        var within = counts.Take(TargetStep + 1).Sum();

        // The first step by whose end enough of the answers were given.
        var upTo = 0L;

        for (var step = 0; step < Ends.Length; step++)
        {
            upTo += counts[step];

            if (upTo * 100 >= count * ScreenTarget.Percent)
            {
                return new AnswerTimes(count, within, Ends[step], BeyondLongest: false);
            }
        }

        return new AnswerTimes(count, within, null, BeyondLongest: true);
    }

    private static int StepOf(TimeSpan took)
    {
        for (var step = 0; step < Ends.Length; step++)
        {
            if (took <= Ends[step])
            {
                return step;
            }
        }

        return Ends.Length;
    }

    private Minutes MinutesOf(string route)
    {
        if (_screens.TryGetValue(route, out var known))
        {
            return known;
        }

        // Two answers at once may both find room for the last route, and add one more than the
        // limit says. It is a bound on memory, and not a count that anything relies on.
        return _screens.Count < MostScreens ? _screens.GetOrAdd(route, static _ => new Minutes()) : _others;
    }

    /// <summary>The minute that it is now, counted from a fixed moment, by the clock of the application.</summary>
    private long MinuteNow() => clock.GetUtcNow().ToUnixTimeSeconds() / 60;

    /// <summary>
    /// The counts of one route, for each step of each minute of the last 24 hours: a ring, in
    /// which the place of a minute is taken again a day later.
    /// </summary>
    private sealed class Minutes
    {
        // Which minute each place is of, so that a place whose minute is a day old counts nothing.
        private readonly long[] _minute = new long[MinutesOfADay];

        // The counts of the steps of each place, one place after the other.
        private readonly int[] _counts = new int[MinutesOfADay * Steps];

        public void Count(long minute, int step)
        {
            var place = (int)(minute % MinutesOfADay);

            lock (_minute)
            {
                if (_minute[place] != minute)
                {
                    _minute[place] = minute;
                    Array.Clear(_counts, place * Steps, Steps);
                }

                _counts[(place * Steps) + step]++;
            }
        }

        public (long[] OfHour, long[] OfDay) SumAt(long now)
        {
            var (ofHour, ofDay) = (new long[Steps], new long[Steps]);

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

                    for (var step = 0; step < Steps; step++)
                    {
                        var count = _counts[(place * Steps) + step];

                        ofDay[step] += count;

                        if (ago < MinutesOfAnHour)
                        {
                            ofHour[step] += count;
                        }
                    }
                }
            }

            return (ofHour, ofDay);
        }
    }
}
