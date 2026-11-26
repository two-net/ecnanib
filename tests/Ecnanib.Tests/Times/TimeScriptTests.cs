using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Logging;
using Ecnanib.WebUi;

namespace Ecnanib.Tests.Times;

/// <summary>
/// The third script of the application, <c>times.js</c>, for the time zone of the device
/// (NFR-USA-3): it writes what the browser says its time zone is into the field of a form that
/// asks for it. These tests hold the script to what the rest of the application rests on: that
/// it is a file of this site which every page names once, that it asks the server nothing and
/// keeps nothing, that it writes no markup, no style, and no time, and that it holds no address
/// and no text of its own. So the server stays the one place that says what a time says. See
/// docs/times.md.
/// </summary>
/// <remarks>
/// None of these tests runs the script. What it does in a browser is held by
/// <c>AccessibilityBrowserTests</c>, which a run starts when it is told to: see
/// docs/accessibility.md. What is held here is the script as it is written, and as the
/// application serves it, with every run of the tests.
/// </remarks>
public partial class TimeScriptTests
{
    private const string Ada = "ada@example.com";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_page_names_the_script_of_the_times_once_and_after_the_script_of_the_framework()
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
            // A file of this site, which starts once the page is read. Every page names it: a
            // page that the browser goes to without leaving this one runs no script that it
            // brings, and the form of the sign-in is reached that way too.
            var script = Assert.Single(Named().Matches(page));

            Assert.DoesNotContain("//", script.Groups[1].Value);
            Assert.DoesNotContain(":", script.Groups[1].Value);

            // After the script of the framework, which it listens to.
            Assert.True(page.IndexOf("_framework/blazor.web", StringComparison.Ordinal) < script.Index);
        }

        using var answer = await visitor.GetAsync(Named().Match(pages[0]).Groups[1].Value, Cancellation);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("javascript", answer.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_script_asks_the_server_nothing_and_does_nothing_by_a_clock()
    {
        var script = await ScriptAsync();

        // The form that a person sends carries what the script wrote. The script itself sends
        // nothing, opens no connection, and loads no other script.
        Assert.All(
            new[] { "fetch", "XMLHttpRequest", "WebSocket", "EventSource", "sendBeacon", "import", "require", "Worker" },
            word => Assert.DoesNotMatch($@"\b{word}\b", script));

        // It writes when a page is there and when a form is sent, and never again and again.
        Assert.All(
            new[] { "setInterval", "setTimeout", "requestAnimationFrame" },
            word => Assert.DoesNotContain(word, script, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_script_writes_the_value_of_a_field_and_nothing_else_and_keeps_nothing()
    {
        var script = await ScriptAsync();

        // What it writes is the value of a field that asks for it: no markup, which could carry
        // what the page was not sent (NFR-SEC-4), no style, and no text of the page.
        Assert.All(
            new[]
            {
                "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "createElement", "eval", "Function",
                ".style", "cssText", "styleSheets", ".animate(", "classList", "className",
                "textContent", "innerText", "replaceChildren", ".data =", "setAttribute",
            },
            word => Assert.DoesNotContain(word, script, StringComparison.Ordinal));

        // The one thing that it writes, and the fields that it writes it into.
        var written = Assert.Single(Assigned().Matches(script));

        Assert.Equal("field.value", written.Groups[1].Value);
        Assert.Contains("querySelectorAll(\"input[data-time-zone]\")", script, StringComparison.Ordinal);

        // It reads no cookie, and stores nothing on the device: the server keeps what the form
        // said, in a cookie that is hidden from scripts as every cookie is (NFR-SEC-1).
        Assert.All(
            new[] { "cookie", "localStorage", "sessionStorage", "indexedDB" },
            word => Assert.DoesNotContain(word, script, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_script_holds_no_address_and_no_sentence_and_writes_no_time()
    {
        var script = await ScriptAsync();

        // It asks at no address, so it holds none.
        Assert.DoesNotMatch("[\"'`]/", script);

        // Every text that it holds is a name of something of the page or of the browser: none
        // has a blank in it, as a sentence has. So no text of the application is in a script
        // (NFR-USA-4).
        Assert.All(
            Text().Matches(script).Select(text => text.Groups[1].Value),
            text => Assert.DoesNotContain(' ', text));

        // It says which time zone the device is in, and that is all that it knows of time: no
        // moment is read, and none is written. What a time says is for the server alone, so
        // every page says a time the same way, with a script or without (NFR-USA-3).
        Assert.Contains("Intl.DateTimeFormat().resolvedOptions().timeZone", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\bDate\b", script);
        Assert.All(
            new[] { "toLocale", "getTimezoneOffset", ".format(", "formatToParts", "Temporal" },
            word => Assert.DoesNotContain(word, script, StringComparison.Ordinal));
    }

    /// <summary>The script without what it says about itself, as the application serves it.</summary>
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

    // The script, as a page names it: with the fingerprint of its content.
    [GeneratedRegex("<script type=\"module\" src=\"(times[^\"]*\\.js)\"></script>")]
    private static partial Regex Named();

    // A text as the script writes one.
    [GeneratedRegex("\"([^\"\\n]*)\"")]
    private static partial Regex Text();

    // What the script gives a value to, other than a name of its own that it declares.
    [GeneratedRegex(@"^\s*([A-Za-z_.\[\]]+)\s*=(?!=|>)", RegexOptions.Multiline)]
    private static partial Regex Assigned();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"^\s*//.*$", RegexOptions.Multiline)]
    private static partial Regex LineComment();
}
