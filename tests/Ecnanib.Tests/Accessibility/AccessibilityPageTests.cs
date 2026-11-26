using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Tests.Accounts;
using Ecnanib.WebUi;
using Ecnanib.WebUi.Layout;
using Ecnanib.WebUi.Pages.Legal;

namespace Ecnanib.Tests.Accessibility;

/// <summary>
/// What the frame and the forms do for a keyboard and a screen reader, page by page
/// (NFR-USA-1): what takes the focus when a page opens, where the focus is after a form, and
/// what the frame says to a screen reader that it shows nobody. The rules that hold for every
/// state of every screen are in <see cref="AccessibilityMarkupTests"/>: here is what the pages
/// of a few forms do, as a browser is sent them. See docs/accessibility.md.
/// </summary>
/// <remarks>
/// None of these tests drives a browser. Where the focus is in a browser, and that it shows, is
/// held by <c>AccessibilityBrowserTests</c>, which a run starts when it is told to.
/// </remarks>
public partial class AccessibilityPageTests
{
    private const string Ada = "ada@example.com";

    [Fact]
    public async Task Every_page_says_what_takes_the_focus_when_it_opens_its_heading_or_what_a_form_was_answered_with()
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
            // The framework is told once, by a selector: what stands above a form where the
            // page has that, and the heading of the page otherwise. A page that is only opened
            // has the heading, so the selector names it there.
            var selector = Assert.Single(FocusOnOpen().Matches(page));

            Assert.Equal(Screens.FocusOnLoad, WebUtility.HtmlDecode(selector.Groups[1].Value));
            Assert.Equal(1, Regex.Count(page, "<h1\\b"));
            Assert.DoesNotContain("class=\"form-error\"", page);
            Assert.DoesNotContain("class=\"form-done\"", page);
            Assert.DoesNotContain("autofocus", page);
        }

        // Each of the three is in the selector, in the content, and the heading only where the
        // content holds neither of the other two.
        Assert.Equal("main:not(:has(.form-error, .form-done)) h1, main .form-error, main .form-done", Screens.FocusOnLoad);
    }

    [Fact]
    public async Task What_stands_above_a_form_takes_the_focus_and_no_field_asks_for_it()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        // A sign-in that failed says so above the form, as an alert, and nothing else on the
        // page asks for the focus: the selector of the layout finds what was said.
        var (_, failed) = await client.SignInAsync(Ada, "not the password");

        Assert.Contains("<p class=\"form-error\" role=\"alert\">The email address or the password is not right.</p>", failed);
        Assert.DoesNotContain("autofocus", failed);
        Assert.DoesNotContain("aria-invalid", failed);

        // The page says what takes the focus as every page does: the selector finds what was said.
        Assert.Equal(Screens.FocusOnLoad, WebUtility.HtmlDecode(Assert.Single(FocusOnOpen().Matches(failed)).Groups[1].Value));
    }

    [Fact]
    public async Task A_form_that_names_fields_as_wrong_gives_the_focus_to_the_first_of_them_which_names_what_says_why()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        // Nothing that the form asks for: every field is refused, and nothing stands above it.
        var (_, page) = await client.SubmitAsync(WebUiModule.RegisterPath, "register", ("Email", "not-an-address"), ("Password", "short"));

        Assert.DoesNotContain("class=\"form-error\"", page);

        // The first field that was refused asks for the focus, and no other does.
        Assert.Equal(1, Regex.Count(page, "\\bautofocus\\b"));
        Assert.Matches("<input id=\"email\"[^>]*aria-invalid=\"true\"[^>]*aria-describedby=\"email-error\"[^>]*\\bautofocus\\b", page);

        // Each field that was refused is marked, and names what says why ahead of its hint.
        Assert.Matches("<input id=\"password\"[^>]*aria-invalid=\"true\"[^>]*aria-describedby=\"password-error password-hint\"", page);
        Assert.Matches("<p id=\"email-error\" class=\"field-error\">[^<]+</p>", page);
        Assert.Matches("<p id=\"password-error\" class=\"field-error\">[^<]+</p>", page);
        Assert.Equal(3, Regex.Count(page, "<p id=\"accept-[A-Za-z]+-error\" class=\"field-error\">"));

        // What says why is no alert of its own: its field says it, when the field has the focus.
        Assert.DoesNotMatch("class=\"field-error\"[^>]*role=", page);

        // With an address that is one, the next field that was refused has the focus.
        var (_, next) = await client.SubmitAsync(WebUiModule.RegisterPath, "register", ("Email", Ada), ("Password", "short"));

        Assert.Equal(1, Regex.Count(next, "\\bautofocus\\b"));
        Assert.Matches("<input id=\"password\"[^>]*\\bautofocus\\b", next);
        Assert.DoesNotMatch("<input id=\"email\"[^>]*aria-invalid", next);

        // A form that was only opened marks nothing, and nothing asks for the focus.
        var opened = await client.PageAsync(WebUiModule.RegisterPath);

        Assert.DoesNotContain("autofocus", opened);
        Assert.DoesNotContain("aria-invalid", opened);
        Assert.Matches("<input id=\"password\"[^>]*aria-describedby=\"password-hint\"", opened);
    }

    [Fact]
    public async Task A_link_that_opens_beside_a_form_says_so_in_its_own_text()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        var page = await client.PageAsync(WebUiModule.RegisterPath);
        var links = NewTab().Matches(page);

        // The three documents of the form, which open beside it so that what was typed stays.
        Assert.Equal(3, links.Count);
        Assert.All(links, link => Assert.EndsWith($" ({LegalPages.OpensInNewTab})", WebUtility.HtmlDecode(link.Groups[1].Value)));
    }

    [Fact]
    public async Task The_frame_says_to_a_screen_reader_what_it_shows_nobody()
    {
        using var application = new AccountsApplication();
        using var visitor = application.CreateClient();
        using var user = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await user.SignInAsync(Ada);

        foreach (var page in new[] { await visitor.PageAsync("/"), await user.PageAsync(WebUiModule.DashboardPath) })
        {
            // The line that says a page is on its way: after the content, not shown, and empty
            // until the script of what moves writes the sentence that the page brought.
            var loading = $"<p class=\"visually-hidden\" role=\"status\" data-loading=\"{Screens.Loading}\"";

            Assert.Contains(loading, page);
            Assert.True(page.IndexOf("</main>", StringComparison.Ordinal) < page.IndexOf(loading, StringComparison.Ordinal));
            Assert.Matches(Regex.Escape(loading) + "[^>]*></p>", page);

            // What the page says of an error that nothing handled is an alert, and what puts it
            // away is a button with a name, whose mark is hidden from a screen reader.
            Assert.Matches("<div id=\"blazor-error-ui\" role=\"alert\"", page);
            Assert.Matches($"<button type=\"button\" class=\"dismiss\" aria-label=\"{Screens.Dismiss}\"[^>]*><span aria-hidden=\"true\"[^>]*>[^<]+</span></button>", page);

            // The dialog that rejoins the server has a name, and what it says is one part that
            // is read out as it changes. The seconds that count down are left out of that.
            Assert.Matches(
                $"<dialog id=\"components-reconnect-modal\" data-nosnippet aria-label=\"{Screens.ServerConnection}\" aria-describedby=\"components-reconnect-said\"",
                page);
            Assert.Matches("<div id=\"components-reconnect-said\" role=\"status\"", page);
            Assert.Matches("<span aria-hidden=\"true\"[^>]*> in <span id=\"components-seconds-to-next-attempt\"[^>]*></span> seconds</span>", page);
            Assert.Equal(2, Regex.Count(page, "<button type=\"button\" id=\"components-(?:reconnect|resume)-button\""));
        }

        // The link of the foot to the document that is open says so, as the bar does of a screen.
        var terms = await visitor.PageAsync(LegalPages.TermsOfServicePath);

        Assert.Contains($"<a href=\"{LegalPages.TermsOfServicePath}\" aria-current=\"page\"", terms);
        Assert.DoesNotContain($"<a href=\"{LegalPages.PrivacyPolicyPath}\" aria-current", terms);
    }

    // What the framework is told to give the focus to, as a page writes it.
    [GeneratedRegex("<blazor-focus-on-navigate selector=\"([^\"]*)\"")]
    private static partial Regex FocusOnOpen();

    // A link that opens in a new tab, with its text.
    [GeneratedRegex("<a href=\"[^\"]*\" target=\"_blank\" rel=\"noopener\">([^<]*)</a>")]
    private static partial Regex NewTab();
}
