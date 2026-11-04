using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Kernel;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Notifications;

/// <summary>
/// FR-PREF-06 on the preferences page: which notifications a user is emailed, offered only with email
/// on. In-app is always on, so there's nothing to choose for it. NotificationEmailTests covers what's sent.
/// </summary>
public sealed partial class EmailChoiceTests(SharedEmailApp app, PostgresFixture postgres) : IClassFixture<SharedEmailApp>
{
    private const string Page = "/account/preferences";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private IPreferences Preferences => app.Services.GetRequiredService<IPreferences>();

    public static TheoryData<string> Refused =>
    [
        "Trade",
        "1",
        "tradeopened",
        "TradeOpened, StopMoved",
        "CatalogSyncFailed",
    ];

    [Fact]
    public async Task With_email_on_the_page_offers_the_choices_with_the_defaults_ticked()
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();

        var page = await visitor.PageAsync(Page);
        var boxes = Boxes(page);

        Assert.Contains("name=\"Input.EmailShown\"", page, StringComparison.Ordinal);
        Assert.Equal(EmailNotificationChoices.AvailableTo(administrator: false).Select(type => type.ToString()).Order(), boxes.Keys.Order());
        Assert.Equal(
            EmailNotificationChoices.Defaults.Except(EmailNotificationChoices.AdministratorOnly).Select(type => type.ToString()).Order(),
            boxes.Where(box => box.Value).Select(box => box.Key).Order());

        // The address isn't verified, so nothing would arrive yet.
        Assert.Contains("Email only goes to a verified address", WebUtility.HtmlDecode(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_verified_user_isnt_told_to_verify()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        await TestUsers.VerifyAsync(app.ConnectionString, await AccountRows.IdAsync(app.ConnectionString, email, Cancellation), Cancellation);

        var page = WebUtility.HtmlDecode(await visitor.PageAsync(Page));

        Assert.DoesNotContain("Email only goes to a verified address", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_email_off_there_is_nothing_to_choose()
    {
        await using var off = AccountApp.Create(postgres);
        using var visitor = Visitor.To(off);
        await visitor.SignUpAsync();

        var page = await visitor.PageAsync(Page);

        Assert.DoesNotContain("Input.EmailNotifications", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Input.EmailShown", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ticked_boxes_are_saved_and_none_is_a_choice()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        using (var response = await SaveAsync(visitor, "TradeOpened", "StopMoved"))
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(Page + "?saved=true", Visitor.Target(response));
        }

        Assert.Equal(
            [NotificationType.TradeOpened, NotificationType.StopMoved],
            (await Preferences.GetEmailNotificationsAsync(userId, Cancellation)).Order());
        Assert.Equal(["StopMoved", "TradeOpened"], Boxes(await visitor.PageAsync(Page)).Where(box => box.Value).Select(box => box.Key).Order());

        using (var response = await SaveAsync(visitor))
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }

        Assert.Empty(await Preferences.GetEmailNotificationsAsync(userId, Cancellation));
        Assert.Equal(Array.Empty<string>(), await NotificationRows.EmailChoicesAsync(app.ConnectionString, userId, Cancellation));
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_choice_that_isnt_offered_is_refused(string value)
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        using var response = await SaveAsync(visitor, "StopMoved", value);
        var page = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Choose from the notifications listed.", page, StringComparison.Ordinal);
        Assert.Null(await NotificationRows.EmailChoicesAsync(app.ConnectionString, userId, Cancellation));

        // What was posted stays ticked, so the user doesn't lose it.
        Assert.True(Boxes(page)["StopMoved"]);
    }

    /// <summary>Only a post from the form as it is now carries the marker, and only that changes the choices.</summary>
    [Fact]
    public async Task A_post_without_the_marker_keeps_what_is_stored()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        (await SaveAsync(visitor, "TradeOpened")).Dispose();
        (await visitor.SubmitRepeatedAsync(Page, Visitor.PreferencesForm, [.. Look("Light"), ("Input.EmailShown", string.Empty)])).Dispose();

        Assert.Equal([NotificationType.TradeOpened], await Preferences.GetEmailNotificationsAsync(userId, Cancellation));
        Assert.Equal(Theme.Light, (await Preferences.GetAsync(userId, Cancellation)).Theme);
    }

    [Fact]
    public async Task With_email_off_saving_keeps_the_stored_choices()
    {
        await using var off = AccountApp.Create(postgres);
        using var visitor = Visitor.To(off);
        var email = await visitor.SignUpAsync();
        var connectionString = off.ConnectionString();
        var userId = await AccountRows.IdAsync(connectionString, email, Cancellation);
        var preferences = off.Services.GetRequiredService<IPreferences>();

        await NotificationRows.SetEmailChoicesAsync(connectionString, userId, ["TradeOpened"], Cancellation);

        (await visitor.SubmitAsync(Page, Visitor.PreferencesForm, Look("Dark"))).Dispose();
        await preferences.SaveAsync(
            userId,
            new UserPreferences(Theme.Dark, AnimationLevel.System, null),
            new HashSet<NotificationType> { NotificationType.StopMoved },
            Cancellation);

        var stored = await NotificationRows.EmailChoicesAsync(connectionString, userId, Cancellation);

        Assert.Equal(new[] { "TradeOpened" }, stored);
        Assert.Equal(Theme.Dark, (await preferences.GetAsync(userId, Cancellation)).Theme);
    }

    [Fact]
    public async Task Only_an_administrator_can_choose_the_administration_ones()
    {
        var administrator = Visitor.NewEmail();
        await using var configured = AccountApp.CreateWithEmail(
            postgres,
            new Email.CapturedEmail(),
            new Dictionary<string, string?> { ["Admin:AdminEmails:0"] = administrator });
        var connectionString = configured.ConnectionString();
        var preferences = configured.Services.GetRequiredService<IPreferences>();
        var look = new UserPreferences(Theme.System, AnimationLevel.System, null);

        using var visitor = Visitor.To(configured);
        await visitor.SignUpAsync(administrator);
        var administratorId = await AccountRows.IdAsync(connectionString, administrator, Cancellation);
        await TestUsers.VerifyAsync(connectionString, administratorId, Cancellation);

        var boxes = Boxes(await visitor.PageAsync(Page));

        Assert.True(boxes[nameof(NotificationType.CatalogSyncFailed)]);
        Assert.True(boxes[nameof(NotificationType.ExchangeRateLimitBan)]);

        using (var response = await SaveAsync(visitor, "ExchangeRateLimitBan"))
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }

        Assert.Equal([NotificationType.ExchangeRateLimitBan], await preferences.GetEmailNotificationsAsync(administratorId, Cancellation));

        var userId = await TestUsers.AddAsync(connectionString, Visitor.NewEmail(), verified: true, Cancellation);
        var refused = await preferences.SaveAsync(userId, look, new HashSet<NotificationType> { NotificationType.CatalogSyncFailed }, Cancellation);

        Assert.Equal(PreferencesResult.UnavailableEmailNotification, refused);
        Assert.Null(await NotificationRows.EmailChoicesAsync(connectionString, userId, Cancellation));
    }

    private static (string Name, string Value)[] Look(string theme) =>
        [("Input.Theme", theme), ("Input.Animation", "System"), ("Input.TimeZone", string.Empty)];

    private static Task<HttpResponseMessage> SaveAsync(Visitor visitor, params string[] ticked) =>
        visitor.SubmitRepeatedAsync(Page, Visitor.PreferencesForm, [.. Look("System"), .. ticked.Select(type => ("Input.EmailNotifications", type))]);

    /// <summary>Each email box on a page, by its value, and whether it's ticked.</summary>
    private static Dictionary<string, bool> Boxes(string page) =>
        Checkboxes().Matches(page).ToDictionary(
            match => match.Groups["value"].Value,
            match => Regex.IsMatch(match.Value, @"\schecked(\s|=|/|>)"));

    [GeneratedRegex("""<input\b[^>]*name="Input\.EmailNotifications"[^>]*value="(?<value>[^"]*)"[^>]*>""")]
    private static partial Regex Checkboxes();
}
