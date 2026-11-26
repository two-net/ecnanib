using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Logging;
using Ecnanib.WebUi;

namespace Ecnanib.Tests.Layout;

/// <summary>
/// The second script of the application, <c>session.js</c>, for the notice before a session
/// ends (NFR-USA-1): it waits, asks the server how long is left, shows the dialog that the page
/// wrote, and says to the server that its user is still there. These tests hold the script to
/// what the rest of the application rests on: that it is a file of this site which every page
/// names once, that it asks the server at two places and polls nothing, that it writes no
/// markup and no style, and that it holds no address and no text of its own, so the page stays
/// the one place that says where to ask and the server the one that says what is said. See
/// "The notice before a session ends" in docs/accounts.md.
/// </summary>
/// <remarks>
/// None of these tests runs the script. What it does in a browser is held by
/// <c>AccessibilityBrowserTests</c>, which a run starts when it is told to: see
/// docs/accessibility.md. What is held here is the script as it is written, and as the
/// application serves it, with every run of the tests.
/// </remarks>
public partial class SessionScriptTests
{
    private const string Ada = "ada@example.com";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_page_names_the_script_of_the_session_once_and_after_the_script_of_the_framework()
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
            await user.PageAsync(WebUiModule.AccountPath),
            (await user.OpenAsync("/no-such-page")).Page,
        };

        foreach (var page in pages)
        {
            // A file of this site, which starts once the page is read. A page of a visitor
            // names it too, and it does nothing there: a page that the browser goes to without
            // leaving this one runs no script that it brings, so the script is on every page.
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
    public async Task The_script_asks_the_server_at_two_places_and_polls_nothing()
    {
        var script = await ScriptAsync();

        // How long is left, and that its user is still there. It opens no connection, and
        // loads no other script.
        Assert.Equal(2, Regex.Count(script, @"\bfetch\("));
        Assert.All(
            new[] { "XMLHttpRequest", "WebSocket", "EventSource", "sendBeacon", "import", "require", "Worker" },
            word => Assert.DoesNotMatch($@"\b{word}\b", script));

        // It waits once for the moment to ask, and nothing is done again and again by a clock:
        // a page that polled would be a request every so often of every page that is open.
        Assert.DoesNotContain("setInterval", script, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(script, @"\bsetTimeout\("));

        // What it is answered is of this moment, and is asked for as such.
        Assert.Equal(2, Regex.Count(script, "cache: \"no-store\""));
    }

    [Fact]
    public async Task The_script_writes_no_markup_and_no_style_and_keeps_nothing()
    {
        var script = await ScriptAsync();

        // It writes the sentence that the server said as text, and shows and hides what the
        // page wrote. Markup that a script wrote could carry what the page was not sent
        // (NFR-SEC-4), and a style that it wrote would be a look that the stylesheet does not say.
        Assert.All(
            new[]
            {
                "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "createElement", "eval", "Function",
                ".style", "cssText", "styleSheets", ".animate(", "classList", "className",
            },
            word => Assert.DoesNotContain(word, script, StringComparison.Ordinal));

        Assert.Contains("replaceChildren(", script, StringComparison.Ordinal);

        // It reads no cookie, and stores nothing on the device: the cookie of a session is
        // hidden from scripts, and stays so (NFR-SEC-1).
        Assert.All(
            new[] { "cookie", "localStorage", "sessionStorage", "indexedDB" },
            word => Assert.DoesNotContain(word, script, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_script_holds_no_address_and_no_sentence_of_its_own()
    {
        var script = await ScriptAsync();

        // Where it asks and where it answers are said by the page, and what the dialog says by
        // the server: so no text of the application is in a script (NFR-USA-4), and an address
        // that changes is changed in one place.
        Assert.DoesNotMatch("[\"'`]/", script);
        Assert.Contains("getAttribute(\"data-ask\")", script, StringComparison.Ordinal);
        Assert.Contains("button.formAction", script, StringComparison.Ordinal);

        // Every text that it holds is a name of something of the page or of the browser: none
        // has a blank in it, as a sentence has.
        Assert.All(
            Text().Matches(script).Select(text => text.Groups[1].Value),
            text => Assert.DoesNotContain(' ', text));

        // It is told how long is left in seconds, which the server counted: the clock of the
        // device says only how long it has waited, and no time of day is read or written.
        Assert.DoesNotMatch(@"\bnew Date\(", script);
        Assert.DoesNotContain("toLocale", script, StringComparison.Ordinal);
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
    [GeneratedRegex("<script type=\"module\" src=\"(session[^\"]*\\.js)\"></script>")]
    private static partial Regex Named();

    // A text as the script writes one.
    [GeneratedRegex("\"([^\"\\n]*)\"")]
    private static partial Regex Text();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"^\s*//.*$", RegexOptions.Multiline)]
    private static partial Regex LineComment();
}
