using System.Text.RegularExpressions;
using Ecnanib.Tests.Accessibility;

namespace Ecnanib.Tests.Times;

/// <summary>
/// Every state of every screen, reached by people whose devices say a time zone: the same
/// states that the accessibility of the pages is held on, by the same steps.
/// </summary>
public sealed class ZonedPages() : AccessibilityPages(TimeZoneCoverageTests.Zone);

/// <summary>
/// Times are shown in the user's local time zone (NFR-USA-3): every time of every screen, and
/// not only those of the pages that a test of its own opens. Each state of
/// <see cref="AccessibilityRules.States"/> is reached once by people who signed in on devices
/// that say a time zone, and every time that a page shows is held to that time zone. A page
/// that wrote a time in UTC, or without saying how far it is from UTC, is named. See
/// docs/times.md.
/// </summary>
/// <remarks>
/// A time is written by <c>PageTimes</c> alone, which a page is handed with its time zone, so
/// there is no way to write one without it: the compiler holds that. What is held here is that
/// every page and every part of one was handed the times of its request, and none those of
/// UTC, which a part that is not handed any falls back on.
/// </remarks>
public partial class TimeZoneCoverageTests(ZonedPages pages) : IClassFixture<ZonedPages>
{
    /// <summary>A time zone that is neither at UTC nor a whole number of hours from it, all year.</summary>
    public const string Zone = "Asia/Kolkata";

    private const string Offset = "UTC+5:30";

    [Fact]
    public async Task Every_time_of_every_screen_is_in_the_time_zone_of_its_page()
    {
        var wrong = new List<string>();
        var shown = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var state in await pages.AllAsync())
        {
            var said = state.Page.Body?.TextContent ?? string.Empty;

            foreach (Match time in Time().Matches(said))
            {
                shown.Add(state.State.Route);

                // A time of day says how far its time zone is from UTC, and that is the time
                // zone of the device of whoever reads the page.
                if (time.Groups["offset"].Value != Offset)
                {
                    wrong.Add($"{state.State.Route} ({state.State.State}): \"{time.Value.Trim()}\"");
                }
            }
        }

        Assert.True(
            wrong.Count == 0,
            $"These times are not in the time zone of their page, {Zone}, or do not say how far it is from UTC:" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, wrong)}{Environment.NewLine}" +
            "A page writes a time with the times that it is handed: [CascadingParameter] PageTimes Times. A part that is " +
            "written over the connection of its page is handed them as a parameter. See docs/times.md.");

        // The states do show times, on the screens that have some: a list that came to nothing
        // would hold nothing.
        Assert.Superset(
            new SortedSet<string>(StringComparer.Ordinal) { "/dashboard", "/agents/{Id:guid}/state", "/agents/{Id:guid}/cycles", "/account/keys" },
            shown);
    }

    // A moment as a page writes one: a day, a time of day, and whatever follows it up to the
    // end of what says how far the time zone is from UTC.
    [GeneratedRegex(@"\b\d{1,2} [A-Z][a-z]+ \d{4}, \d\d:\d\d(?::\d\d)?(?: (?<offset>UTC(?:[+-]\d{1,2}(?::\d\d)?)?))?")]
    private static partial Regex Time();
}
