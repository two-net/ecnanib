using System.Net;
using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Net.Codecrete.QrCodeGenerator;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// A user can enable two-factor authentication by enrolling an authenticator app (FR-ACC-3), and
/// can turn it off again. The file of the tests names the application "Ecnanib Test".
/// </summary>
public class TwoFactorEnrolmentTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_user_turns_two_factor_on_with_the_key_on_the_page_their_password_and_a_code_from_the_app()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The account page says how things stand, and leads to the page.
        var account = await client.PageAsync("/account");
        Assert.Contains("Off. Signing in takes your password alone.", account);
        Assert.Contains($"href=\"{Browser.TwoFactorPage}\"", account);

        // The page offers a key, as a QR code and as text, and asks for a code and the password.
        var page = await client.PageAsync(Browser.TwoFactorPage);
        Assert.Contains("<svg class=\"qr-code\"", page);
        Assert.Contains("name=\"Code\"", page);
        Assert.Contains("name=\"Password\"", page);

        var app = AuthenticatorApp.WithKeyOn(page);
        Assert.Equal(20, app.Key.Length);

        var (path, answer) = await client.PostAsync(
            Browser.TwoFactorPage,
            "turn-on",
            Browser.AntiforgeryTokenIn(page),
            ("SealedKey", Browser.SealedKeyIn(page)),
            ("Code", app.CodeAt(clock.GetUtcNow())),
            ("Password", AccountsApplication.Password));

        // It is on, and the answer shows the recovery codes.
        Assert.Equal(Browser.TwoFactorPage, path);
        Assert.Contains("Two-factor authentication is on.", answer);
        Assert.Equal(10, Browser.RecoveryCodesIn(answer).Count);
        Assert.DoesNotContain("name=\"SealedKey\"", answer);

        var stored = StoredTwoFactor.Of(application.Database, Ada);
        Assert.Equal(Morning, stored.EnabledAt);
        Assert.NotNull(stored.Secret);
        Assert.Equal(Totp.StepAt(Morning), stored.LastStep);
        Assert.Equal(0, stored.FailedCodes);
        Assert.Null(stored.LockedUntil);

        // The pages say so from now on, without the key and without the recovery codes.
        Assert.Contains(
            "On. Signing in takes a code from your authenticator app after your password. You have 10 of 10 recovery codes left.",
            await client.PageAsync("/account"));

        var on = await client.PageAsync(Browser.TwoFactorPage);
        Assert.Contains("Two-factor authentication is on, since 12 November 2026.", on);
        Assert.Contains("You have 10 of 10 recovery codes left.", on);
        Assert.DoesNotContain("qr-code", on);
        Assert.DoesNotContain("setup-key", on);
        Assert.Empty(Browser.RecoveryCodesIn(on));

        // The log names the account.
        var turnedOn = Assert.Single(
            application.Events(),
            written => written.Template == "User {UserId} turned two-factor authentication on, which ended {SessionCount} other sessions");
        Assert.Equal(user.Id.ToString(), turnedOn.Text("UserId"));
    }

    [Fact]
    public async Task The_QR_code_carries_the_key_of_the_page_under_the_name_of_the_application_and_the_address_of_the_account()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var page = await client.PageAsync(Browser.TwoFactorPage);
        var app = AuthenticatorApp.WithKeyOn(page);

        // What an app reads from a QR code: the key in Base32, with the two names it shows beside the code.
        var address = $"otpauth://totp/Ecnanib%20Test:ada%40example.com?secret={app.KeyText}&issuer=Ecnanib%20Test";

        // The same code, drawn here from that address, with the border of four modules a scanner needs.
        var expected = QrCode.EncodeText(address, QrCode.Ecc.Medium);
        Assert.Contains($"viewBox=\"0 0 {expected.Size + 8} {expected.Size + 8}\"", page);
        Assert.Contains($"d=\"{expected.ToGraphicsPath(4)}\"", page);

        // Black on white, whatever the colors of the page are.
        Assert.Contains("fill=\"#ffffff\"", page);
        Assert.Contains("fill=\"#000000\"", page);

        // The key for typing is in groups of four.
        Assert.Matches("<code class=\"setup-key\">[A-Z2-7]{4}( [A-Z2-7]{4}){7}</code>", page);
    }

    [Fact]
    public async Task The_page_shows_another_key_each_time_it_is_opened_and_nothing_is_stored_for_it()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var first = await client.PageAsync(Browser.TwoFactorPage);
        var second = await client.PageAsync(Browser.TwoFactorPage);

        Assert.NotEqual(AuthenticatorApp.WithKeyOn(first).KeyText, AuthenticatorApp.WithKeyOn(second).KeyText);
        Assert.NotEqual(Browser.SealedKeyIn(first), Browser.SealedKeyIn(second));

        // A key that nobody confirmed is nowhere: nobody can come back later to one they noted.
        Assert.True(StoredTwoFactor.Of(application.Database, Ada).IsEmpty);
        Assert.Empty(StoredRecoveryCode.All(application.Database));
        Assert.Empty(StoredAuditEvent.OfTwoFactor(application.Database));
    }

    [Fact]
    public async Task A_wrong_code_turns_nothing_on_and_the_page_shows_the_same_key_again()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var page = await client.PageAsync(Browser.TwoFactorPage);
        var app = AuthenticatorApp.WithKeyOn(page);
        var wrong = app.WrongCodeAt(Morning);

        var (_, answer) = await client.PostAsync(
            Browser.TwoFactorPage,
            "turn-on",
            Browser.AntiforgeryTokenIn(page),
            ("SealedKey", Browser.SealedKeyIn(page)),
            ("Code", wrong),
            ("Password", AccountsApplication.Password));

        // The form again, with the key the user has in their app by now. The code is not written back.
        Assert.Contains("That code is not right.", answer);
        Assert.Equal(app.KeyText, AuthenticatorApp.WithKeyOn(answer).KeyText);
        Assert.Equal(Browser.SealedKeyIn(page), Browser.SealedKeyIn(answer));
        Assert.DoesNotContain(wrong, answer);
        Assert.True(StoredTwoFactor.Of(application.Database, Ada).IsEmpty);

        // The code of an hour ago is as wrong, and so is what is not six digits.
        foreach (var code in new[] { app.CodeAt(Morning.AddHours(-1)), string.Empty, "12345", "1234567", "abcdef" })
        {
            var (_, refused) = await client.PostAsync(
                Browser.TwoFactorPage,
                "turn-on",
                Browser.AntiforgeryTokenIn(answer),
                ("SealedKey", Browser.SealedKeyIn(answer)),
                ("Code", code),
                ("Password", AccountsApplication.Password));

            Assert.Contains("That code is not right.", refused);
        }

        Assert.True(StoredTwoFactor.Of(application.Database, Ada).IsEmpty);

        // The right code of the same key turns it on.
        var (_, turnedOn) = await client.PostAsync(
            Browser.TwoFactorPage,
            "turn-on",
            Browser.AntiforgeryTokenIn(answer),
            ("SealedKey", Browser.SealedKeyIn(answer)),
            ("Code", app.CodeAt(Morning)),
            ("Password", AccountsApplication.Password));

        Assert.Contains("Two-factor authentication is on.", turnedOn);
        Assert.NotNull(StoredTwoFactor.Of(application.Database, Ada).EnabledAt);
    }

    [Fact]
    public async Task The_key_of_a_page_can_be_confirmed_for_half_an_hour_and_no_longer()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The page was opened, by whoever was at the browser, and left open.
        var page = await client.PageAsync(Browser.TwoFactorPage);
        var app = AuthenticatorApp.WithKeyOn(page);

        clock.Set(Morning.AddMinutes(30));
        var (_, late) = await client.PostAsync(
            Browser.TwoFactorPage,
            "turn-on",
            Browser.AntiforgeryTokenIn(page),
            ("SealedKey", Browser.SealedKeyIn(page)),
            ("Code", app.CodeAt(clock.GetUtcNow())),
            ("Password", AccountsApplication.Password));

        // Its key is of no use any more: the page offers another.
        Assert.Contains("Here is a new key", late);
        Assert.NotEqual(app.KeyText, AuthenticatorApp.WithKeyOn(late).KeyText);
        Assert.True(StoredTwoFactor.Of(application.Database, Ada).IsEmpty);

        // That one works until the last second of its half hour.
        var next = AuthenticatorApp.WithKeyOn(late);
        clock.Set(Morning.AddMinutes(60).AddSeconds(-1));
        var (_, inTime) = await client.PostAsync(
            Browser.TwoFactorPage,
            "turn-on",
            Browser.AntiforgeryTokenIn(late),
            ("SealedKey", Browser.SealedKeyIn(late)),
            ("Code", next.CodeAt(clock.GetUtcNow())),
            ("Password", AccountsApplication.Password));

        Assert.Contains("Two-factor authentication is on.", inTime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("WRONG-MARKER is not the password")]
    [InlineData("correct horse battery staple ")]
    public async Task A_wrong_password_turns_nothing_on(string password)
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var page = await client.PageAsync(Browser.TwoFactorPage);
        var app = AuthenticatorApp.WithKeyOn(page);

        // Someone at a browser that was left signed in has the code of an app of their own,
        // and not the password.
        var (_, answer) = await client.PostAsync(
            Browser.TwoFactorPage,
            "turn-on",
            Browser.AntiforgeryTokenIn(page),
            ("SealedKey", Browser.SealedKeyIn(page)),
            ("Code", app.CodeAt(Morning)),
            ("Password", password));

        Assert.Contains("That is not your password.", answer);
        Assert.DoesNotContain("WRONG-MARKER", answer);
        Assert.DoesNotContain("Two-factor authentication is on", answer);
        Assert.True(StoredTwoFactor.Of(application.Database, Ada).IsEmpty);
        Assert.Empty(StoredRecoveryCode.All(application.Database));
    }

    [Fact]
    public async Task A_key_that_was_made_for_another_account_is_refused()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var ada = application.CreateClient();
        using var grace = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await application.RegisterVerifiedAsync(Grace);
        await ada.SignInAsync(Ada);
        await grace.SignInAsync(Grace);

        // Grace's page, with a key that Grace knows.
        var hers = await grace.PageAsync(Browser.TwoFactorPage);
        var app = AuthenticatorApp.WithKeyOn(hers);

        // Posted by Ada's browser, as if Grace had planted it there.
        var page = await ada.PageAsync(Browser.TwoFactorPage);
        var (_, answer) = await ada.PostAsync(
            Browser.TwoFactorPage,
            "turn-on",
            Browser.AntiforgeryTokenIn(page),
            ("SealedKey", Browser.SealedKeyIn(hers)),
            ("Code", app.CodeAt(Morning)),
            ("Password", AccountsApplication.Password));

        // Refused, and the page offers a key of Ada's own.
        Assert.Contains("Here is a new key", answer);
        Assert.NotEqual(app.KeyText, AuthenticatorApp.WithKeyOn(answer).KeyText);
        Assert.True(StoredTwoFactor.Of(application.Database, Ada).IsEmpty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("k2")]
    [InlineData("k2.")]
    [InlineData(".AAAA")]
    [InlineData("k2.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("k2.not base64 at all")]
    [InlineData("no-such-key.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task What_is_not_a_key_that_the_application_made_is_refused(string sealedKey)
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The key that the last of these would be, if the application took a key as it comes.
        var chosen = new AuthenticatorApp(new byte[20]);

        var page = await client.PageAsync(Browser.TwoFactorPage);
        using var answer = await client.SendFormAsync(
            Browser.TwoFactorPage,
            "turn-on",
            Browser.AntiforgeryTokenIn(page),
            ("SealedKey", sealedKey),
            ("Code", chosen.CodeAt(Morning)),
            ("Password", AccountsApplication.Password));

        // The form again with a new key, and not an error.
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("Here is a new key", await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(StoredTwoFactor.Of(application.Database, Ada).IsEmpty);
    }

    [Fact]
    public async Task An_account_whose_address_is_not_verified_cannot_turn_two_factor_on()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();

        // Someone registered the address, which may not be theirs, and signed in.
        await application.RegisterAsync(Ada);
        await client.SignInAsync(Ada);

        // The page says what is missing, and offers no key.
        var page = await client.PageAsync(Browser.TwoFactorPage);
        Assert.Contains("once your email address is verified", page);
        Assert.DoesNotContain("qr-code", page);
        Assert.DoesNotContain("name=\"SealedKey\"", page);

        // Nor does the form turn it on when it is posted all the same, with a key that the
        // application made for this account. The test gets one by saying for a moment that the
        // address is verified.
        application.Database.Execute($"UPDATE users SET email_verified_at = registered_at WHERE email = '{Ada}'");
        var withKey = await client.PageAsync(Browser.TwoFactorPage);
        var sealedKey = Browser.SealedKeyIn(withKey);
        var app = AuthenticatorApp.WithKeyOn(withKey);
        application.Database.Execute($"UPDATE users SET email_verified_at = NULL WHERE email = '{Ada}'");

        var (_, answer) = await client.PostAsync(
            Browser.TwoFactorPage,
            "turn-on",
            Browser.AntiforgeryTokenIn(page),
            ("SealedKey", sealedKey),
            ("Code", app.CodeAt(Morning)),
            ("Password", AccountsApplication.Password));

        Assert.Contains("once your email address is verified", answer);
        Assert.True(StoredTwoFactor.Of(application.Database, Ada).IsEmpty);

        // So the owner of the address, who registers it again and chooses the password with
        // the link, is not kept out by a code of whoever registered it first.
        await application.RegisterAsync(Ada, "the password of the owner");
        Assert.True(await application.VerifyAsync(
            AccountsApplication.TokenIn(application.Email.Sent[^1]), "the password of the owner"));

        using var owner = application.CreateClient();
        var (path, signedIn) = await owner.SignInAsync(Ada, "the password of the owner");

        Assert.Equal("/", path);
        Assert.Contains("Sign out", signedIn);
    }

    [Fact]
    public async Task Turning_it_on_ends_every_other_session_of_the_account_and_keeps_this_one()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var laptop = application.CreateClient();
        using var phone = application.CreateClient();
        using var other = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.RegisterVerifiedAsync(Grace);
        await laptop.SignInAsync(Ada);
        await phone.SignInAsync(Ada);
        await other.SignInAsync(Grace);

        await laptop.TurnOnTwoFactorAsync(clock);

        // The phone was signed in with the password alone, and is asked to sign in again.
        Assert.StartsWith(SessionAuthentication.SignInPath, (await phone.OpenAsync("/account")).Address);

        // The browser that turned it on stays signed in, and another account is not touched.
        Assert.Contains("You are signed in as ada@example.com.", await laptop.PageAsync("/account"));
        Assert.Contains("You are signed in as grace@example.com.", await other.PageAsync("/account"));
        Assert.Equal(2, StoredSession.All(application.Database).Count);
        Assert.Single(StoredSession.All(application.Database), session => session.UserId == user.Id);

        var turnedOn = Assert.Single(
            application.Events(),
            written => written.Template == "User {UserId} turned two-factor authentication on, which ended {SessionCount} other sessions");
        Assert.Equal(1, turnedOn["SessionCount"].GetInt32());
    }

    [Fact]
    public async Task Sending_the_form_again_after_it_is_on_shows_no_recovery_codes_and_is_not_an_error()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var page = await client.PageAsync(Browser.TwoFactorPage);
        var app = AuthenticatorApp.WithKeyOn(page);
        var form = new[]
        {
            ("SealedKey", Browser.SealedKeyIn(page)),
            ("Code", app.CodeAt(Morning)),
            ("Password", AccountsApplication.Password),
        };

        var (_, first) = await client.PostAsync(Browser.TwoFactorPage, "turn-on", Browser.AntiforgeryTokenIn(page), form);
        var codes = StoredRecoveryCode.All(application.Database);
        Assert.Equal(10, Browser.RecoveryCodesIn(first).Count);

        // As a reload of the page that showed the recovery codes sends it, and with a wrong
        // password too: it is on already, so nothing is looked at.
        foreach (var password in new[] { AccountsApplication.Password, "not the password at all" })
        {
            using var again = await client.SendFormAsync(
                Browser.TwoFactorPage,
                "turn-on",
                Browser.AntiforgeryTokenIn(first),
                ("SealedKey", Browser.SealedKeyIn(page)),
                ("Code", app.CodeAt(Morning)),
                ("Password", password));
            var answer = await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            // The answer says that nothing was done, and where to get recovery codes: the
            // first answer, which showed them, may never have been seen.
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Contains("Two-factor authentication was on already", answer);
            Assert.Contains("href=\"/account/two-factor/recovery-codes\"", answer);
            Assert.Contains("Two-factor authentication is on, since 12 November 2026.", answer);
            Assert.Empty(Browser.RecoveryCodesIn(answer));
            Assert.DoesNotContain("setup-key", answer);
        }

        // Nothing was made a second time.
        Assert.Equal(codes, StoredRecoveryCode.All(application.Database), (one, other) => one.CodeHash.SequenceEqual(other.CodeHash));
        Assert.Single(StoredAuditEvent.OfTwoFactor(application.Database));
    }

    [Fact]
    public async Task Two_requests_that_turn_it_on_at_the_same_time_turn_it_on_once()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var laptop = application.CreateClient();
        using var phone = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await laptop.SignInAsync(Ada);
        await phone.SignInAsync(Ada);

        // Each browser has a page with a key of its own.
        var requests = new List<Task<(string Path, string Page)>>();
        foreach (var browser in new[] { laptop, phone })
        {
            var page = await browser.PageAsync(Browser.TwoFactorPage);
            var app = AuthenticatorApp.WithKeyOn(page);

            requests.Add(browser.PostAsync(
                Browser.TwoFactorPage,
                "turn-on",
                Browser.AntiforgeryTokenIn(page),
                ("SealedKey", Browser.SealedKeyIn(page)),
                ("Code", app.CodeAt(Morning)),
                ("Password", AccountsApplication.Password)));
        }

        var answers = await Task.WhenAll(requests);

        // One of them turned it on and was shown the recovery codes. The other found it on, or
        // found its session ended by the first.
        Assert.Single(answers, answer => Browser.RecoveryCodesIn(answer.Page).Count == 10);
        Assert.Equal(10, StoredRecoveryCode.All(application.Database).Count);
        Assert.Equal("TwoFactorTurnedOn", Assert.Single(StoredAuditEvent.OfTwoFactor(application.Database)).Action);
        Assert.Single(StoredSession.All(application.Database));
    }

    [Fact]
    public async Task A_user_turns_it_off_with_a_code_and_then_signs_in_with_the_password_alone()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var (app, _) = await client.TurnOnTwoFactorAsync(clock);

        // The page that says it is on leads to the form.
        Assert.Contains("href=\"/account/two-factor/turn-off\"", await client.PageAsync(Browser.TwoFactorPage));

        clock.Set(Morning.AddMinutes(5));
        var (path, page) = await client.SubmitAsync(
            "/account/two-factor/turn-off", "turn-off", ("Code", app.CodeAt(clock.GetUtcNow())));

        // Back at the page, which offers to turn it on again, with a new key.
        Assert.Equal(Browser.TwoFactorPage, path);
        Assert.Contains("Turn on two-factor authentication", page);
        Assert.NotEqual(app.KeyText, AuthenticatorApp.WithKeyOn(page).KeyText);
        Assert.Contains("Off. Signing in takes your password alone.", await client.PageAsync("/account"));

        // Nothing of it is left: not the key, and not the recovery codes.
        Assert.True(StoredTwoFactor.Of(application.Database, Ada).IsEmpty);
        Assert.Empty(StoredRecoveryCode.All(application.Database));

        // Signing in takes the password alone again.
        await client.SignOutAsync();
        var (after, signedIn) = await client.SignInAsync(Ada);

        Assert.Equal("/", after);
        Assert.Contains("Sign out", signedIn);

        var turnedOff = Assert.Single(
            application.Events(), written => written.Template == "User {UserId} turned two-factor authentication off");
        Assert.Equal(user.Id.ToString(), turnedOff.Text("UserId"));
    }

    [Fact]
    public async Task A_wrong_code_does_not_turn_it_off()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var (app, _) = await client.TurnOnTwoFactorAsync(clock);

        clock.Set(Morning.AddMinutes(5));
        var wrong = app.WrongCodeAt(clock.GetUtcNow());
        var (path, page) = await client.SubmitAsync("/account/two-factor/turn-off", "turn-off", ("Code", wrong));

        // The form again. The code is not written back.
        Assert.Equal("/account/two-factor/turn-off", path);
        Assert.Contains("That code is not right.", page);
        Assert.DoesNotContain(wrong, page);

        var stored = StoredTwoFactor.Of(application.Database, Ada);
        Assert.NotNull(stored.EnabledAt);
        Assert.Equal(1, stored.FailedCodes);
        Assert.Equal(10, StoredRecoveryCode.All(application.Database).Count);
    }

    [Theory]
    [InlineData("/account/two-factor/turn-off", "turn-off")]
    [InlineData("/account/two-factor/recovery-codes", "new-recovery-codes")]
    public async Task A_form_from_a_page_that_is_older_than_the_change_is_answered_and_is_not_an_error(string path, string form)
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var (app, _) = await client.TurnOnTwoFactorAsync(clock);

        // The page was left open in one tab while two-factor authentication was turned off in another.
        var old = await client.PageAsync(path);
        clock.Set(Morning.AddMinutes(5));
        await client.SubmitAsync("/account/two-factor/turn-off", "turn-off", ("Code", app.CodeAt(clock.GetUtcNow())));

        clock.Set(Morning.AddMinutes(10));
        using var answer = await client.SendFormAsync(
            path, form, Browser.AntiforgeryTokenIn(old), ("Code", app.CodeAt(clock.GetUtcNow())));

        // The page that says how things stand now.
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(Browser.TwoFactorPage, answer.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.True(StoredTwoFactor.Of(application.Database, Ada).IsEmpty);

        // Opening either page then leads there too.
        Assert.Equal(Browser.TwoFactorPage, (await client.OpenAsync(path)).Address);
    }

    [Theory]
    [InlineData("/account/two-factor")]
    [InlineData("/account/two-factor/turn-off")]
    [InlineData("/account/two-factor/recovery-codes")]
    public async Task A_visitor_who_opens_its_pages_is_sent_to_sign_in(string path)
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        var (asked, page) = await client.OpenAsync(path);

        Assert.Equal($"{SessionAuthentication.SignInPath}?returnUrl={Uri.EscapeDataString(path)}", asked);
        Assert.Contains("<h1>Sign in</h1>", page);
    }

    [Fact]
    public async Task A_post_without_the_token_against_request_forgery_changes_nothing()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // Turning it on, from a page of another site.
        var page = await client.PageAsync(Browser.TwoFactorPage);
        var app = AuthenticatorApp.WithKeyOn(page);
        await client.PostAsync(
            Browser.TwoFactorPage,
            "turn-on",
            "not-the-token",
            ("SealedKey", Browser.SealedKeyIn(page)),
            ("Code", app.CodeAt(Morning)),
            ("Password", AccountsApplication.Password));

        Assert.True(StoredTwoFactor.Of(application.Database, Ada).IsEmpty);

        // And turning it off.
        (app, _) = await client.TurnOnTwoFactorAsync(clock);
        clock.Set(Morning.AddMinutes(5));
        await client.PostAsync("/account/two-factor/turn-off", "turn-off", "not-the-token", ("Code", app.CodeAt(clock.GetUtcNow())));

        Assert.NotNull(StoredTwoFactor.Of(application.Database, Ada).EnabledAt);
    }

    [Fact]
    public async Task The_pages_that_show_the_key_and_the_recovery_codes_are_not_kept_by_the_browser()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        using var setup = await client.GetAsync(Browser.TwoFactorPage, TestContext.Current.CancellationToken);
        var page = await setup.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var app = AuthenticatorApp.WithKeyOn(page);

        using var turnedOn = await client.SendFormAsync(
            Browser.TwoFactorPage,
            "turn-on",
            Browser.AntiforgeryTokenIn(page),
            ("SealedKey", Browser.SealedKeyIn(page)),
            ("Code", app.CodeAt(Morning)),
            ("Password", AccountsApplication.Password));

        Assert.Equal(10, Browser.RecoveryCodesIn(await turnedOn.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Count);

        // Neither is stored by the browser or by anything between it and the application, so
        // the back button of a shared computer shows neither.
        Assert.True(setup.Headers.CacheControl!.NoStore);
        Assert.True(turnedOn.Headers.CacheControl!.NoStore);
    }
}
