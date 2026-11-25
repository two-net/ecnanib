using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Persistence;
using Ecnanib.WebUi;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ecnanib.Tests.Animation;

/// <summary>
/// The user can reduce animation in settings (FR-ANI-4): the pages of a user move as their
/// device asks, which is what a new user and a visitor have, or reduced. The choice is saved to
/// the account, as the theme is, and the server writes it into every page that it answers with,
/// beside the theme and ahead of the stylesheets. See docs/motion.md.
/// </summary>
/// <remarks>
/// No test drives a browser. What is held here is what a browser is sent: the first element of
/// a page, and the row of an account. That a page which says "reduced" is given nothing that
/// moves, and that one which says nothing follows the device, is the stylesheet's: see
/// <see cref="MotionStyleTests"/>.
/// </remarks>
public partial class MotionPageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string OfAccount = WebUiModule.AppearancePath;
    private const string Form = "motion";
    private const string Field = "Motion";

    private const string Moving = "<html lang=\"en\">";
    private const string Reduced = "<html lang=\"en\" data-motion=\"reduced\">";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void The_choices_are_system_and_reduced_and_are_stored_by_these_names()
    {
        // A member is stored by its name, so a member that is renamed needs a migration. There
        // is none that has the pages move whatever the device asks.
        Assert.Equal(["System", "Reduced"], Enum.GetNames<Motion>());

        // The one of a new user, and of a visitor, is the one that nothing needs to say.
        Assert.Equal(Motion.System, default(Motion));
    }

    [Fact]
    public async Task The_pages_of_a_visitor_move_as_their_device_asks_and_nothing_is_stored_for_one()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClientThatStays();

        using var answer = await client.GetAsync("/", Cancellation);
        var page = await answer.Content.ReadAsStringAsync(Cancellation);

        // The page says nothing of it: the stylesheet follows the device.
        Assert.Equal(Moving, RootOf(page));

        // Nothing is saved on the device for it, and the page of the appearance of a visitor
        // offers the theme alone.
        Assert.DoesNotContain(Browser.CookiesSetBy(answer), cookie => cookie.Contains("motion", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(ChoicesOn(await client.PageAsync(WebUiModule.DeviceAppearancePath)));
    }

    [Fact]
    public async Task The_pages_of_a_new_user_move_as_their_device_asks()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        Assert.Equal("System", MotionOf(application, Ada));

        var page = await client.PageAsync(OfAccount);

        Assert.Equal(Moving, RootOf(page));
        Assert.Equal("system", ChosenOn(page));
    }

    [Fact]
    public async Task The_pages_of_an_account_from_before_the_setting_move_as_its_device_asks()
    {
        // A database of the version before, in which an account says nothing of it.
        var database = PostgresServer.CreateDatabase();
        using (var before = database.Context())
        {
            before.GetService<IMigrator>().Migrate("20261124151658_AddTheme");
        }

        database.Execute(
            $"""
            INSERT INTO users (id, email, password_hash, registered_at, email_verification_sets_password, two_factor_failed_codes, theme)
            VALUES ('{Guid.CreateVersion7()}', '{Ada}', 'not-a-hash', now(), FALSE, 0, 'Dark')
            """);

        // Starting this version migrates the database.
        using var application = new AccountsApplication(database: database);
        _ = application.Registration;

        Assert.Equal("System", MotionOf(application, Ada));
    }

    [Fact]
    public async Task A_user_reduces_what_moves_and_every_page_says_so()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The page of the appearance offers the two under the theme, each with what it means.
        var before = await client.PageAsync(OfAccount);
        Assert.Contains("<legend>Animation</legend>", before);
        Assert.Equal(["system", "reduced"], ChoicesOn(before));
        Assert.Contains("<label for=\"motion-system\">System</label>", before);
        Assert.Contains("<label for=\"motion-reduced\">Reduced</label>", before);
        Assert.Contains("The pages move as they change, unless your device asks for reduced motion.", before);
        Assert.Contains("A change shows at once and nothing moves by itself, whatever your device asks for.", before);
        Assert.True(before.IndexOf("<legend>Theme</legend>", StringComparison.Ordinal) < before.IndexOf("<legend>Animation</legend>", StringComparison.Ordinal));

        var (path, after) = await client.SubmitAsync(OfAccount, Form, (Field, "reduced"));

        // The page that comes back is the page of the appearance, and says "reduced".
        Assert.Equal(OfAccount, path);
        Assert.Equal(Reduced, RootOf(after));
        Assert.Equal("reduced", ChosenOn(after));

        // It is saved to the account, by the name of the choice.
        Assert.Equal("Reduced", MotionOf(application, Ada));

        // Every page says it from then on: a page of the user, the start page, a legal
        // document, and the page of an address that has none.
        foreach (var address in new[] { "/", WebUiModule.DashboardPath, "/account", "/legal/terms" })
        {
            Assert.Equal(Reduced, RootOf(await client.PageAsync(address)));
        }

        var (_, missing) = await client.OpenAsync("/no-such-page");
        Assert.Contains("Page not found", missing);
        Assert.Equal(Reduced, RootOf(missing));

        // Also a page that asks for itself again without leaving the page that it is: the
        // browser then takes the mark from the answer.
        Assert.Equal(Reduced, RootOf(await client.AskAgainAsync(WebUiModule.DashboardPath)));

        // And back to what the device asks.
        (_, after) = await client.SubmitAsync(OfAccount, Form, (Field, "system"));
        Assert.Equal(Moving, RootOf(after));
        Assert.Equal("system", ChosenOn(after));
        Assert.Equal("System", MotionOf(application, Ada));
    }

    [Fact]
    public async Task What_a_page_says_of_it_is_ahead_of_its_stylesheets_and_no_script_puts_it_there()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.SubmitAsync(OfAccount, Form, (Field, "reduced"));

        var page = await client.PageAsync(WebUiModule.DashboardPath);

        // The first element says it before the first stylesheet is named, so nothing of the
        // page moves while a stylesheet or a script is on its way.
        var root = Root().Match(page);
        var stylesheet = page.IndexOf("<link rel=\"stylesheet\"", StringComparison.Ordinal);

        Assert.True(root.Success && stylesheet > 0 && root.Index < stylesheet);
        Assert.Contains("data-motion=\"reduced\"", root.Value);
        Assert.DoesNotContain("<script", page[..stylesheet]);

        // It is said once, and nowhere else in the page.
        Assert.Equal(1, Regex.Count(page, "data-motion="));
    }

    [Fact]
    public async Task The_choice_holds_on_every_device_that_its_user_signs_in_on_and_on_none_once_they_sign_out()
    {
        using var application = new AccountsApplication();
        using var desk = application.CreateClient();
        using var phone = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        await desk.SignInAsync(Ada);
        await desk.SubmitAsync(OfAccount, Form, (Field, "reduced"));

        // The first page that another browser is shown after signing in says it.
        var (path, start) = await phone.SignInAsync(Ada);

        Assert.Equal("/", path);
        Assert.Equal(Reduced, RootOf(start));

        // A change on one device is on the other with its next page.
        await phone.SubmitAsync(OfAccount, Form, (Field, "system"));

        Assert.Equal(Moving, RootOf(await desk.PageAsync(WebUiModule.DashboardPath)));

        // Signed out, the device is one of a visitor again: its pages move as it asks, and
        // the account keeps what it says.
        await phone.SubmitAsync(OfAccount, Form, (Field, "reduced"));
        var (_, signedOut) = await desk.SignOutAsync();

        Assert.Equal(Moving, RootOf(signedOut));
        Assert.Equal("Reduced", MotionOf(application, Ada));
    }

    [Fact]
    public async Task The_choice_of_a_user_is_saved_to_the_account_and_is_in_their_download()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClientThatStays();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        using var answer = await client.SendFormAsync(
            OfAccount, Form, Browser.AntiforgeryTokenIn(await client.PageAsync(OfAccount)), (Field, "reduced"));

        // The answer leads back to the page, by a request that changes nothing, and saves
        // nothing on the device.
        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal(OfAccount, new Uri(client.BaseAddress!, answer.Headers.Location!).AbsolutePath);
        Assert.DoesNotContain(Browser.CookiesSetBy(answer), cookie => cookie.Contains("motion", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("Reduced", MotionOf(application, Ada));

        // The page of the account says what is stored, under the theme.
        var account = await client.PageAsync("/account");
        Assert.Contains("Animation: Reduced. Your choice is stored with your account, and is in a download.", account);

        // And it is in the download of what is stored about the user.
        var export = JsonDocument.Parse(await client.PageAsync(WebUiModule.ExportPath)).RootElement;
        Assert.Equal("Reduced", export.GetProperty("account").GetProperty("motion").GetString());
    }

    [Fact]
    public async Task The_theme_and_what_moves_are_chosen_each_by_itself()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        await client.SubmitAsync(OfAccount, "theme", ("Theme", "dark"));
        var (_, both) = await client.SubmitAsync(OfAccount, Form, (Field, "reduced"));

        // The page says both, the theme first.
        Assert.Equal("<html lang=\"en\" data-theme=\"dark\" data-motion=\"reduced\">", RootOf(both));

        // A form saves its own choice, and leaves the other as it is.
        var (_, light) = await client.SubmitAsync(OfAccount, "theme", ("Theme", "light"));
        Assert.Equal("<html lang=\"en\" data-theme=\"light\" data-motion=\"reduced\">", RootOf(light));

        var (_, moving) = await client.SubmitAsync(OfAccount, Form, (Field, "system"));
        Assert.Equal("<html lang=\"en\" data-theme=\"light\">", RootOf(moving));

        Assert.Equal("Light", application.Database.Scalar<string>($"SELECT theme FROM users WHERE email = '{Ada}'"));
        Assert.Equal("System", MotionOf(application, Ada));
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
        await ada.SubmitAsync(OfAccount, Form, (Field, "reduced"));

        Assert.Equal("Reduced", MotionOf(application, Ada));
        Assert.Equal("System", MotionOf(application, Grace));
        Assert.Equal(Moving, RootOf(await grace.PageAsync(OfAccount)));
    }

    [Fact]
    public async Task A_form_that_names_no_choice_saves_nothing()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.SubmitAsync(OfAccount, Form, (Field, "reduced"));

        foreach (var fields in new (string, string)[][] { [(Field, "full")], [(Field, "Reduced")], [(Field, "1")], [("Theme", "dark")], [] })
        {
            // The page comes back as it was, and says what it takes.
            var (path, page) = await client.SubmitAsync(OfAccount, Form, fields);

            Assert.Equal(OfAccount, path);
            Assert.Contains("<p class=\"form-error\" role=\"alert\">Choose System or Reduced.</p>", page);
            Assert.Equal("reduced", ChosenOn(page));
            Assert.Equal(Reduced, RootOf(page));
            Assert.Equal("Reduced", MotionOf(application, Ada));
        }

        // The field of the other form is not read for this one, so nothing of the theme changed.
        Assert.Equal("System", application.Database.Scalar<string>($"SELECT theme FROM users WHERE email = '{Ada}'"));
    }

    [Fact]
    public async Task A_form_without_the_token_of_its_page_is_refused()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // A form that another site posts for a user has no token of a page of this browser.
        using var answer = await client.SendFormAsync(OfAccount, Form, "not-the-token", (Field, "reduced"));

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal("System", MotionOf(application, Ada));
    }

    /// <summary>What the row of an account says of how much moves.</summary>
    private static string MotionOf(AccountsApplication application, string email) =>
        application.Database.Scalar<string>($"SELECT motion FROM users WHERE email = '{email}'");

    /// <summary>The first element of a page, which says its theme and how much of it moves.</summary>
    private static string RootOf(string page) => Root().Match(page).Value;

    /// <summary>The choices that a page offers, in its order.</summary>
    private static string[] ChoicesOn(string page) => [.. Choice().Matches(page).Select(choice => choice.Groups[1].Value)];

    /// <summary>The choice that is ticked on a page.</summary>
    private static string ChosenOn(string page) =>
        Assert.Single(Choice().Matches(page), choice => choice.Value.Contains(" checked", StringComparison.Ordinal)).Groups[1].Value;

    [GeneratedRegex("<html[^>]*>")]
    private static partial Regex Root();

    [GeneratedRegex("<input [^>]*type=\"radio\" name=\"Motion\" value=\"([^\"]*)\"[^>]*>")]
    private static partial Regex Choice();
}
