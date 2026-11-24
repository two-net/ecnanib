using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Persistence;
using Ecnanib.WebUi;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ecnanib.Tests.Themes;

/// <summary>
/// The user can choose Dark, Light, or System, and System is the default for new users and for
/// visitors (FR-THM-1). The choice is saved to the account of a user and applied on every device
/// that they sign in on, and for a visitor it is saved on the device (FR-THM-3). The server
/// writes the theme into every page that it answers with, ahead of the stylesheets, so the page
/// is in its theme before the first paint (FR-THM-4). See docs/themes.md.
/// </summary>
/// <remarks>
/// No test drives a browser. What is held here is what a browser is sent: the first element of
/// a page, the cookie of a device, and the row of an account. That a page which says nothing
/// follows the device, and changes with it, is the stylesheet's: see <see cref="ThemeStyleTests"/>.
/// </remarks>
public partial class ThemePageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string OfAccount = WebUiModule.AppearancePath;
    private const string OfDevice = WebUiModule.DeviceAppearancePath;
    private const string Form = "theme";
    private const string Field = "Theme";

    private const string Cookie = "__Host-ecnanib-theme";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void The_themes_are_dark_light_and_system_and_are_stored_by_these_names()
    {
        // A member is stored by its name, so a member that is renamed needs a migration.
        Assert.Equal(["System", "Dark", "Light"], Enum.GetNames<Theme>());

        // The one of a new user, and of a visitor, is the one that nothing needs to say.
        Assert.Equal(Theme.System, default(Theme));
    }

    [Fact]
    public async Task A_visitor_who_chose_nothing_is_shown_the_pages_as_their_device_is_set()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClientThatStays();

        using var answer = await client.GetAsync("/", Cancellation);
        var page = await answer.Content.ReadAsStringAsync(Cancellation);

        // The page names no theme, and says that it may be either: the stylesheet follows the device.
        Assert.Equal("<html lang=\"en\">", RootOf(page));
        Assert.Equal("light dark", ColorSchemeOf(page));

        // Nothing is saved on the device for it: the one cookie of the answer is the
        // framework's, for the token against request forgery.
        Assert.DoesNotContain(Browser.CookiesSetBy(answer), cookie => cookie.StartsWith(Cookie, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_user_has_the_theme_of_their_device()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        Assert.Equal("System", ThemeOf(application, Ada));

        var page = await client.PageAsync(OfAccount);

        Assert.Equal("<html lang=\"en\">", RootOf(page));
        Assert.Equal("light dark", ColorSchemeOf(page));
        Assert.Equal("system", ChosenOn(page));
    }

    [Fact]
    public async Task An_account_from_before_the_themes_has_the_theme_of_its_device()
    {
        // A database of the version before, in which an account has no theme.
        var database = PostgresServer.CreateDatabase();
        using (var before = database.Context())
        {
            before.GetService<IMigrator>().Migrate("20261124071900_AddEquitySignal");
        }

        database.Execute(
            $"""
            INSERT INTO users (id, email, password_hash, registered_at, email_verification_sets_password, two_factor_failed_codes)
            VALUES ('{Guid.CreateVersion7()}', '{Ada}', 'not-a-hash', now(), FALSE, 0)
            """);

        // Starting this version migrates the database.
        using var application = new AccountsApplication(database: database);
        _ = application.Registration;

        Assert.Equal("System", ThemeOf(application, Ada));
    }

    [Fact]
    public async Task A_user_chooses_a_theme_and_every_page_is_written_in_it()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The page of the appearance among the settings offers the three, each with what it means.
        var before = await client.PageAsync(OfAccount);
        Assert.Contains("<h1>Appearance</h1>", before);
        Assert.Equal(["system", "dark", "light"], ChoicesOn(before));
        Assert.Contains("<label for=\"theme-system\">System</label>", before);
        Assert.Contains("<label for=\"theme-dark\">Dark</label>", before);
        Assert.Contains("<label for=\"theme-light\">Light</label>", before);
        Assert.Contains("The pages are light or dark as your device is set, and change when its setting does.", before);
        Assert.Contains("on every device you sign in on.", before);

        var (path, after) = await client.SubmitAsync(OfAccount, Form, (Field, "dark"));

        // The page that comes back is the page of the appearance, in the theme that was chosen.
        Assert.Equal(OfAccount, path);
        Assert.Equal("<html lang=\"en\" data-theme=\"dark\">", RootOf(after));
        Assert.Equal("dark", ColorSchemeOf(after));
        Assert.Equal("dark", ChosenOn(after));

        // It is saved to the account, by the name of the theme.
        Assert.Equal("Dark", ThemeOf(application, Ada));

        // Every page is written in it from then on: a page of the user, the start page, a legal
        // document, and the page of an address that has none.
        foreach (var address in new[] { "/", WebUiModule.DashboardPath, "/account", "/legal/terms" })
        {
            Assert.Equal("<html lang=\"en\" data-theme=\"dark\">", RootOf(await client.PageAsync(address)));
        }

        var (_, missing) = await client.OpenAsync("/no-such-page");
        Assert.Contains("Page not found", missing);
        Assert.Equal("<html lang=\"en\" data-theme=\"dark\">", RootOf(missing));

        // Light, and back to the device.
        (_, after) = await client.SubmitAsync(OfAccount, Form, (Field, "light"));
        Assert.Equal("<html lang=\"en\" data-theme=\"light\">", RootOf(after));
        Assert.Equal("light", ColorSchemeOf(after));
        Assert.Equal("Light", ThemeOf(application, Ada));

        (_, after) = await client.SubmitAsync(OfAccount, Form, (Field, "system"));
        Assert.Equal("<html lang=\"en\">", RootOf(after));
        Assert.Equal("light dark", ColorSchemeOf(after));
        Assert.Equal("System", ThemeOf(application, Ada));
    }

    [Fact]
    public async Task The_theme_is_in_a_page_ahead_of_its_stylesheets_and_no_script_puts_it_there()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.SubmitAsync(OfAccount, Form, (Field, "dark"));

        var page = await client.PageAsync(WebUiModule.DashboardPath);

        // The first element says the theme, and the browser is told which colours the page may
        // have, before the first stylesheet is named: nothing is painted in the other theme
        // while a stylesheet is on its way.
        var root = Root().Match(page);
        var scheme = ColorScheme().Match(page);
        var stylesheet = page.IndexOf("<link rel=\"stylesheet\"", StringComparison.Ordinal);

        Assert.True(root.Success && scheme.Success && stylesheet > 0);
        Assert.True(root.Index < scheme.Index && scheme.Index < stylesheet);

        // And no script is ahead of them, which could have put it there after the fact.
        Assert.DoesNotContain("<script", page[..stylesheet]);
    }

    [Fact]
    public async Task The_theme_of_an_account_holds_on_every_device_that_its_user_signs_in_on()
    {
        using var application = new AccountsApplication();
        using var desk = application.CreateClient();
        using var phone = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        await desk.SignInAsync(Ada);
        await desk.SubmitAsync(OfAccount, Form, (Field, "dark"));

        // The first page that another browser is shown after signing in is in it.
        var (path, start) = await phone.SignInAsync(Ada);

        Assert.Equal("/", path);
        Assert.Equal("<html lang=\"en\" data-theme=\"dark\">", RootOf(start));

        // A change on one device is on the other with its next page.
        await phone.SubmitAsync(OfAccount, Form, (Field, "light"));

        Assert.Equal("<html lang=\"en\" data-theme=\"light\">", RootOf(await desk.PageAsync(WebUiModule.DashboardPath)));

        // Also for a page that asks for itself again without leaving the page that it is.
        await phone.SubmitAsync(OfAccount, Form, (Field, "dark"));

        Assert.Equal("<html lang=\"en\" data-theme=\"dark\">", RootOf(await desk.AskAgainAsync(WebUiModule.DashboardPath)));
    }

    [Fact]
    public async Task The_choice_of_a_user_is_saved_to_the_account_and_not_on_the_device()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClientThatStays();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        using var answer = await client.SendFormAsync(
            OfAccount, Form, Browser.AntiforgeryTokenIn(await client.PageAsync(OfAccount)), (Field, "dark"));

        // The answer leads back to the page, by a request that changes nothing.
        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal(OfAccount, new Uri(client.BaseAddress!, answer.Headers.Location!).AbsolutePath);

        Assert.Equal("Dark", ThemeOf(application, Ada));
        Assert.DoesNotContain(Browser.CookiesSetBy(answer), cookie => cookie.StartsWith(Cookie, StringComparison.Ordinal));

        // The page of the account says what is stored, and leads to where it is chosen.
        var account = await client.PageAsync("/account");
        Assert.Contains("Theme: Dark. Your choice is stored with your account, and is in a download.", account);
        Assert.Contains($"<a href=\"{OfAccount}\">Choose how the pages look</a>", account);

        // And it is in the download of what is stored about the user.
        var export = JsonDocument.Parse(await client.PageAsync(WebUiModule.ExportPath)).RootElement;
        Assert.Equal("Dark", export.GetProperty("account").GetProperty("theme").GetString());
    }

    [Fact]
    public async Task The_choice_of_one_user_is_not_the_choice_of_another()
    {
        using var application = new AccountsApplication();
        using var ada = application.CreateClient();
        using var grace = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await application.RegisterVerifiedAsync(Grace);
        await ada.SignInAsync(Ada);
        await grace.SignInAsync(Grace);

        // The form names no user: the choice is of whoever is signed in.
        await ada.SubmitAsync(OfAccount, Form, (Field, "dark"));

        Assert.Equal("Dark", ThemeOf(application, Ada));
        Assert.Equal("System", ThemeOf(application, Grace));
        Assert.Equal("<html lang=\"en\">", RootOf(await grace.PageAsync(OfAccount)));
    }

    [Fact]
    public async Task A_visitor_chooses_a_theme_on_a_page_of_their_own_and_it_is_saved_on_the_device()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClientThatStays();
        using var another = application.CreateClient();

        // The page needs nobody to be signed in, and offers the same three.
        var before = await client.PageAsync(OfDevice);
        Assert.Contains("<h1>Appearance</h1>", before);
        Assert.Equal(["system", "dark", "light"], ChoicesOn(before));
        Assert.Equal("system", ChosenOn(before));
        Assert.Contains("Your choice is saved on this device, in this browser.", before);

        using var answer = await client.SendFormAsync(OfDevice, Form, Browser.AntiforgeryTokenIn(before), (Field, "dark"));

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal(OfDevice, new Uri(client.BaseAddress!, answer.Headers.Location!).AbsolutePath);

        // One cookie, for this site alone, sent only over TLS and hidden from scripts, which
        // the browser keeps for a year: a choice does not end with the browser.
        var saved = Assert.Single(Browser.CookiesSetBy(answer));
        Assert.StartsWith($"{Cookie}=dark; ", saved);
        Assert.Equal(
            ["httponly", "max-age=31536000", "path=/", "samesite=lax", "secure"],
            saved.Split("; ")[1..].Order(StringComparer.Ordinal));

        // The pages of that browser are in the theme from then on, and those of another are not.
        var after = await client.PageAsync(OfDevice);
        Assert.Equal("<html lang=\"en\" data-theme=\"dark\">", RootOf(after));
        Assert.Equal("dark", ColorSchemeOf(after));
        Assert.Equal("dark", ChosenOn(after));
        Assert.Equal("<html lang=\"en\" data-theme=\"dark\">", RootOf(await client.PageAsync("/")));
        Assert.Equal("<html lang=\"en\">", RootOf(await another.PageAsync("/")));

        // Nothing is stored about a visitor.
        Assert.Equal(0L, application.Database.Scalar<long>("SELECT count(*) FROM users"));

        // A device that is to look as it is set needs no cookie, and its cookie is taken back.
        using var back = await client.SendFormAsync(OfDevice, Form, Browser.AntiforgeryTokenIn(after), (Field, "system"));

        Assert.StartsWith($"{Cookie}=; expires=Thu, 01 Jan 1970", Assert.Single(Browser.CookiesSetBy(back)));
        Assert.Equal("<html lang=\"en\">", RootOf(await client.PageAsync("/")));
    }

    [Theory]
    [InlineData("sepia")]
    [InlineData("Dark")]
    [InlineData("dark\"><script>")]
    [InlineData("")]
    public async Task A_cookie_that_names_no_theme_is_no_choice(string value)
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("Cookie", $"{Cookie}={value}");

        using var answer = await client.SendAsync(request, Cancellation);
        var page = await answer.Content.ReadAsStringAsync(Cancellation);

        // What a cookie says is written into no page: the page is as the device is set.
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("<html lang=\"en\">", RootOf(page));
        Assert.Equal("light dark", ColorSchemeOf(page));
    }

    [Fact]
    public async Task The_theme_of_an_account_counts_while_its_user_is_signed_in_and_the_one_of_the_device_otherwise()
    {
        using var application = new AccountsApplication();
        using var desk = application.CreateClient();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        // The account is dark, and this device is light.
        await desk.SignInAsync(Ada);
        await desk.SubmitAsync(OfAccount, Form, (Field, "dark"));
        await client.SubmitAsync(OfDevice, Form, (Field, "light"));

        Assert.Equal("<html lang=\"en\" data-theme=\"light\">", RootOf(await client.PageAsync(SessionAuthentication.SignInPath)));

        var (_, start) = await client.SignInAsync(Ada);
        Assert.Equal("<html lang=\"en\" data-theme=\"dark\">", RootOf(start));

        // A user who opens the page of a visitor is sent to the one among the settings: the
        // choice that counts for them is the one of their account.
        Assert.Equal(OfAccount, (await client.OpenAsync(OfDevice)).Address);

        // Signed out, the device has its own choice again, which signing in did not touch.
        var (_, signedOut) = await client.SignOutAsync();
        Assert.Equal("<html lang=\"en\" data-theme=\"light\">", RootOf(signedOut));
        Assert.Equal("Dark", ThemeOf(application, Ada));
    }

    [Fact]
    public async Task A_form_that_names_no_theme_saves_nothing()
    {
        using var application = new AccountsApplication();
        using var visitor = application.CreateClientThatStays();
        using var user = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await user.SignInAsync(Ada);
        await user.SubmitAsync(OfAccount, Form, (Field, "dark"));

        foreach (var fields in new (string, string)[][] { [(Field, "sepia")], [(Field, "Dark")], [(Field, "1")], [] })
        {
            // The page of a user comes back as it was, and says what it takes.
            var (path, page) = await user.SubmitAsync(OfAccount, Form, fields);

            Assert.Equal(OfAccount, path);
            Assert.Contains("<p class=\"form-error\" role=\"alert\">Choose System, Dark, or Light.</p>", page);
            Assert.Equal("dark", ChosenOn(page));
            Assert.Equal("Dark", ThemeOf(application, Ada));

            // And so does the page of a visitor, whose device is sent no cookie for it.
            using var answer = await visitor.SendFormAsync(
                OfDevice, Form, Browser.AntiforgeryTokenIn(await visitor.PageAsync(OfDevice)), fields);

            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.Contains("Choose System, Dark, or Light.", await answer.Content.ReadAsStringAsync(Cancellation));
            Assert.DoesNotContain(Browser.CookiesSetBy(answer), cookie => cookie.StartsWith(Cookie, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_form_without_the_token_of_its_page_is_refused()
    {
        using var application = new AccountsApplication();
        using var visitor = application.CreateClientThatStays();
        using var user = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await user.SignInAsync(Ada);

        // A form that another site posts for a user has no token of a page of this browser.
        using var ofAccount = await user.SendFormAsync(OfAccount, Form, "not-the-token", (Field, "dark"));

        Assert.Equal(HttpStatusCode.BadRequest, ofAccount.StatusCode);
        Assert.Equal("System", ThemeOf(application, Ada));

        // And the device of a visitor is sent no cookie by one.
        await visitor.PageAsync(OfDevice);
        using var ofDevice = await visitor.SendFormAsync(OfDevice, Form, "not-the-token", (Field, "dark"));

        Assert.Equal(HttpStatusCode.BadRequest, ofDevice.StatusCode);
        Assert.DoesNotContain(Browser.CookiesSetBy(ofDevice), cookie => cookie.StartsWith(Cookie, StringComparison.Ordinal));
        Assert.Equal("<html lang=\"en\">", RootOf(await visitor.PageAsync("/")));
    }

    [Fact]
    public async Task The_foot_of_a_page_leads_a_visitor_to_the_appearance_and_a_user_is_led_there_by_the_settings()
    {
        using var application = new AccountsApplication();
        using var visitor = application.CreateClient();
        using var user = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await user.SignInAsync(Ada);

        // A visitor: at the foot of every page, under the legal documents, and marked on the
        // page that it leads to.
        Assert.Contains($"<a href=\"{OfDevice}\">Appearance</a>", FootOf(await visitor.PageAsync("/")));
        Assert.Contains($"<a href=\"{OfDevice}\">Appearance</a>", FootOf(await visitor.PageAsync(SessionAuthentication.SignInPath)));
        Assert.Contains($"<a href=\"{OfDevice}\" aria-current=\"page\">Appearance</a>", FootOf(await visitor.PageAsync(OfDevice)));

        // A visitor who asks for the page among the settings is sent to sign in, as on every
        // page of the settings.
        Assert.Equal(
            SessionAuthentication.PageWith(SessionAuthentication.SignInPath, OfAccount),
            (await visitor.OpenAsync(OfAccount)).Address);

        // A user: the foot does not lead there, and the navigation of the settings does.
        var page = await user.PageAsync("/account");

        Assert.DoesNotContain($"href=\"{OfDevice}\"", page);
        Assert.Contains($"<a href=\"{OfAccount}\">Appearance</a>", page);
    }

    /// <summary>The theme that the row of an account says.</summary>
    private static string ThemeOf(AccountsApplication application, string email) =>
        application.Database.Scalar<string>($"SELECT theme FROM users WHERE email = '{email}'");

    /// <summary>The first element of a page, which says its theme.</summary>
    private static string RootOf(string page) => Root().Match(page).Value;

    /// <summary>Which of light and dark a page tells the browser that it may be.</summary>
    private static string ColorSchemeOf(string page) => Assert.Single(ColorScheme().Matches(page)).Groups[1].Value;

    /// <summary>The themes that a page offers, in its order.</summary>
    private static string[] ChoicesOn(string page) => [.. Choice().Matches(page).Select(choice => choice.Groups[1].Value)];

    /// <summary>The theme that is ticked on a page.</summary>
    private static string ChosenOn(string page) =>
        Assert.Single(Choice().Matches(page), choice => choice.Value.Contains(" checked", StringComparison.Ordinal)).Groups[1].Value;

    /// <summary>The foot of a page.</summary>
    private static string FootOf(string page) => Foot().Match(page).Value;

    [GeneratedRegex("<html[^>]*>")]
    private static partial Regex Root();

    [GeneratedRegex("<meta name=\"color-scheme\" content=\"([^\"]*)\"")]
    private static partial Regex ColorScheme();

    [GeneratedRegex("<input [^>]*type=\"radio\" name=\"Theme\" value=\"([^\"]*)\"[^>]*>")]
    private static partial Regex Choice();

    [GeneratedRegex(@"<footer[\s>].*?</footer>", RegexOptions.Singleline)]
    private static partial Regex Foot();
}
