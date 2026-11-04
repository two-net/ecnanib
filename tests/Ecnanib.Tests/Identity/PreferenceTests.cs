using System.Net;
using Ecnanib.Components;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Identity;

/// <summary>
/// SRS 3.2 and AC-05, as far as HTML can show it: the preference is in the first response, so nothing
/// flashes. Following the operating system live, and the motion it allows, need a browser: see
/// docs/architecture.md.
/// </summary>
public sealed class PreferenceTests(SharedAccountApp app, PostgresFixture postgres) : IClassFixture<SharedAccountApp>
{
    private const string Page = "/account/preferences";

    /// <summary>No daylight saving, so the offset is the same whenever the tests run.</summary>
    private const string Bangkok = "Asia/Bangkok";

    /// <summary>No daylight saving since 2019, and on the other side of UTC.</summary>
    private const string SaoPaulo = "America/Sao_Paulo";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private IPreferences Preferences => app.Services.GetRequiredService<IPreferences>();

    public static TheoryData<string, string> Refused => new()
    {
        { "Input.Theme", "Blue" },
        { "Input.Theme", "1" },
        { "Input.Theme", "light" },
        { "Input.Animation", "Light" },
        { "Input.Animation", "Reduced, Off" },
        { "Input.TimeZone", "Mars/Olympus" },
        { "Input.TimeZone", "../../etc/passwd" },

        // A browser can report this alias, and it's shown, but only the canonical name is offered.
        { "Input.TimeZone", "Asia/Calcutta" },
    };

    [Fact]
    public async Task A_visitor_gets_the_system_theme()
    {
        using var visitor = Visitor.To(app);

        var page = await visitor.PageAsync("/");

        AssertTheme(page, "system", "light dark");
        Assert.Contains("data-motion=\"system\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_user_who_hasnt_chosen_gets_the_defaults()
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();

        var page = await visitor.PageAsync(Page);

        AssertTheme(page, "system", "light dark");
        Assert.Contains("data-motion=\"system\"", page, StringComparison.Ordinal);
        AssertSelected(page, "System", "System", string.Empty);
    }

    /// <summary>FR-PREF-03 keeps visitors on System whatever the operator chooses for users.</summary>
    [Fact]
    public async Task The_configured_defaults_apply_to_users_and_only_the_animation_to_visitors()
    {
        await using var configured = AccountApp.Create(
            postgres,
            new Dictionary<string, string?> { ["Ui:DefaultTheme"] = "Dark", ["Ui:DefaultAnimation"] = "Reduced" });
        using var visitor = Visitor.To(configured);

        var landing = await visitor.PageAsync("/");

        AssertTheme(landing, "system", "light dark");
        Assert.Contains("data-motion=\"reduced\"", landing, StringComparison.Ordinal);

        await visitor.SignUpAsync();
        var page = await visitor.PageAsync(Page);

        AssertTheme(page, "dark", "dark");
        Assert.Contains("data-motion=\"reduced\"", page, StringComparison.Ordinal);
        AssertSelected(page, "Dark", "Reduced", string.Empty);
    }

    [Fact]
    public async Task Saved_preferences_apply_from_the_next_page_on_every_device()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();

        using var response = await SaveAsync(visitor, "Light", "Off", Bangkok);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(Page + "?saved=true", Visitor.Target(response));

        var page = await visitor.PageAsync(Page + "?saved=true");

        Assert.Contains("Your preferences are saved.", page, StringComparison.Ordinal);
        AssertTheme(page, "light", "light");
        Assert.Contains("data-motion=\"off\"", page, StringComparison.Ordinal);
        AssertSelected(page, "Light", "Off", Bangkok);

        // Stored per user, not per browser (FR-PREF-03).
        using var other = Visitor.To(app);
        (await other.SignInAsync(email)).Dispose();

        AssertTheme(await other.PageAsync("/account"), "light", "light");
    }

    [Fact]
    public async Task Times_follow_the_browser_until_the_user_chooses_a_zone()
    {
        using var visitor = Visitor.To(app);
        visitor.SendBrowserTimeZone(Bangkok);
        await visitor.SignUpAsync();

        Assert.Contains("UTC+07:00", await TextAsync(visitor, "/account"), StringComparison.Ordinal);
        Assert.Contains($"Your browser's time zone ({Bangkok})", await TextAsync(visitor, Page), StringComparison.Ordinal);

        (await SaveAsync(visitor, "System", "System", SaoPaulo)).Dispose();
        var account = await TextAsync(visitor, "/account");

        Assert.Contains("UTC-03:00", account, StringComparison.Ordinal);
        Assert.DoesNotContain("UTC+07:00", account, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Times_are_in_UTC_without_a_browser_zone()
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();

        Assert.Contains("UTC+00:00", await TextAsync(visitor, "/account"), StringComparison.Ordinal);
    }

    /// <summary>Anyone can write the cookie, and on Unix a time zone lookup reads a file named after it.</summary>
    [Theory]
    [InlineData("../../../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("Asia/../UTC")]
    [InlineData("Mars/Olympus")]
    [InlineData("Asia/Bangkok/../../../../etc/passwd")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task A_cookie_that_isnt_a_time_zone_leaves_times_in_UTC(string cookie)
    {
        using var visitor = Visitor.To(app);
        visitor.SendBrowserTimeZone(cookie);
        await visitor.SignUpAsync();

        Assert.Contains("UTC+00:00", await TextAsync(visitor, "/account"), StringComparison.Ordinal);
        Assert.Contains("Your browser's time zone<", await TextAsync(visitor, Page), StringComparison.Ordinal);
    }

    /// <summary>A browser reports the alias it knows, which isn't offered, but it's still a zone.</summary>
    [Fact]
    public async Task A_browser_alias_still_sets_the_zone()
    {
        using var visitor = Visitor.To(app);
        visitor.SendBrowserTimeZone("Asia/Calcutta");
        await visitor.SignUpAsync();

        Assert.Contains("UTC+05:30", await TextAsync(visitor, "/account"), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_value_nobody_offered_saves_nothing(string field, string value)
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var fields = new Dictionary<string, string> { ["Input.Theme"] = "Dark", ["Input.Animation"] = "Off", ["Input.TimeZone"] = Bangkok };
        fields[field] = value;

        using var response = await visitor.SubmitAsync(Page, Visitor.PreferencesForm, [.. fields.Select(entry => (entry.Key, entry.Value))]);
        var page = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Choose one of the options.", page, StringComparison.Ordinal);
        Assert.Equal(0, await RowsAsync(email));
    }

    [Fact]
    public async Task The_contract_refuses_a_zone_it_doesnt_offer()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        var result = await Preferences.SaveAsync(userId, new UserPreferences(Theme.Dark, AnimationLevel.Off, "Asia/Calcutta"), Cancellation);

        Assert.Equal(PreferencesResult.UnknownTimeZone, result);
        Assert.Equal(0, await RowsAsync(email));
    }

    [Fact]
    public async Task Saving_twice_keeps_one_row_with_the_latest_choice()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        await Preferences.SaveAsync(userId, new UserPreferences(Theme.Dark, AnimationLevel.Off, Bangkok), Cancellation);
        await Preferences.SaveAsync(userId, new UserPreferences(Theme.Light, AnimationLevel.Full, null), Cancellation);

        Assert.Equal(1, await RowsAsync(email));
        Assert.Equal(new UserPreferences(Theme.Light, AnimationLevel.Full, null), await Preferences.GetAsync(userId, Cancellation));
    }

    /// <summary>An account deleted while its page was open inserts nothing, rather than failing the foreign key.</summary>
    [Fact]
    public async Task Saving_for_an_account_that_doesnt_exist_does_nothing()
    {
        var userId = Guid.NewGuid();

        Assert.Equal(
            PreferencesResult.Saved,
            await Preferences.SaveAsync(userId, new UserPreferences(Theme.Dark, AnimationLevel.Off, null), Cancellation));
        Assert.Equal(
            0,
            await Sql.ScalarAsync<long>(app.ConnectionString, $"SELECT count(*) FROM user_preferences WHERE user_id = '{userId}'", Cancellation));
    }

    [Fact]
    public async Task Preferences_go_with_the_account()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
        (await SaveAsync(visitor, "Dark", "Off", Bangkok)).Dispose();

        await Sql.ExecuteAsync(app.ConnectionString, $"DELETE FROM users WHERE id = '{userId}'", Cancellation);

        Assert.Equal(
            0,
            await Sql.ScalarAsync<long>(app.ConnectionString, $"SELECT count(*) FROM user_preferences WHERE user_id = '{userId}'", Cancellation));
    }

    /// <summary>
    /// The error page never touches the database, which may be why it's showing, so it renders as a
    /// visitor's. TerminalMiddleware throws only for one path, so the rest of the app still works.
    /// </summary>
    [Fact]
    public async Task The_error_page_uses_the_system_theme_without_the_database()
    {
        await using var failing = AccountApp.Create(
            postgres,
            services: services => services.AddTransient<IStartupFilter>(_ => new TerminalMiddleware(Fail)),
            environment: Environments.Production);
        using var visitor = Visitor.ToPublic(failing);
        await visitor.SignUpAsync();
        (await SaveAsync(visitor, "Dark", "Off", null)).Dispose();

        AssertTheme(await visitor.PageAsync("/account"), "dark", "dark");

        using var response = await visitor.GetAsync("/fail");
        var page = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertTheme(page, "system", "light dark");

        static Task Fail(HttpContext context)
        {
            if (context.Request.Path == "/fail")
            {
                throw new InvalidOperationException("Failing on purpose.");
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;

            return Task.CompletedTask;
        }
    }

    private static Task<HttpResponseMessage> SaveAsync(Visitor visitor, string theme, string animation, string? timeZone) =>
        visitor.SubmitAsync(
            Page,
            Visitor.PreferencesForm,
            ("Input.Theme", theme),
            ("Input.Animation", animation),
            ("Input.TimeZone", timeZone ?? string.Empty));

    private static void AssertTheme(string page, string theme, string colorScheme)
    {
        Assert.Contains($"data-theme=\"{theme}\"", page, StringComparison.Ordinal);
        Assert.Contains($"<meta name=\"color-scheme\" content=\"{colorScheme}\"", page, StringComparison.Ordinal);
    }

    private static void AssertSelected(string page, string theme, string animation, string timeZone)
    {
        AssertSelected(page, "theme", theme);
        AssertSelected(page, "animation", animation);
        AssertSelected(page, "time-zone", timeZone);
    }

    private static void AssertSelected(string page, string select, string value)
    {
        var start = page.IndexOf($"id=\"{select}\"", StringComparison.Ordinal);
        var options = page[start..page.IndexOf("</select>", start, StringComparison.Ordinal)];

        // Blazor writes an empty attribute without its value, which a browser reads as empty.
        var option = value.Length == 0 ? "<option value selected" : $"<option value=\"{value}\" selected";

        Assert.True(options.Contains(option, StringComparison.Ordinal), options[..Math.Min(options.Length, 600)]);
    }

    /// <summary>Razor encodes the plus in an offset, and apostrophes.</summary>
    private static async Task<string> TextAsync(Visitor visitor, string path) => WebUtility.HtmlDecode(await visitor.PageAsync(path));

    private async Task<long> RowsAsync(string email)
    {
        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        return await Sql.ScalarAsync<long>(app.ConnectionString, $"SELECT count(*) FROM user_preferences WHERE user_id = '{userId}'", Cancellation);
    }
}
