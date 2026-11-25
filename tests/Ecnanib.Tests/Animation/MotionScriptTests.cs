using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Themes;
using Ecnanib.WebUi;

namespace Ecnanib.Tests.Animation;

/// <summary>
/// The application has one script of its own, <c>motion.js</c>, for what moves and a stylesheet
/// cannot do alone: a figure that counts from its old value to its new one, and saying when a
/// state changed, an item entered a list, or a page is on its way (FR-ANI-2, FR-ANI-3). These
/// tests hold the script to what the rest of the application rests on: that it is a file of
/// this site which every page names once, that it asks the server for nothing and polls
/// nothing, that it writes no markup and no style, and that it holds no time and no look of its
/// own, so the stylesheet stays the one place that says what moves (FR-ANI-4, FR-ANI-5). See
/// docs/motion.md.
/// </summary>
/// <remarks>
/// No test drives a browser, and none runs the script: that a figure counts and a page fades is
/// seen by hand, as docs/motion.md says. What is held here is the script as it is written, and
/// as the application serves it.
/// </remarks>
public partial class MotionScriptTests
{
    private const string Ada = "ada@example.com";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_page_names_the_one_script_of_the_application_once_and_after_its_stylesheets()
    {
        using var application = new AccountsApplication();
        using var visitor = application.CreateClient();
        using var user = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await user.SignInAsync(Ada);

        var pages = new[]
        {
            await visitor.PageAsync("/"),
            await visitor.PageAsync(SessionAuthentication.SignInPath),
            await user.PageAsync(WebUiModule.DashboardPath),
            await user.PageAsync(WebUiModule.AppearancePath),
            (await user.OpenAsync("/no-such-page")).Page,
        };

        foreach (var page in pages)
        {
            // One script of the application, as a file of this site: the page carries none of
            // it. It starts once the page is read, as a module does, so it holds up nothing.
            var script = Assert.Single(Named().Matches(page));

            Assert.DoesNotContain("//", script.Groups[1].Value);
            Assert.DoesNotContain(":", script.Groups[1].Value);

            // It comes after the stylesheets, which say everything that it reads, and after
            // the script of the framework, which it listens to.
            Assert.True(page.LastIndexOf("<link rel=\"stylesheet\"", StringComparison.Ordinal) < script.Index);
            Assert.True(page.IndexOf("_framework/blazor.web", StringComparison.Ordinal) < script.Index);
        }

        // And it is there to be had, as a script.
        using var answer = await visitor.GetAsync(Named().Match(pages[0]).Groups[1].Value, Cancellation);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("javascript", answer.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_script_asks_the_server_for_nothing_and_polls_nothing()
    {
        var script = await ScriptAsync();

        // Updates reach a page over its one connection, and no script of a page polls
        // (NFR-ARC-6): this one makes no request, opens no connection, and loads no other script.
        Assert.All(
            new[] { "fetch", "XMLHttpRequest", "WebSocket", "EventSource", "sendBeacon", "import", "require", "Worker" },
            word => Assert.DoesNotMatch($@"\b{word}\b", script));

        // Nothing is done again and again by a clock. A figure counts by the frames of the
        // browser, which draws none for a tab in the background (FR-ANI-6).
        Assert.DoesNotContain("setInterval", script, StringComparison.Ordinal);
        Assert.Contains("requestAnimationFrame", script, StringComparison.Ordinal);
        Assert.Contains("IntersectionObserver", script, StringComparison.Ordinal);
        Assert.Contains("visibilitychange", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_script_writes_no_markup_and_no_style()
    {
        var script = await ScriptAsync();

        // It writes text into the node of a figure, and names states as attributes. Markup
        // that a script wrote could carry what the page was not sent (NFR-SEC-4), and a style
        // that it wrote would be a look that the stylesheet does not say.
        Assert.All(
            new[]
            {
                "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "createElement", "eval", "Function",
                ".style", "cssText", "styleSheets", ".animate(", "classList", "className",
            },
            word => Assert.DoesNotContain(word, script, StringComparison.Ordinal));

        // The text of a figure is written in the node that is there: one that was replaced
        // would be lost to the connection of the page, which writes to the node that it knows.
        Assert.Contains(".data = ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("textContent =", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerText", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_script_holds_no_time_of_its_own_and_reads_the_ones_that_the_stylesheet_says()
    {
        var script = await ScriptAsync();
        var styles = Stylesheets.WithoutComments(await Stylesheets.OfThePagesAsync());

        // No time is written in it, as a stylesheet writes one or as a number of milliseconds
        // for a clock.
        Assert.Empty(Time().Matches(script).Select(time => time.Value));

        // It waits twice, each time for as long as the stylesheet says: before the placeholder
        // of a page that is on its way, and before it gives the content back when the page
        // never comes.
        Assert.Equal(
            ["setTimeout(arrived, timeOf(\"--loading-longest\"));", "setTimeout(late, timeOf(\"--loading-after\"));"],
            Wait().Matches(script).Select(wait => wait.Value));

        // And these are the times that it reads, each of which the stylesheet gives.
        var read = Read().Matches(script).Select(name => name.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["--figure-change", "--loading-after", "--loading-longest"], read);
        Assert.All(read, name => Assert.Matches($@"{Regex.Escape(name)}:\s*\d+m?s;", styles));

        // A page that does not come says so in no other way than by the request that failed,
        // which the script hears.
        Assert.Contains("addEventListener(\"unhandledrejection\", arrived);", script, StringComparison.Ordinal);
        Assert.Contains("addEventListener(\"pageshow\", arrived);", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_the_script_marks_is_what_the_stylesheet_reads()
    {
        var script = await ScriptAsync();
        var styles = Stylesheets.WithoutComments(await Stylesheets.OfThePagesAsync());

        // The states that the script names on an element, by setting, toggling, or marking.
        var marked = Marked().Matches(script)
            .Select(mark => mark.Groups[1].Value)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["aria-busy", "data-changed", "data-entered", "data-hidden", "data-offscreen", "data-still"], marked);

        // Each is a state that the stylesheet has a rule for, and a mark that no rule reads
        // would be a state that nothing shows.
        Assert.All(marked, name => Assert.Contains($"[{name}", styles, StringComparison.Ordinal));

        // And what the script reads on a page is what the pages write: MotionMarkupTests holds
        // the pages to these.
        Assert.All(
            new[] { "\"data.figure\"", "\"[data-key]\"", "\"data-change\"", "\"[data-moves]\"", "\"value\"" },
            name => Assert.Contains(name, script, StringComparison.Ordinal));
    }

    /// <summary>The script of the application without what it says about itself, as the application serves it.</summary>
    private static async Task<string> ScriptAsync()
    {
        // The application as it is, on the database that the tests share: a script is a file,
        // and nothing of it is stored.
        using var application = new LoggedApplication(LoggedApplication.Settings());
        using var client = application.CreateClient();

        var named = Assert.Single(Named().Matches(await client.PageAsync("/")));
        var script = await client.GetStringAsync(named.Groups[1].Value, Cancellation);

        return LineComment().Replace(BlockComment().Replace(script, string.Empty), string.Empty);
    }

    // The script of the application, as a page names it: with the fingerprint of its content.
    [GeneratedRegex("<script type=\"module\" src=\"(motion[^\"]*\\.js)\"></script>")]
    private static partial Regex Named();

    // A time as a stylesheet writes it, or as a clock is given one: a number of milliseconds.
    [GeneratedRegex(@"(?<![\w.])\d*\.?\d+(?:ms|s)\b|(?<=setTimeout\([^;]*,\s*)\d+")]
    private static partial Regex Time();

    [GeneratedRegex(@"setTimeout\([^;]*;")]
    private static partial Regex Wait();

    [GeneratedRegex("timeOf\\(\"(--[a-z-]+)\"\\)")]
    private static partial Regex Read();

    [GeneratedRegex("(?:setAttribute|toggleAttribute|mark)\\((?:[\\w.()?]+, )?\"([a-z-]+)\"")]
    private static partial Regex Marked();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"^\s*//.*$", RegexOptions.Multiline)]
    private static partial Regex LineComment();
}
