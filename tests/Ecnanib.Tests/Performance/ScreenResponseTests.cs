using Ecnanib.Platform.Performance;
using Ecnanib.Tests.Accessibility;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Layout;

namespace Ecnanib.Tests.Performance;

/// <summary>
/// Typical screens respond within 300 ms at the 95th percentile, measured at the server
/// (NFR-PRF-1). The screens are those of the specification (FR-UI-2), as the screen map lists
/// them, in an application that has something to show on each: the people of
/// <see cref="AccessibilityWorld"/>, with their keys, agents, cycles, trades, lessons, and
/// notifications. Each screen is asked for again and again, by whoever it opens for, and the
/// application's own measure says how long it took. See "Screen response times" in
/// docs/performance.md.
/// </summary>
/// <remarks>
/// The measure is the one that the operator status screen shows, so this is what an operator
/// reads of a service that runs. How the screens answer while the cycles of 500 agents run is in
/// <see cref="LoadTests"/>.
/// </remarks>
public class ScreenResponseTests
{
    /// <summary>
    /// How often each screen is asked for. The first answer of a page is slower than any after
    /// it, because its queries are new to the application: with this many, one such answer is
    /// fewer than 5 of 100, as it is in a service that runs.
    /// </summary>
    private const int Times = 40;

    [Fact]
    public async Task The_screens_of_the_specification_answer_within_300_ms_at_the_95th_percentile_measured_at_the_server()
    {
        using var world = await AccessibilityWorld.SeededAsync();

        var browsers = new Dictionary<ScreenFor, HttpClient>
        {
            [ScreenFor.Visitor] = world.CreateClient(),
            [ScreenFor.User] = world.CreateClient(),
            [ScreenFor.Operator] = world.CreateClient(),
        };

        await world.EnterAsync(Who.User, new ReadVisit(browsers[ScreenFor.User]));
        await world.EnterAsync(Who.Operator, new ReadVisit(browsers[ScreenFor.Operator]));

        // Each screen once for each of its routes: the settings have two entries that lead to one page.
        var screens = ScreenRules.Screens.DistinctBy(screen => screen.Route).ToList();

        foreach (var screen in screens)
        {
            var path = world.AddressOf(screen.Route);

            for (var asked = 0; asked < Times; asked++)
            {
                await browsers[screen.For].PageAsync(path);
            }
        }

        var read = world.ScreenTimes.Read();

        // Every screen of the specification was measured, under its route.
        Assert.All(screens, screen =>
        {
            var times = Assert.Single(read.Screens, measured => measured.Route == screen.Route).LastHour;

            Assert.True(times.Count >= Times, $"{screen.Screen} at {screen.Route} was counted {times.Count} times.");
            Assert.True(
                times.MeetsTarget,
                $"{screen.Screen} at {screen.Route}: {times.WithinTarget} of {times.Count} answers were within "
                + $"{ScreenTarget.Within.TotalMilliseconds} ms, and {ScreenTarget.Percent} of 100 took up to {Said(times)}.");
        });

        // And so were all of them together, with the pages that signing in went through.
        Assert.True(
            read.All.LastHour.MeetsTarget,
            $"All screens: {read.All.LastHour.WithinTarget} of {read.All.LastHour.Count} answers were within "
            + $"{ScreenTarget.Within.TotalMilliseconds} ms, and {ScreenTarget.Percent} of 100 took up to {Said(read.All.LastHour)}.");

        // What was measured, for whoever reads the output of the run.
        foreach (var screen in read.Screens)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{screen.Route}: {screen.LastHour.Count} answers, {ScreenTarget.Percent} of 100 within {Said(screen.LastHour)}");
        }
    }

    private static string Said(AnswerTimes times) =>
        times.BeyondLongest ? "more than a minute" : $"{times.Percentile?.TotalMilliseconds} ms";
}
