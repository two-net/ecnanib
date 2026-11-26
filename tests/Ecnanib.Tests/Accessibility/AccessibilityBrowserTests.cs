using System.Collections.Concurrent;
using Ecnanib.Accounts;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Notifications;
using Ecnanib.WebUi;
using Ecnanib.WebUi.Layout;
using Ecnanib.WebUi.Live;
using Microsoft.Playwright;

namespace Ecnanib.Tests.Accessibility;

/// <summary>
/// The browser of a run, started when the first test asks for it, and for all of them, with
/// what it found at every state of every screen, which it goes through once.
/// </summary>
public sealed class BrowserOfTheRun : IAsyncDisposable
{
    private readonly Lazy<Task<DrivenBrowser>> _browser = new(DrivenBrowser.StartAsync);
    private readonly Lazy<Task<IReadOnlyList<StateFindings>>> _found;

    public BrowserOfTheRun() =>
        _found = new(async () => await AccessibilityBrowserRun.GoThroughAsync(await StartedAsync()));

    internal Task<DrivenBrowser> StartedAsync() => _browser.Value;

    /// <summary>What the browser found at every state of the list: see <see cref="AccessibilityBrowserRun"/>.</summary>
    internal Task<IReadOnlyList<StateFindings>> FoundAsync() => _found.Value;

    public async ValueTask DisposeAsync()
    {
        if (_browser.IsValueCreated && _browser.Value.IsCompletedSuccessfully)
        {
            await (await _browser.Value).DisposeAsync();
        }
    }
}

/// <summary>
/// The check of every screen in a browser (NFR-USA-1): what a keyboard and a screen reader meet
/// there, which the markup alone does not say. Every state of <see cref="AccessibilityRules.States"/>
/// is opened in Chromium: axe checks the page by the rules of WCAG 2.0 and 2.1, levels A and
/// AA, on a light page, and its colours again on a dark one; the page is walked with the
/// keyboard; and it is looked at for scrolling sideways at the width of a phone and at twice
/// the size of a desk. Once in a run: where the focus is when a page opens and after a form,
/// that the link past the bar and the menu of a narrow screen work by keyboard, that the
/// notice before a session ends comes, is answered, and lets the session end, and that a page
/// which follows is paused and follows again. See docs/accessibility.md.
/// </summary>
/// <remarks>
/// These tests drive a browser, and are skipped unless the run was told to: set
/// <c>ECNANIB_BROWSER=chromium</c>. The application listens on a port of this machine for
/// them, over TLS, as <see cref="ListeningApplicationTests"/> holds with every run.
/// </remarks>
public partial class AccessibilityBrowserTests(BrowserOfTheRun run) : IClassFixture<BrowserOfTheRun>
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    // How long a page is given to do what it does by itself, such as opening a dialog.
    private const float Soon = 10_000;

    [Fact]
    public async Task Every_state_of_every_screen_is_reached_in_the_browser()
    {
        DrivenBrowser.IsAskedFor();

        var found = await run.FoundAsync();

        // Each state of the list, and each once: a state that the browser did not come to was
        // checked by nothing.
        Assert.Equal(AccessibilityRules.States.Count, found.Count);
        AssertNothing(found, state => state.NotReached is null ? [] : [state.NotReached], "were not reached by the browser");
    }

    [Fact]
    public async Task Every_state_of_every_screen_passes_axe_on_a_light_page()
    {
        DrivenBrowser.IsAskedFor();

        AssertNothing(await run.FoundAsync(), state => state.Axe, "do not pass axe by the rules of WCAG 2.0 and 2.1, levels A and AA");
    }

    [Fact]
    public async Task Every_state_of_every_screen_passes_axe_for_its_colours_on_a_dark_page()
    {
        DrivenBrowser.IsAskedFor();

        AssertNothing(await run.FoundAsync(), state => state.AxeDark, "do not pass axe for their colours on a dark page");
    }

    [Fact]
    public async Task Every_state_of_every_screen_is_walked_through_with_the_keyboard()
    {
        DrivenBrowser.IsAskedFor();

        AssertNothing(await run.FoundAsync(), state => state.Walk, "are not walked through with the keyboard as a page is to be");
    }

    [Fact]
    public async Task No_state_scrolls_sideways_on_a_narrow_screen_or_at_twice_the_size()
    {
        DrivenBrowser.IsAskedFor();

        AssertNothing(await run.FoundAsync(), state => state.Widths, "scroll sideways");
    }

    [Fact]
    public async Task No_state_has_the_browser_say_anything_of_it()
    {
        DrivenBrowser.IsAskedFor();

        AssertNothing(await run.FoundAsync(), state => state.Console, "have the browser report an error of a script or of the content security policy");
    }

    [Theory]
    [InlineData("<img src=\"data:image/gif;base64,R0lGODlhAQABAAAAACw=\">", "image-alt")]
    [InlineData("<input type=\"text\" name=\"Name\">", "label")]
    [InlineData("<button></button>", "button-name")]
    [InlineData("<a href=\"/somewhere\"></a>", "link-name")]
    [InlineData("<p style=\"color: #aaaaaa; background: #ffffff\">Hard to read.</p>", "color-contrast")]
    [InlineData("<div role=\"region\" aria-labelledby=\"gone\" tabindex=\"0\" aria-hidden=\"true\">Hidden, and a stop.</div>", "aria-hidden-focus")]
    public async Task A_planted_page_is_found_by_axe(string markup, string rule)
    {
        DrivenBrowser.IsAskedFor();

        await using var blank = await (await run.StartedAsync()).OpenAsync(new Uri("about:blank"));
        var page = await blank.NewPageAsync();

        await page.SetContentAsync(Planted(markup));

        var found = await Deque.AxeCore.Playwright.PageExtensions.RunAxe(
            page, new Deque.AxeCore.Commons.AxeRunOptions { RunOnly = Deque.AxeCore.Commons.RunOnlyOptions.Tags("wcag2a", "wcag2aa", "wcag21a", "wcag21aa") });

        Assert.Contains(found.Violations, violation => violation.Id == rule);
    }

    [Theory]
    [InlineData("<a href=\"/one\">One</a> <p contenteditable=\"true\">Typed in.</p>", "", "which is no control of the page")]
    [InlineData("<a href=\"/one\">One</a>", "a:focus-visible { outline: none; }", "without the line that says so")]
    [InlineData("<a href=\"/one\">One</a>", "a:focus-visible { outline: 2px solid #f4f4f4; }", "contrast of")]
    [InlineData("<a href=\"/one\">One</a> <a href=\"/two\" tabindex=\"1\">Two</a> <a href=\"/three\">Three</a>", "", "comes first in the page")]
    public async Task A_planted_page_is_found_by_the_walk(string markup, string style, string phrase)
    {
        DrivenBrowser.IsAskedFor();

        await using var blank = await (await run.StartedAsync()).OpenAsync(new Uri("about:blank"));
        var page = await blank.NewPageAsync();

        await page.SetContentAsync(Planted(markup, style));
        await page.FocusAsync("h1");

        var found = await KeyboardWalk.FindingsAsync(page, "h1");

        Assert.Contains(found, finding => finding.Contains(phrase, StringComparison.Ordinal));

        // And a page that has nothing of the kind is walked through without a word.
        await page.SetContentAsync(Planted("<a href=\"/one\">One</a> <button type=\"button\">Two</button>"));
        await page.FocusAsync("h1");

        Assert.Empty(await KeyboardWalk.FindingsAsync(page, "h1"));
    }

    [Fact]
    public async Task A_page_that_opens_has_the_focus_at_its_heading_and_the_link_past_the_bar_leads_into_the_content()
    {
        DrivenBrowser.IsAskedFor();

        using var application = new AccountsApplication(listens: true);
        await using var visitor = await (await run.StartedAsync()).OpenAsync(application.ListeningAt);
        var page = await visitor.NewPageAsync();
        var said = Listen(page);

        // A page that opens: the focus is at its heading, and shows no line there.
        await page.GotoAsync("/");
        await FocusSettlesAsync(page);

        Assert.Equal("h1", await FocusAsync(page));
        Assert.Equal("none", await page.EvaluateAsync<string>("() => getComputedStyle(document.activeElement).outlineStyle"));

        // Back through the bar to the first link of the page, which shows while it has the focus.
        for (var back = 0; back < 10 && !(await FocusAsync(page)).StartsWith("a.skip-link", StringComparison.Ordinal); back++)
        {
            await page.Keyboard.PressAsync("Shift+Tab");
        }

        Assert.Equal("a.skip-link", await FocusAsync(page));
        Assert.Equal(Screens.SkipLink, await page.EvaluateAsync<string>("() => document.activeElement.textContent"));
        Assert.True(
            await page.EvaluateAsync<bool>("() => document.activeElement.getBoundingClientRect().top >= 0"),
            "The link past the bar has the focus and does not show.");
        Assert.True(await OutlinedAsync(page), "The link past the bar has the focus without the line that says so.");

        // It leads into the content, and the next stop of the keyboard is there.
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("() => document.activeElement === document.querySelector('main')", null, new() { Timeout = Soon });
        await page.Keyboard.PressAsync("Tab");

        Assert.True(
            await page.EvaluateAsync<bool>("() => document.activeElement !== document.querySelector('main') && document.querySelector('main').contains(document.activeElement)"),
            $"After the link past the bar, the keyboard stops at {await FocusAsync(page)}, which is not in the content.");

        // A page that the browser goes to without leaving this one has the focus at its heading too.
        await page.EvaluateAsync("() => { window.stayed = true; }");
        await page.ClickAsync($"header a[href='{SessionAuthentication.SignInPath}']");
        await page.WaitForURLAsync($"**{SessionAuthentication.SignInPath}");
        await page.WaitForFunctionAsync("() => document.activeElement?.tagName === 'H1' && document.title === 'Sign in'", null, new() { Timeout = Soon });

        Assert.True(await page.EvaluateAsync<bool>("() => window.stayed === true"), "The browser left the page to go to the next one.");
        Assert.Empty(said);
    }

    [Fact]
    public async Task What_a_form_is_answered_with_has_the_focus_when_its_page_opens()
    {
        DrivenBrowser.IsAskedFor();

        using var application = new AccountsApplication(listens: true);
        await application.RegisterVerifiedAsync(Ada);
        await using var visitor = await (await run.StartedAsync()).OpenAsync(application.ListeningAt);
        var page = await visitor.NewPageAsync();
        var said = Listen(page);

        // What stands above the form: it has the focus, so it is read out first.
        await page.GotoAsync(SessionAuthentication.SignInPath);
        await page.FillAsync("#email", Ada);
        await page.FillAsync("#password", "not the password");
        await page.ClickAsync("main form button[type=submit]");
        await page.WaitForSelectorAsync("main .form-error");
        await FocusSettlesAsync(page);

        Assert.Equal("p.form-error", await FocusAsync(page));
        Assert.Equal("The email address or the password is not right.", await page.EvaluateAsync<string>("() => document.activeElement.textContent"));

        // A form that names fields as wrong, and says nothing above it: the first of them has
        // the focus, shows the line, and names what says why. The browser would hold such a
        // form back by itself, so it is told not to: the server answers it here.
        await page.GotoAsync(WebUiModule.RegisterPath);
        await page.EvaluateAsync("() => { document.querySelector('main form').noValidate = true; }");
        await page.FillAsync("#email", "not-an-address");
        await page.FillAsync("#password", "short");
        await page.ClickAsync("main form button[type=submit]");
        await page.WaitForSelectorAsync("#email-error");
        await FocusSettlesAsync(page);

        Assert.Equal("input#email", await FocusAsync(page));
        Assert.True(await OutlinedAsync(page), "The field that was refused has the focus without the line that says so.");
        Assert.Equal(
            "Enter an email address, such as name@example.com.",
            await page.EvaluateAsync<string>("() => document.getElementById(document.activeElement.getAttribute('aria-describedby')).textContent"));
        Assert.Empty(said);
    }

    [Fact]
    public async Task The_menu_of_a_narrow_screen_opens_and_closes_by_the_keyboard()
    {
        DrivenBrowser.IsAskedFor();

        using var application = new AccountsApplication(listens: true);
        await application.RegisterVerifiedAsync(Ada);
        await using var phone = await (await run.StartedAsync()).OpenAsync(application.ListeningAt, DrivenBrowser.Phone);
        var page = await phone.NewPageAsync();
        var said = Listen(page);

        await SignInAsync(page, Ada);
        await page.GotoAsync(WebUiModule.DashboardPath);
        await FocusSettlesAsync(page);

        // Closed, what it holds does not show and is no stop of the keyboard.
        var menu = page.Locator("details.site-menu");
        var signOut = page.Locator($"#{Screens.SignOutFormId} button");

        Assert.False(await signOut.IsVisibleAsync());

        // What opens it is a stop of the keyboard, and Enter opens it.
        await page.FocusAsync("details.site-menu > summary");
        Assert.True(await OutlinedAsync(page), "What opens the menu has the focus without the line that says so.");

        await page.Keyboard.PressAsync("Enter");

        Assert.NotNull(await menu.GetAttributeAsync("open"));
        Assert.True(await signOut.IsVisibleAsync());

        // What it holds comes next: the button that signs out, and then the links.
        await page.Keyboard.PressAsync("Tab");
        Assert.Equal("button", await FocusAsync(page));
        Assert.Equal(Screens.SignOut, await page.EvaluateAsync<string>("() => document.activeElement.textContent"));

        await page.Keyboard.PressAsync("Tab");
        Assert.Equal(WebUiModule.DashboardPath, await page.EvaluateAsync<string>("() => document.activeElement.getAttribute('href')"));

        // And the same key closes it again.
        await page.FocusAsync("details.site-menu > summary");
        await page.Keyboard.PressAsync("Enter");

        Assert.Null(await menu.GetAttributeAsync("open"));
        Assert.False(await signOut.IsVisibleAsync());

        // The page does not scroll sideways at this width, open or closed.
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"));
        Assert.Empty(said);
    }

    [Fact]
    public async Task The_notice_before_a_session_ends_comes_is_answered_and_lets_the_session_of_whoever_says_nothing_end()
    {
        DrivenBrowser.IsAskedFor();

        // The shortest period that the file allows: a minute, with half a minute of notice. The
        // application goes by the clock of the machine here, as the browser does.
        using var application = new AccountsApplication(listens: true, settings: file => file.With("Security:SessionIdleMinutes", 1));
        await application.RegisterVerifiedAsync(Ada);
        await application.RegisterVerifiedAsync(Grace);

        var browser = await run.StartedAsync();

        // Two people at once, each in a browser of their own: each waits half a minute, twice.
        await Task.WhenAll(StaysThenSaysNothingAsync(), ClosesThenSignsOutAsync());

        async Task StaysThenSaysNothingAsync()
        {
            await using var hers = await browser.OpenAsync(application.ListeningAt);
            var page = await hers.NewPageAsync();
            var said = Listen(page);

            await SignInAsync(page, Ada);
            await page.GotoAsync(WebUiModule.AccountPath);
            await FocusSettlesAsync(page);

            // Half a minute before the end the dialog opens, with the focus on the button that stays.
            await OpensAsync(page);

            Assert.Equal(SessionPages.Title, await page.TextContentAsync("#session-warning-title"));
            Assert.Matches(@"you will be signed out in (?:about a minute|less than a minute), at \d\d:\d\d UTC,", await page.TextContentAsync("#session-warning-text") ?? string.Empty);
            Assert.True(await page.EvaluateAsync<bool>("() => document.activeElement === document.querySelector('#session-warning button[formaction]')"));
            Assert.True(await OutlinedAsync(page), "The button that stays has the focus without the line that says so.");
            Assert.True(await page.EvaluateAsync<bool>("() => document.getElementById('session-warning-failed').hidden"));

            // The keyboard stays out of the page behind the dialog: the next stop is its other
            // button, then what the browser itself has, and then its first button again.
            await page.Keyboard.PressAsync("Tab");
            Assert.Equal(SessionPages.SignOutNow, await page.EvaluateAsync<string>("() => document.activeElement.textContent"));

            for (var stops = 0; stops < 3 && !await page.EvaluateAsync<bool>("() => document.activeElement === document.querySelector('#session-warning button[formaction]')"); stops++)
            {
                await page.Keyboard.PressAsync("Tab");

                Assert.True(
                    await page.EvaluateAsync<bool>("() => document.activeElement === document.body || document.getElementById('session-warning').contains(document.activeElement)"),
                    $"With the notice open, the keyboard stops at {await FocusAsync(page)}, which is behind it.");
            }

            Assert.Equal(SessionPages.Stay, await page.EvaluateAsync<string>("() => document.activeElement.textContent"));

            // She stays: the dialog closes, the focus is back where it was, and the page is the same page.
            await page.Keyboard.PressAsync("Enter");
            await ClosesAsync(page);

            Assert.Equal("h1", await FocusAsync(page));
            Assert.EndsWith(WebUiModule.AccountPath, page.Url);

            // It comes again, and she says nothing: the session ends, and the page leads to the
            // page that signs in, and from there back.
            await OpensAsync(page);
            await page.WaitForURLAsync($"**{SessionAuthentication.SignInPath}?**", new() { Timeout = 60_000 });

            Assert.Contains("returnUrl=%2Faccount", page.Url);
            Assert.Empty(said);
        }

        async Task ClosesThenSignsOutAsync()
        {
            await using var hers = await browser.OpenAsync(application.ListeningAt);
            var page = await hers.NewPageAsync();
            var said = Listen(page);

            await SignInAsync(page, Grace);
            await page.GotoAsync(WebUiModule.AccountPath);
            await FocusSettlesAsync(page);

            // The key that closes a dialog says that she is still there, as the button does.
            await OpensAsync(page);
            await page.Keyboard.PressAsync("Escape");
            await ClosesAsync(page);

            Assert.EndsWith(WebUiModule.AccountPath, page.Url);

            // It comes again, and the other button signs her out at once: it sends the form of the bar.
            await OpensAsync(page);
            await page.ClickAsync("#session-warning button:not([formaction])");
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/", new() { Timeout = Soon });

            Assert.Equal(0, await page.Locator($"#{Screens.SignOutFormId}").CountAsync());
            Assert.Equal(0, await page.Locator("#session-warning").CountAsync());
            Assert.Empty(said);
        }

        // The dialog opens by itself, half a minute after its user last did something.
        static Task OpensAsync(IPage page) =>
            page.WaitForSelectorAsync("#session-warning[open]", new() { Timeout = 60_000 });

        static Task ClosesAsync(IPage page) =>
            page.WaitForSelectorAsync("#session-warning:not([open])", new() { State = WaitForSelectorState.Attached, Timeout = Soon });
    }

    [Fact]
    public async Task A_device_says_its_time_zone_as_its_user_signs_in_and_the_pages_say_their_times_in_it()
    {
        DrivenBrowser.IsAskedFor();

        using var application = new AccountsApplication(listens: true);
        await application.RegisterVerifiedAsync(Ada);

        var browser = await run.StartedAsync();

        // A device that is not in UTC (NFR-USA-3), in a time zone that a browser and the time
        // zone database have under one name. Chromium says an older name for some, such as
        // Asia/Calcutta for Asia/Kolkata: PageTimesTests holds that those are understood.
        await using var device = await browser.OpenAsync(application.ListeningAt, timeZone: "Asia/Tokyo");
        var page = await device.NewPageAsync();
        var said = Listen(page);

        // The script of the times writes what the browser says into the field of the form.
        await page.GotoAsync(SessionAuthentication.SignInPath);
        await page.WaitForFunctionAsync("() => document.querySelector('input[data-time-zone]')?.value !== ''", null, new() { Timeout = Soon });

        Assert.Equal("Asia/Tokyo", await page.InputValueAsync("input[data-time-zone]"));

        await SignInAsync(page, Ada);

        // The server keeps it on the device, where no script reads it.
        var kept = Assert.Single(await device.CookiesAsync(), cookie => cookie.Name == "__Host-ecnanib-time-zone");

        Assert.Equal("Asia%2FTokyo", kept.Value);
        Assert.True(kept.HttpOnly);
        Assert.True(kept.Secure);
        Assert.DoesNotContain("time-zone", await page.EvaluateAsync<string>("() => document.cookie"), StringComparison.Ordinal);

        // The page of the appearance says what the device said, with the device chosen.
        await page.GotoAsync(WebUiModule.AppearancePath);

        Assert.Contains("its time zone is Asia/Tokyo, which is UTC+9 now.", await page.TextContentAsync("main") ?? string.Empty);
        Assert.True(await page.IsCheckedAsync("#times-device"));

        // UTC is one choice away, and back: the form carries what the device says now, so the
        // page that comes back says it still.
        await page.CheckAsync("#times-utc");
        await page.ClickAsync("main form:has(#times-utc) button[type=submit]");
        await page.WaitForSelectorAsync("#times-utc:checked", new() { Timeout = Soon });

        await page.CheckAsync("#times-device");
        await page.ClickAsync("main form:has(#times-device) button[type=submit]");
        await page.WaitForSelectorAsync("#times-device:checked", new() { Timeout = Soon });

        Assert.Contains("its time zone is Asia/Tokyo, which is UTC+9 now.", await page.TextContentAsync("main") ?? string.Empty);
        Assert.Empty(said);
    }

    [Fact]
    public async Task A_page_that_follows_is_paused_by_its_link_and_follows_again()
    {
        DrivenBrowser.IsAskedFor();

        using var application = new AccountsApplication(listens: true);
        await application.RegisterVerifiedAsync(Ada);
        await using var hers = await (await run.StartedAsync()).OpenAsync(application.ListeningAt);
        var page = await hers.NewPageAsync();
        var said = Listen(page);

        // The connections that the page makes, and which of them are closed again.
        var made = 0;
        var closed = 0;

        page.WebSocket += (_, connection) =>
        {
            Interlocked.Increment(ref made);
            connection.Close += (_, _) => Interlocked.Increment(ref closed);
        };

        await SignInAsync(page, Ada);
        await page.GotoAsync(WebUiModule.DashboardPath);
        await FocusSettlesAsync(page);

        // A page that follows makes its one connection, and says that it follows.
        await UntilAsync(() => made == 1, "The dashboard made no connection.");

        var link = page.Locator(".live-pause a");
        var status = page.Locator(".live-pause [role=status]");

        Assert.Equal(LivePages.Follows, await status.TextContentAsync());
        Assert.Equal(LivePages.Pause, await link.TextContentAsync());

        // Its link pauses it, by keyboard: the page is the same page, the focus stays on the
        // link, which is then the other one, and what the page says of itself has changed.
        await link.FocusAsync();
        Assert.True(await OutlinedAsync(page), "The link that pauses has the focus without the line that says so.");

        await page.EvaluateAsync("() => { window.stayed = true; }");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync($"**{WebUiModule.DashboardPath}?{LivePages.PauseKey}={LivePages.Paused}");
        await page.WaitForFunctionAsync($"() => document.querySelector('.live-pause a')?.textContent === '{LivePages.Resume}'", null, new() { Timeout = Soon });

        Assert.True(await page.EvaluateAsync<bool>("() => window.stayed === true"), "The browser left the page to pause it.");
        Assert.True(await page.EvaluateAsync<bool>("() => document.activeElement === document.querySelector('.live-pause a')"), "The link that paused the page lost the focus.");
        Assert.StartsWith("Live updates are paused. This page shows what was so at ", await status.TextContentAsync());

        // The connection is given up within moments, and the page makes no other.
        await UntilAsync(() => closed == 1, "The paused dashboard kept its connection.");
        Assert.Equal(1, made);

        // The same link has it follow again, with a connection of its own.
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync($"() => document.querySelector('.live-pause a')?.textContent === '{LivePages.Pause}'", null, new() { Timeout = Soon });
        await UntilAsync(() => made == 2, "The dashboard that follows again made no connection.");

        Assert.EndsWith(WebUiModule.DashboardPath, page.Url);
        Assert.Equal(LivePages.Follows, await status.TextContentAsync());
        Assert.Empty(said);
    }

    [Fact]
    public async Task A_page_that_asks_for_itself_again_keeps_the_focus_where_it_was()
    {
        DrivenBrowser.IsAskedFor();

        using var application = new AccountsApplication(listens: true);
        var ada = await application.RegisterVerifiedAsync(Ada);
        await using var hers = await (await run.StartedAsync()).OpenAsync(application.ListeningAt);
        var page = await hers.NewPageAsync();
        var said = Listen(page);

        await SignInAsync(page, Ada);
        await page.GotoAsync(WebUiModule.NotificationsPath);
        await FocusSettlesAsync(page);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.Equal("h1#notifications-heading", await FocusAsync(page));

        // Something that the page shows changes: the page asks for itself again, and the
        // browser changes it in its place. The heading had the focus by a mark of the
        // framework, which the browser takes away then: the script of the pages puts it back.
        await page.EvaluateAsync("() => { window.asked = 0; Blazor.addEventListener('enhancedload', () => { window.asked++; }); }");
        await application.GiveNotificationAsync(ada.Id, agentId: null, DateTimeOffset.UtcNow);
        await page.WaitForFunctionAsync("() => window.asked > 0 && document.querySelector('.notification') !== null", null, new() { Timeout = Soon });

        Assert.Equal("h1#notifications-heading", await FocusAsync(page));

        // A control that has the focus keeps it by itself, also when the page changes around it.
        await page.FocusAsync(".live-pause a");
        await application.GiveNotificationAsync(ada.Id, agentId: null, DateTimeOffset.UtcNow);
        await page.WaitForFunctionAsync("() => document.querySelectorAll('.notification').length > 1", null, new() { Timeout = Soon });

        Assert.Equal("a", await FocusAsync(page));
        Assert.Equal(LivePages.Pause, await page.EvaluateAsync<string>("() => document.activeElement.textContent"));
        Assert.Empty(said);
    }

    /// <summary>
    /// Fails when the browser found something at a state, and names every such state with what
    /// was found, and the file that has all of it.
    /// </summary>
    private static void AssertNothing(IReadOnlyList<StateFindings> found, Func<StateFindings, IReadOnlyList<string>> of, string broken)
    {
        var states = found.Where(state => of(state).Count > 0).ToList();

        Assert.True(
            states.Count == 0,
            $"{states.Count} of the {found.Count} states {broken}. All that the run found is in {AccessibilityBrowserRun.File}:{Environment.NewLine}"
            + string.Join(
                Environment.NewLine,
                states.Select(state => $"  {state.Named}:{Environment.NewLine}" + string.Join(Environment.NewLine, of(state).Select(finding => $"    {finding}")))));
    }

    /// <summary>A page that keeps to what is checked, with the focus of the stylesheet of the application, and what a test plants in its content.</summary>
    private static string Planted(string markup, string style = "") =>
        $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <title>A planted page</title>
            <style>
                :focus-visible { outline: 2px solid CanvasText; outline-offset: 2px; }
                h1:focus { outline: none; }
                {{style}}
            </style>
        </head>
        <body>
            <main>
                <h1 tabindex="-1">A planted page</h1>
                {{markup}}
            </main>
        </body>
        </html>
        """;

    /// <summary>Signs a person in on the form, as they do.</summary>
    private static async Task SignInAsync(IPage page, string email)
    {
        await page.GotoAsync(SessionAuthentication.SignInPath);
        await page.FillAsync("#email", email);
        await page.FillAsync("#password", AccountsApplication.Password);
        await page.ClickAsync("main form button[type=submit]");
        await page.WaitForSelectorAsync($"#{Screens.SignOutFormId}", new() { State = WaitForSelectorState.Attached });
    }

    /// <summary>Waits until the page has given the focus to something, as it does once it is read.</summary>
    private static Task FocusSettlesAsync(IPage page) =>
        page.WaitForFunctionAsync("() => document.activeElement && document.activeElement !== document.body", null, new() { Timeout = Soon });

    /// <summary>What has the focus, as a selector says it: its element, its id, and its classes.</summary>
    private static Task<string> FocusAsync(IPage page) =>
        page.EvaluateAsync<string>(
            """
            () => {
                const has = document.activeElement;
                if (!has || has === document.body) return "nothing";
                const classes = typeof has.className === "string" && has.className.trim() ? "." + has.className.trim().split(/\s+/).join(".") : "";
                return has.tagName.toLowerCase() + (has.id ? "#" + has.id : "") + classes;
            }
            """);

    /// <summary>Whether what has the focus shows the line of the stylesheet around it: two pixels, solid.</summary>
    private static Task<bool> OutlinedAsync(IPage page) =>
        page.EvaluateAsync<bool>(
            """
            () => {
                const look = getComputedStyle(document.activeElement);
                return look.outlineStyle === "solid" && parseFloat(look.outlineWidth) >= 2;
            }
            """);

    /// <summary>
    /// What the browser says of a page while a test drives it: an error of a script, and a
    /// report of the content security policy. A page that works says neither.
    /// </summary>
    private static ConcurrentQueue<string> Listen(IPage page)
    {
        var said = new ConcurrentQueue<string>();

        page.PageError += (_, error) => said.Enqueue(error);
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                said.Enqueue(message.Text);
            }
        };

        return said;
    }

    /// <summary>Waits for what a page does by itself and a test only counts, for some seconds.</summary>
    private static async Task UntilAsync(Func<bool> holds, string otherwise)
    {
        for (var waited = 0; waited < 100 && !holds(); waited++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.True(holds(), otherwise);
    }
}
