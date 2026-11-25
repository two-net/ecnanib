using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.WebUi;
using Microsoft.AspNetCore.Components;

namespace Ecnanib.Tests.Layout;

/// <summary>
/// The UI is a responsive web application, usable at desktop and mobile widths (FR-UI-1), with
/// navigation to every screen (FR-UI-2). These tests hold the frame that every page is written
/// in: the link past the bar, the bar, the content, and the foot, the mark of the screen that is
/// open, the navigation of the settings, and the menu of a narrow screen, which needs no script.
/// What each screen is and what leads to it is in <see cref="ScreenRuleTests"/>. See
/// docs/layout.md.
/// </summary>
/// <remarks>
/// No test drives a browser: what a page looks like at a width is seen by hand, as docs/layout.md
/// says. What is held here is what the look rests on: the elements of the frame, and the two
/// rules of the stylesheet that make one frame of them at both widths.
/// </remarks>
public partial class LayoutPageTests
{
    private const string Ada = "ada@example.com";
    private const string Olive = "olive@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private static readonly string[] LegalPaths = ["/legal/terms", "/legal/privacy", "/legal/risk-disclosure"];

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_page_has_the_link_past_the_bar_the_bar_the_content_and_the_foot()
    {
        using var application = new AccountsApplication();
        using var visitor = application.CreateClient();
        using var user = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await user.SignInAsync(Ada);

        // Every page there is, so that a page which is added later is looked at too. A page that
        // is not for whoever asks answers with another page, which has the frame as well.
        var paths = Pages().ToList();
        Assert.Contains(WebUiModule.HomePath, paths);
        Assert.Contains(WebUiModule.DashboardPath, paths);
        Assert.Contains(WebUiModule.OperatorStatusPath, paths);

        foreach (var path in paths)
        {
            foreach (var client in new[] { visitor, user })
            {
                using var answer = await client.GetAsync(path, Cancellation);

                AssertHasTheFrame(path, await answer.Content.ReadAsStringAsync(Cancellation));
            }
        }
    }

    [Fact]
    public async Task The_link_past_the_bar_leads_to_the_content_of_the_page_that_it_is_on()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The pages share one base address, so the link names its page, with what follows the
        // question mark of its address. The browser follows it by itself.
        foreach (var address in new[] { "/", "/account", "/usage?period=30" })
        {
            var link = Assert.Single(SkipLink().Matches(await client.PageAsync(address)));

            Assert.Equal($"{address}#content", WebUtility.HtmlDecode(link.Groups[1].Value));
            Assert.Contains("data-enhance-nav=\"false\"", link.Value);
            Assert.Contains(">Skip to the content</a>", link.Value);
        }
    }

    [Fact]
    public async Task The_bar_marks_the_screen_that_is_open_and_no_other()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning);

        // A page that stores a key opens for a user who has just proved who they are.
        await client.ConfirmIdentityAsync();

        // A link says "page" of the very page it leads to, and "true" of a page that is a part
        // of its screen. The settings have a navigation of their own, which says it too.
        (string Path, string[] Bar, string[] Settings)[] pages =
        [
            ("/", [], []),
            ("/dashboard", ["Dashboard=page"], []),
            ("/agents", ["Agents=page"], []),
            ("/agents/new", ["Agents=true"], []),
            ($"/agents/{agent}", ["Agents=true"], []),
            ($"/agents/{agent}/state", ["Agents=true"], []),
            ("/experience", ["Experience=page"], []),
            ("/experience/lessons", ["Experience=true"], []),
            ("/usage", ["AI usage=page"], []),
            ("/catalog", ["Model catalog=page"], []),
            ("/notifications", ["Notifications=page"], []),
            ("/account", ["Settings=page"], ["Profile=page"]),
            ("/account/two-factor", ["Settings=true"], ["Security=page"]),
            ("/account/keys", ["Settings=true"], ["API keys=page"]),
            ("/account/keys/ai/claude", ["Settings=true"], ["API keys=true"]),
            ("/account/appearance", ["Settings=true"], ["Appearance=page"]),
            ("/account/notifications", ["Settings=true"], ["Notifications=page"]),
        ];

        foreach (var (path, bar, settings) in pages)
        {
            var page = await client.PageAsync(path);

            Assert.Equal(bar, Marked(BarOf(page)));
            Assert.Equal(settings, Marked(SettingsOf(page)));
        }
    }

    [Fact]
    public async Task A_visitor_is_shown_the_start_page_and_the_ways_in_and_nothing_of_a_user()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        var page = await client.PageAsync("/");
        var bar = BarOf(page);

        // The bar: the name of the service, which leads to the start page, and the two ways in.
        Assert.Matches("<a class=\"site-name\" href=\"/\"[^>]*>Ecnanib</a>", bar);
        Assert.Equal(["Sign in", "Register"], LinkTexts(bar).Skip(1));
        Assert.Empty(Marked(bar));

        // Nothing of it is for a user: no menu, no button, and no navigation of the settings.
        Assert.DoesNotContain("<details", page);
        Assert.DoesNotContain("<form", page);
        Assert.DoesNotContain("Sign out", page);
        Assert.DoesNotContain("settings-nav", page);

        // The foot leads a visitor to where they choose how the pages look on their device,
        // under the legal documents. A user is led there by the settings.
        var foot = page[Footer().Match(page).Index..page.IndexOf("</footer>", StringComparison.Ordinal)];
        Assert.Equal(
            ["Terms of Service", "Privacy Policy", "Risk disclosure", "Appearance"],
            LinkTexts(foot));
        Assert.Contains($"<a href=\"{WebUiModule.DeviceAppearancePath}\">Appearance</a>", foot);

        // The start page says what the service is, and leads on.
        Assert.Contains("<title>AI trading agents on Binance</title>", page);
        Assert.Contains("<h1>Ecnanib</h1>", page);
        Assert.Contains("Ecnanib is a free service for AI trading agents on Binance.", page);
        Assert.Contains("<a href=\"/register\">Create an account</a>", page);
        Assert.Contains("<a href=\"/sign-in\">Sign in to your account</a>", page);
        Assert.Contains("<a href=\"/legal/risk-disclosure\">risk disclosure</a>", page);
        Assert.DoesNotContain("Go to your dashboard", page);

        // On the page of a way in, the bar says that it is open.
        Assert.Equal(["Sign in=page"], Marked(BarOf(await client.PageAsync("/sign-in"))));
        Assert.Equal(["Register=page"], Marked(BarOf(await client.PageAsync("/register"))));

        // Signed in, the same page leads to what is the user's, and no longer in.
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        page = await client.PageAsync("/");

        Assert.Contains("<h1>Ecnanib</h1>", page);
        Assert.Contains("<a href=\"/dashboard\">Go to your dashboard</a>", page);
        Assert.Contains("<a href=\"/agents\">Go to your agents</a>", page);
        Assert.DoesNotContain("Create an account", page);
        Assert.DoesNotContain("Sign in to your account", page);
    }

    [Fact]
    public async Task A_start_page_of_a_closed_registration_says_so_and_does_not_lead_to_it()
    {
        using var application = new AccountsApplication(registrationOpen: false);
        using var client = application.CreateClient();

        var page = await client.PageAsync("/");

        Assert.Contains("Registration is closed. No new accounts are being accepted at the moment.", page);
        Assert.DoesNotContain("href=\"/register\"", page);
        Assert.Equal(["Sign in"], LinkTexts(BarOf(page)).Skip(1));
    }

    [Fact]
    public async Task The_settings_have_a_navigation_of_their_own_on_their_pages_and_on_no_other()
    {
        using var application = new AccountsApplication();
        using var visitor = application.CreateClient();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // Profile, security, API keys, appearance, and notifications, in the order of the
        // specification, above each page of the settings.
        (string Path, string Text)[] settings =
        [
            ("/account", "Profile"),
            ("/account/two-factor", "Security"),
            ("/account/keys", "API keys"),
            ("/account/appearance", "Appearance"),
            ("/account/notifications", "Notifications"),
        ];

        foreach (var (path, _) in settings)
        {
            var page = await client.PageAsync(path);
            var navigation = SettingsOf(page);

            Assert.Contains("aria-label=\"Settings\"", page);
            (string Path, string Text)[] links = [.. Link().Matches(navigation).Select(link => (link.Groups[1].Value, link.Groups[2].Value))];

            Assert.Equal(settings, links);

            // It is in the content, above the heading of the page.
            Assert.True(page.IndexOf("<main", StringComparison.Ordinal) < page.IndexOf("settings-nav", StringComparison.Ordinal));
            Assert.True(page.IndexOf("settings-nav", StringComparison.Ordinal) < page.IndexOf("<h1", StringComparison.Ordinal));
        }

        foreach (var path in new[] { "/", "/dashboard", "/agents", "/catalog", "/notifications", "/legal/terms" })
        {
            Assert.DoesNotContain("settings-nav", await client.PageAsync(path));
        }

        // A page under the account that is for visitors has none: nobody is signed in on it.
        Assert.DoesNotContain("settings-nav", await visitor.PageAsync(WebUiModule.AccountDeletedPath));
    }

    [Fact]
    public async Task The_page_of_the_appearance_is_among_the_settings_and_takes_the_choices_of_a_theme_and_of_what_moves()
    {
        using var application = new AccountsApplication();
        using var visitor = application.CreateClient();
        using var client = application.CreateClient();

        // A visitor is sent to sign in, as on every page of the settings.
        Assert.Equal(
            SessionAuthentication.PageWith(SessionAuthentication.SignInPath, WebUiModule.AppearancePath),
            (await visitor.OpenAsync(WebUiModule.AppearancePath)).Address);

        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var page = await client.PageAsync(WebUiModule.AppearancePath);

        Assert.Contains("<h1>Appearance</h1>", page);
        Assert.Contains("The pages are light or dark as your device is set, and change when its setting does.", page);
        Assert.DoesNotContain("is not built yet.", page);

        // It has the navigation of the settings above it, as every page of the settings has.
        Assert.Single(SettingsNavigation().Matches(page));

        // Three forms: the button of the bar, the choice of a theme, which is three fields to
        // tick, and the choice of how much moves, which is two. Nothing is typed. What the
        // choices do is held by ThemePageTests and MotionPageTests.
        var forms = Form().Matches(page);
        Assert.Equal(3, forms.Count);
        Assert.Contains("action=\"/sign-out\"", forms[0].Value);
        Assert.Equal(
            ["system", "dark", "light", "system", "reduced"],
            Input().Matches(page)
                .Where(input => input.Value.Contains("type=\"radio\"", StringComparison.Ordinal))
                .Select(input => Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value));
        Assert.All(
            Input().Matches(page),
            input => Assert.True(
                input.Value.Contains("type=\"hidden\"", StringComparison.Ordinal) || input.Value.Contains("type=\"radio\"", StringComparison.Ordinal)));
        Assert.DoesNotContain("<select", page);
    }

    [Fact]
    public async Task The_navigation_of_a_narrow_screen_opens_and_closes_without_a_script()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var page = await client.PageAsync("/");
        var bar = BarOf(page);

        // What opens and closes is an element that the browser itself opens and closes, also by
        // keyboard: "Menu", and what it holds.
        var menu = Assert.Single(Menu().Matches(bar));
        Assert.Equal("Menu", menu.Groups[1].Value.Trim());

        // It holds who is signed in, with the button that signs out, and then the navigation.
        var held = menu.Groups[2].Value;
        var user = held.IndexOf("class=\"site-user\"", StringComparison.Ordinal);
        var navigation = held.IndexOf("<nav class=\"site-nav\"", StringComparison.Ordinal);

        Assert.True(user >= 0 && user < navigation, "The menu does not hold who is signed in and then the navigation.");
        Assert.Contains("ada@example.com", held[user..navigation]);
        Assert.Contains("Sign out", held[user..navigation]);
        Assert.Equal(
            ["Dashboard", "Agents", "Experience", "AI usage", "Model catalog", "Settings"],
            LinkTexts(held[navigation..]));

        // The link to the notifications is ahead of the menu, and not in it: it stays in the bar
        // of a narrow screen.
        var notifications = bar.IndexOf("href=\"/notifications\"", StringComparison.Ordinal);

        Assert.True(notifications >= 0 && notifications < menu.Index, "The link to the notifications is not ahead of the menu.");
        Assert.DoesNotContain("href=\"/notifications\"", held);

        // The page takes nothing for it: its one form signs out, and it has no field to tick.
        Assert.Contains("action=\"/sign-out\"", Assert.Single(Form().Matches(page)).Value);
        Assert.All(Input().Matches(page), input => Assert.Contains("type=\"hidden\"", input.Value));

        // The stylesheet that every page links makes one frame of this at both widths. Below one
        // width, what a closed menu holds takes no room. From that width on, what opens the menu
        // is hidden and what it holds always shows. A browser that cannot lay out what a menu
        // holds keeps the menu at every width. And the page is light or dark as the device is set.
        var styles = await client.GetStringAsync(Stylesheet().Match(page).Groups[1].Value, Cancellation);

        Assert.Contains("@supports selector(::details-content)", styles);
        Assert.Contains(".site-menu:not([open])::details-content", styles);
        Assert.Contains("@media (min-width: 48rem)", styles);
        Assert.Contains("color-scheme: light dark;", styles);
        Assert.Contains("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"", page);
    }

    [Fact]
    public async Task The_bar_leads_an_operator_to_the_operator_status_screen_and_nobody_else()
    {
        const string link = "<a href=\"/operator\"[^>]*>Operator status</a>";

        using var application = new AccountsApplication(
            settings: file => file.WithJson("Application:Operators", $"[ \"{Olive}\" ]"));
        using var visitor = application.CreateClient();
        using var user = application.CreateClient();
        using var anOperator = application.CreateClient();

        await application.RegisterVerifiedAsync(Ada);
        await application.RegisterVerifiedAsync(Olive);
        await user.SignInAsync(Ada);
        await anOperator.SignInAsync(Olive);

        // The link is shown to whom the screen opens for.
        Assert.DoesNotMatch(link, await visitor.PageAsync("/"));
        Assert.DoesNotMatch(link, await user.PageAsync("/"));
        Assert.Matches(link, await anOperator.PageAsync("/"));
        Assert.Matches(link, await anOperator.PageAsync("/account"));

        // An operator opens the screen, which says what it is to show, and that it is open.
        var page = await anOperator.PageAsync(WebUiModule.OperatorStatusPath);

        Assert.Contains("<h1>Operator status</h1>", page);
        Assert.Contains("What it is to show is not built yet", page);
        Assert.Equal(["Operator status=page"], Marked(BarOf(page)));

        // A user who is not an operator is refused, and is told no more than that there is no
        // page for them (NFR-SEC-3). They stay signed in.
        using (var refused = await user.GetAsync(WebUiModule.OperatorStatusPath, Cancellation))
        {
            var said = await refused.Content.ReadAsStringAsync(Cancellation);

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("<h1>Page not found</h1>", said);
            Assert.DoesNotContain("Operator status", said);
            Assert.Contains("Sign out", said);
        }

        // A visitor is sent to sign in, and from there back.
        Assert.Equal(
            SessionAuthentication.PageWith(SessionAuthentication.SignInPath, WebUiModule.OperatorStatusPath),
            (await visitor.OpenAsync(WebUiModule.OperatorStatusPath)).Address);
    }

    [Fact]
    public async Task An_address_that_has_no_page_says_so_with_the_bar_around_it()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        using var answer = await client.GetAsync("/no-such-page", Cancellation);
        var page = await answer.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
        Assert.Contains("<title>Page not found</title>", page);
        Assert.Contains("<h1>Page not found</h1>", page);
        Assert.Contains("There is no page for you at this address.", page);
        Assert.Contains("<a href=\"/\">Back to the start page</a>", page);
        AssertHasTheFrame("/no-such-page", page);

        // The address of the browser is the one that was asked for, and the link past the bar
        // names that one, so following it leaves the page where it is.
        using var asked = await client.GetAsync("/no-such/page?of=any", Cancellation);
        var link = Assert.Single(SkipLink().Matches(await asked.Content.ReadAsStringAsync(Cancellation)));

        Assert.Equal("/no-such/page?of=any#content", WebUtility.HtmlDecode(link.Groups[1].Value));
    }

    /// <summary>
    /// Holds a page to the frame: the viewport of the device, the link past the bar ahead of
    /// everything, one bar with the name of the service, one content, which that link leads to,
    /// and one foot with the legal documents.
    /// </summary>
    private static void AssertHasTheFrame(string path, string page)
    {
        void Holds(bool held, string what) => Assert.True(held, $"The page at {path} {what}.");

        Holds(
            page.Contains("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"", StringComparison.Ordinal),
            "does not say that it is as wide as the device");

        Holds(SkipLink().Count(page) == 1, "does not have the one link past the bar");
        Holds(Header().Count(page) == 1, "does not have the one bar");
        Holds(Content().Count(page) == 1, "does not have the one content that the link past the bar leads to");
        Holds(Footer().Count(page) == 1, "does not have the one foot");

        var skip = SkipLink().Match(page).Index;
        var header = Header().Match(page).Index;
        var main = Content().Match(page).Index;
        var footer = Footer().Match(page).Index;

        Holds(skip < header && header < main && main < footer, "does not have the link past the bar, the bar, the content, and the foot in that order");
        Holds(page.IndexOf("<h1", StringComparison.Ordinal) > main, "has no heading in its content");
        Holds(BarOf(page).Contains("<a class=\"site-name\" href=\"/\"", StringComparison.Ordinal), "does not lead to the start page by the name of the service");

        var foot = page[footer..];
        Holds(LegalPaths.All(legal => foot.Contains($"<a href=\"{legal}\"", StringComparison.Ordinal)), "does not lead to the legal documents at its foot");
    }

    /// <summary>The bar at the top of a page.</summary>
    private static string BarOf(string page) => Bar().Match(page).Value;

    /// <summary>The navigation of the settings on a page, or nothing where there is none.</summary>
    private static string SettingsOf(string page) => SettingsNavigation().Match(page).Value;

    /// <summary>The links of a part of a page that say that their screen is open, each with what it says.</summary>
    private static string[] Marked(string part) =>
        [.. MarkedLink().Matches(part).Select(link => $"{WebUtility.HtmlDecode(link.Groups[2].Value)}={link.Groups[1].Value}")];

    /// <summary>What the links of a part of a page say, in their order.</summary>
    private static IEnumerable<string> LinkTexts(string part) =>
        Link().Matches(part).Select(link => WebUtility.HtmlDecode(link.Groups[2].Value));

    /// <summary>The path of every page of the application that names nothing in its address.</summary>
    private static IEnumerable<string> Pages() =>
        typeof(Program).Assembly.GetTypes()
            .SelectMany(component => component.GetCustomAttributes<RouteAttribute>())
            .Select(route => route.Template)
            .Where(route => !route.Contains('{', StringComparison.Ordinal))
            .Distinct()
            .Order(StringComparer.Ordinal);

    [GeneratedRegex("<a class=\"skip-link\" href=\"([^\"]*)\"[^>]*>[^<]*</a>")]
    private static partial Regex SkipLink();

    [GeneratedRegex(@"<header[\s>]")]
    private static partial Regex Header();

    [GeneratedRegex("<main id=\"content\" tabindex=\"-1\"")]
    private static partial Regex Content();

    [GeneratedRegex(@"<footer[\s>]")]
    private static partial Regex Footer();

    [GeneratedRegex(@"<header[\s>].*?</header>", RegexOptions.Singleline)]
    private static partial Regex Bar();

    [GeneratedRegex("<nav class=\"settings-nav\".*?</nav>", RegexOptions.Singleline)]
    private static partial Regex SettingsNavigation();

    [GeneratedRegex("<a [^>]*href=\"([^\"]*)\"[^>]*>([^<]*)</a>")]
    private static partial Regex Link();

    [GeneratedRegex("<a [^>]*aria-current=\"(page|true)\"[^>]*>([^<]*)</a>")]
    private static partial Regex MarkedLink();

    [GeneratedRegex(@"<details class=""site-menu""[^>]*>\s*<summary[^>]*>([^<]*)</summary>(.*?)</details>", RegexOptions.Singleline)]
    private static partial Regex Menu();

    [GeneratedRegex("<link rel=\"stylesheet\" href=\"([^\"]*app[^\"]*\\.css)\"")]
    private static partial Regex Stylesheet();

    [GeneratedRegex("<form[^>]*>")]
    private static partial Regex Form();

    [GeneratedRegex("<input[^>]*>")]
    private static partial Regex Input();
}
