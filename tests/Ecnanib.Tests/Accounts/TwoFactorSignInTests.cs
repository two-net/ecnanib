using System.Net;
using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Npgsql;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// Once two-factor authentication is enabled, sign-in requires a valid code from the app
/// (FR-ACC-3): the password alone starts no session, a code is accepted once, and a code cannot be
/// found by trying.
/// </summary>
public class TwoFactorSignInTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string WrongCode = "That code is not right.";
    private const string Refused = "Too many wrong codes were entered.";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task With_two_factor_on_the_password_alone_starts_no_session_and_sets_no_session_cookie()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (user, _, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClientThatStays();

        var form = await client.PageAsync(SessionAuthentication.SignInPath);
        using var answer = await client.SendFormAsync(
            SessionAuthentication.SignInPath,
            "sign-in",
            Browser.AntiforgeryTokenIn(form),
            ("Email", Ada),
            ("Password", AccountsApplication.Password));

        // The browser is sent to the page that takes the code, with a cookie that says who gave
        // the password, and with no session.
        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal(SessionAuthentication.SignInCodePath, new Uri(client.BaseAddress!, answer.Headers.Location!).AbsolutePath);
        Assert.StartsWith("__Host-ecnanib-sign-in=", Assert.Single(Browser.CookiesSetBy(answer)));
        Assert.Empty(StoredSession.All(application.Database));

        // The sign-in waits for its code for ten minutes.
        var waiting = Assert.Single(StoredPendingSignIn.All(application.Database));
        Assert.Equal(user.Id, waiting.UserId);
        Assert.Equal(clock.GetUtcNow().AddMinutes(10), waiting.ExpiresAt);

        // It is not a session: it opens no page of signed-in users.
        using var account = await client.GetAsync("/account", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, account.StatusCode);
        Assert.StartsWith(SessionAuthentication.SignInPath, account.Headers.Location!.OriginalString);
        Assert.DoesNotContain("Sign out", await client.PageAsync("/"));

        var asked = Assert.Single(
            application.Events(), written => written.Template == "User {UserId} gave their password and is asked for a code");
        Assert.Equal(user.Id.ToString(), asked.Text("UserId"));
    }

    [Fact]
    public async Task A_code_from_the_app_after_the_password_signs_in_and_leads_where_the_user_was_going()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (user, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();

        var (asked, form) = await client.OpenAsync("/account?tab=security");
        using var afterPassword = await client.SendFormAsync(
            asked, "sign-in", Browser.AntiforgeryTokenIn(form), ("Email", Ada), ("Password", AccountsApplication.Password));

        // The page that takes the code, which knows where the user was going.
        var codeAddress = afterPassword.RequestMessage!.RequestUri!.PathAndQuery;
        var codePage = await afterPassword.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal($"{SessionAuthentication.SignInCodePath}?returnUrl=%2Faccount%3Ftab%3Dsecurity", codeAddress);
        Assert.Contains("<h1>Enter your code</h1>", codePage);
        Assert.DoesNotContain("Sign out", codePage);

        using var afterCode = await client.SendFormAsync(
            codeAddress, "sign-in-code", Browser.AntiforgeryTokenIn(codePage), ("Code", app.CodeAt(clock.GetUtcNow())));

        Assert.Equal("/account?tab=security", afterCode.RequestMessage!.RequestUri!.PathAndQuery);
        Assert.Contains(
            "You are signed in as ada@example.com.",
            await afterCode.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // The session is there, and the sign-in that waited is not.
        Assert.Equal(user.Id, Assert.Single(StoredSession.All(application.Database)).UserId);
        Assert.Empty(StoredPendingSignIn.All(application.Database));
        Assert.Equal(Totp.StepAt(clock.GetUtcNow()), StoredTwoFactor.Of(application.Database, Ada).LastStep);

        // Signed in twice by now: to turn it on, and with the code.
        Assert.Equal(2, application.Events().Count(written => written.Template == "User {UserId} signed in"));

        // Someone who is signed in has no use for the page.
        Assert.Equal("/", (await client.OpenAsync(SessionAuthentication.SignInCodePath)).Address);
    }

    [Fact]
    public async Task A_code_as_the_app_shows_it_with_a_space_in_the_middle_signs_in()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        var code = app.CodeAt(clock.GetUtcNow());
        var (path, page) = await client.EnterCodeAsync($" {code[..3]} {code[3..]} ");

        Assert.Equal("/", path);
        Assert.Contains("Sign out", page);
    }

    [Fact]
    public async Task A_wrong_code_signs_nobody_in_and_another_can_be_tried()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (user, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        var wrong = app.WrongCodeAt(clock.GetUtcNow());
        var (path, page) = await client.EnterCodeAsync(wrong);

        // The form again. The code is not written back.
        Assert.Equal(SessionAuthentication.SignInCodePath, path);
        Assert.Contains(WrongCode, page);
        Assert.DoesNotContain(wrong, page);
        Assert.Empty(StoredSession.All(application.Database));
        Assert.Equal(1, StoredTwoFactor.Of(application.Database, Ada).FailedCodes);

        var failed = Assert.Single(application.Events(), written => written.Template == "A code of user {UserId} was not right");
        Assert.Equal(user.Id.ToString(), failed.Text("UserId"));

        // The right one signs in, on the same sign-in.
        var (after, signedIn) = await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));

        Assert.Equal("/", after);
        Assert.Contains("Sign out", signedIn);
        Assert.Equal(0, StoredTwoFactor.Of(application.Database, Ada).FailedCodes);
    }

    [Fact]
    public async Task A_code_is_accepted_once()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var owner = application.CreateClient();
        using var watcher = application.CreateClient();

        var code = app.CodeAt(clock.GetUtcNow());
        await owner.SignInAsync(Ada);
        Assert.Equal("/", (await owner.EnterCodeAsync(code)).Path);

        // Someone who knows the password and saw the code being typed, within its half minute.
        await watcher.SignInAsync(Ada);
        var (path, page) = await watcher.EnterCodeAsync(code);

        Assert.Equal(SessionAuthentication.SignInCodePath, path);
        Assert.Contains(WrongCode, page);
        Assert.Single(StoredSession.All(application.Database));

        // It was the right code, so it is not a guess, and is not counted as one.
        Assert.Equal(0, StoredTwoFactor.Of(application.Database, Ada).FailedCodes);

        // The next code of the app works.
        clock.Set(clock.GetUtcNow().AddSeconds(30));
        Assert.Equal("/", (await watcher.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()))).Path);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-30, true)] // The step before: a code that was typed slowly.
    [InlineData(30, true)] // The step after: a phone whose clock is a little ahead.
    [InlineData(-60, false)]
    [InlineData(60, false)]
    [InlineData(-3600, false)]
    public async Task The_code_of_the_present_step_and_of_the_ones_beside_it_is_accepted_and_no_other(int seconds, bool accepted)
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();

        // Well after the code that turned it on, so that every code here is a later one.
        clock.Set(Morning.AddHours(2));
        await client.SignInAsync(Ada);

        var (path, _) = await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow().AddSeconds(seconds)));

        Assert.Equal(accepted ? "/" : SessionAuthentication.SignInCodePath, path);
        Assert.Equal(accepted ? 1 : 0, StoredSession.All(application.Database).Count);
    }

    [Fact]
    public async Task A_code_of_a_step_before_the_last_one_used_is_refused()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var first = application.CreateClient();
        using var second = application.CreateClient();
        var now = clock.GetUtcNow();

        // The code of the step after the present one is accepted.
        await first.SignInAsync(Ada);
        Assert.Equal("/", (await first.EnterCodeAsync(app.CodeAt(now.AddSeconds(30)))).Path);

        // The code of the present step is an earlier one now, though its half minute is not over.
        await second.SignInAsync(Ada);
        var (path, page) = await second.EnterCodeAsync(app.CodeAt(now));

        Assert.Equal(SessionAuthentication.SignInCodePath, path);
        Assert.Contains(WrongCode, page);
        Assert.Single(StoredSession.All(application.Database));
    }

    [Fact]
    public async Task Five_wrong_codes_refuse_every_code_for_fifteen_minutes_and_no_longer()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (user, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        var start = clock.GetUtcNow();
        await client.SignInAsync(Ada);

        // Four are answered as wrong codes are.
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var (_, wrong) = await client.EnterCodeAsync(app.WrongCodeAt(start));

            Assert.Contains(WrongCode, wrong);
            Assert.DoesNotContain(Refused, wrong);
        }

        // The fifth is one too many.
        var (_, fifth) = await client.EnterCodeAsync(app.WrongCodeAt(start));
        Assert.Contains(Refused, fifth);
        Assert.Contains("the next 15 minutes", fifth);

        var stored = StoredTwoFactor.Of(application.Database, Ada);
        Assert.Equal(start.AddMinutes(15), stored.LockedUntil);
        Assert.Equal(0, stored.FailedCodes);

        // Now the right code is refused as well, so that trying gains nothing.
        var (path, refused) = await client.EnterCodeAsync(app.CodeAt(start));
        Assert.Equal(SessionAuthentication.SignInCodePath, path);
        Assert.Contains(Refused, refused);
        Assert.Empty(StoredSession.All(application.Database));

        // The log says so once, as a warning, and names the account.
        var locked = Assert.Single(
            application.Events(),
            written => written.Template == "The codes of user {UserId} are refused for {Minutes} minutes, after {WrongCodes} wrong ones in a row");
        Assert.Equal("Warning", locked.Level);
        Assert.Equal(user.Id.ToString(), locked.Text("UserId"));
        Assert.Equal(15, locked["Minutes"].GetInt32());
        Assert.Equal(5, locked["WrongCodes"].GetInt32());

        // Until the last second of the fifteen minutes. The sign-in that waited has ended by
        // then, so the password is given again.
        clock.Set(start.AddMinutes(15).AddSeconds(-1));
        await client.SignInAsync(Ada);
        Assert.Contains(Refused, (await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()))).Page);
        Assert.Empty(StoredSession.All(application.Database));

        // And no longer.
        clock.Set(start.AddMinutes(15));
        var (after, signedIn) = await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));

        Assert.Equal("/", after);
        Assert.Contains("Sign out", signedIn);
    }

    [Fact]
    public async Task A_right_code_starts_the_count_of_wrong_ones_again()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();

        // Four wrong codes and a right one, twice over: eight wrong codes, and never five in a row.
        for (var round = 1; round <= 2; round++)
        {
            clock.Set(clock.GetUtcNow().AddMinutes(1));
            await client.SignInAsync(Ada);

            for (var attempt = 1; attempt <= 4; attempt++)
            {
                Assert.Contains(WrongCode, (await client.EnterCodeAsync(app.WrongCodeAt(clock.GetUtcNow()))).Page);
            }

            Assert.Equal(4, StoredTwoFactor.Of(application.Database, Ada).FailedCodes);
            Assert.Equal("/", (await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()))).Path);
            Assert.Equal(0, StoredTwoFactor.Of(application.Database, Ada).FailedCodes);

            await client.SignOutAsync();
        }

        Assert.Null(StoredTwoFactor.Of(application.Database, Ada).LockedUntil);
    }

    [Fact]
    public async Task Wrong_codes_count_for_the_account_whichever_browser_sent_them()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        await application.RegisterWithTwoFactorAsync(Grace, clock);
        using var one = application.CreateClient();
        using var other = application.CreateClient();
        var wrong = app.WrongCodeAt(clock.GetUtcNow());

        await one.SignInAsync(Ada);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Contains(WrongCode, (await one.EnterCodeAsync(wrong)).Page);
        }

        // Starting again on another browser does not start the count again.
        await other.SignInAsync(Ada);
        Assert.Contains(WrongCode, (await other.EnterCodeAsync(wrong)).Page);
        Assert.Contains(Refused, (await other.EnterCodeAsync(wrong)).Page);

        // The first browser is refused too, with the right code.
        Assert.Contains(Refused, (await one.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()))).Page);
        Assert.Empty(StoredSession.All(application.Database));

        // Another account is not touched.
        Assert.Null(StoredTwoFactor.Of(application.Database, Grace).LockedUntil);
    }

    [Fact]
    public async Task Wrong_codes_that_arrive_at_the_same_time_are_all_counted()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        var wrong = app.WrongCodeAt(clock.GetUtcNow());

        // Four browsers, each with the password given and the form open.
        var browsers = Enumerable.Range(0, 4).Select(_ => application.CreateClient()).ToList();
        try
        {
            var forms = new List<string>();
            foreach (var browser in browsers)
            {
                await browser.SignInAsync(Ada);
                forms.Add(Browser.AntiforgeryTokenIn(await browser.PageAsync(SessionAuthentication.SignInCodePath)));
            }

            var answers = await Task.WhenAll(browsers.Select((browser, index) =>
                browser.PostAsync(SessionAuthentication.SignInCodePath, "sign-in-code", forms[index], ("Code", wrong))));

            // Each was judged after the one before it, so none of them was lost.
            Assert.All(answers, answer => Assert.Contains(WrongCode, answer.Page));
            Assert.Equal(4, StoredTwoFactor.Of(application.Database, Ada).FailedCodes);
        }
        finally
        {
            browsers.ForEach(browser => browser.Dispose());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData("١٢٣٤٥٦")] // Six digits, and not the digits a code is written in.
    [InlineData("ABCD-EFGH-IJKL")] // Shorter than a recovery code.
    [InlineData("ABCD-EFGH-IJKL-MNOP-QRST")] // Longer than one.
    [InlineData("ABCD-EFGH-IJKL-MN01")] // With characters that no recovery code has.
    [InlineData("' OR 1=1 --")]
    public async Task What_cannot_be_a_code_is_refused_without_being_counted(string code)
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        // Posted as it is: a browser would not send the form without a code.
        using var answer = await client.SendFormAsync(
            SessionAuthentication.SignInCodePath,
            "sign-in-code",
            Browser.AntiforgeryTokenIn(await client.PageAsync(SessionAuthentication.SignInCodePath)),
            ("Code", code));

        // The form again, with its answer, and not an error. It was no guess at a code.
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(SessionAuthentication.SignInCodePath, answer.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains(WrongCode, await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(StoredSession.All(application.Database));
        Assert.Equal(0, StoredTwoFactor.Of(application.Database, Ada).FailedCodes);
    }

    [Fact]
    public async Task The_same_code_sent_twice_at_the_same_time_signs_in_once()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var one = application.CreateClient();
        using var other = application.CreateClient();
        var code = app.CodeAt(clock.GetUtcNow());

        await one.SignInAsync(Ada);
        await other.SignInAsync(Ada);
        var formOfOne = Browser.AntiforgeryTokenIn(await one.PageAsync(SessionAuthentication.SignInCodePath));
        var formOfOther = Browser.AntiforgeryTokenIn(await other.PageAsync(SessionAuthentication.SignInCodePath));

        var answers = await Task.WhenAll(
            one.PostAsync(SessionAuthentication.SignInCodePath, "sign-in-code", formOfOne, ("Code", code)),
            other.PostAsync(SessionAuthentication.SignInCodePath, "sign-in-code", formOfOther, ("Code", code)));

        // One of them is signed in, and the other finds the code used.
        Assert.Single(answers, answer => answer.Path == "/");
        Assert.Single(answers, answer => answer.Path == SessionAuthentication.SignInCodePath && answer.Page.Contains(WrongCode));
        Assert.Single(StoredSession.All(application.Database));
    }

    [Fact]
    public async Task Only_the_browser_that_gave_the_password_can_give_the_code()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var owner = application.CreateClient();
        using var stranger = application.CreateClient();
        await owner.SignInAsync(Ada);

        // A browser where nobody gave a password is sent to the page that takes one, and from
        // there back to where it was going.
        Assert.Equal(
            $"{SessionAuthentication.SignInPath}?returnUrl=%2Faccount",
            (await stranger.OpenAsync($"{SessionAuthentication.SignInCodePath}?returnUrl=%2Faccount")).Address);

        // So is its form, though the code is right and a sign-in of the account waits for one.
        var (path, page) = await stranger.PostAsync(
            SessionAuthentication.SignInCodePath,
            "sign-in-code",
            Browser.AntiforgeryTokenIn(await stranger.PageAsync(SessionAuthentication.SignInPath)),
            ("Code", app.CodeAt(clock.GetUtcNow())));

        Assert.Equal(SessionAuthentication.SignInPath, path);
        Assert.Contains("<h1>Sign in</h1>", page);
        Assert.Empty(StoredSession.All(application.Database));

        // The code was not looked at, so the owner can still use it.
        Assert.Equal("/", (await owner.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()))).Path);
    }

    [Fact]
    public async Task A_sign_in_that_waits_for_a_code_ends_after_ten_minutes()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        var start = clock.GetUtcNow();
        await client.SignInAsync(Ada);

        // The page is there until the last second.
        clock.Set(start.AddMinutes(10).AddSeconds(-1));
        Assert.Equal(SessionAuthentication.SignInCodePath, (await client.OpenAsync(SessionAuthentication.SignInCodePath)).Address);
        var form = Browser.AntiforgeryTokenIn(await client.PageAsync(SessionAuthentication.SignInCodePath));

        // Then the right code signs nobody in, and the password is asked for again.
        clock.Set(start.AddMinutes(10));
        var (path, page) = await client.PostAsync(
            SessionAuthentication.SignInCodePath, "sign-in-code", form, ("Code", app.CodeAt(clock.GetUtcNow())));

        Assert.Equal(SessionAuthentication.SignInPath, path);
        Assert.Contains("<h1>Sign in</h1>", page);
        Assert.Empty(StoredSession.All(application.Database));
        Assert.Equal(SessionAuthentication.SignInPath, (await client.OpenAsync(SessionAuthentication.SignInCodePath)).Address);

        // Giving the password again starts another, and removes the one that ended.
        await client.SignInAsync(Ada);
        Assert.Equal(clock.GetUtcNow().AddMinutes(10), Assert.Single(StoredPendingSignIn.All(application.Database)).ExpiresAt);
        Assert.Equal("/", (await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()))).Path);
    }

    [Fact]
    public async Task A_password_reset_ends_a_sign_in_that_waits_for_a_code()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();

        // Someone gave the password, and then the owner changed it.
        await client.SignInAsync(Ada);
        await application.RequestResetAsync(Ada);
        Assert.Equal(
            PasswordResetOutcome.Done,
            await application.ResetAsync(AccountsApplication.TokenIn(application.Email.Sent[^1]), "another long password"));

        Assert.Empty(StoredPendingSignIn.All(application.Database));

        // The code does not finish a sign-in that was started with the earlier password.
        var (path, _) = await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));

        Assert.Equal(SessionAuthentication.SignInPath, path);
        Assert.Empty(StoredSession.All(application.Database));

        // A reset does not turn two-factor authentication off: the new password asks for a code too.
        Assert.Equal(SessionAuthentication.SignInCodePath, (await client.SignInAsync(Ada, "another long password")).Path);
        Assert.Equal("/", (await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()))).Path);
    }

    [Fact]
    public async Task Turning_two_factor_off_ends_a_sign_in_that_waits_for_a_code()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var laptop = application.CreateClient();
        using var phone = application.CreateClient();

        await laptop.SignInAsync(Ada);
        await laptop.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));

        // The phone gave the password, and then the laptop turned two-factor authentication off.
        clock.Set(clock.GetUtcNow().AddMinutes(1));
        await phone.SignInAsync(Ada);
        await laptop.SubmitAsync("/account/two-factor/turn-off", "turn-off", ("Code", app.CodeAt(clock.GetUtcNow())));

        Assert.Empty(StoredPendingSignIn.All(application.Database));

        // The phone starts again, and now the password is enough.
        clock.Set(clock.GetUtcNow().AddMinutes(1));
        Assert.Equal(SessionAuthentication.SignInPath, (await phone.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()))).Path);
        Assert.Equal("/", (await phone.SignInAsync(Ada)).Path);
    }

    [Fact]
    public async Task Signing_out_ends_a_sign_in_that_waits_for_a_code()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClientThatStays();
        await client.SubmitAsync(
            SessionAuthentication.SignInPath, "sign-in", ("Email", Ada), ("Password", AccountsApplication.Password));
        Assert.Single(StoredPendingSignIn.All(application.Database));

        // Nobody is signed in, so the request needs no token against request forgery.
        using var answer = await client.SendFormAsync(WebUi.WebUiModule.SignOutPath, "sign-out", "no-token-at-all");

        // The sign-in is gone from the database, and its cookie is taken from the browser.
        Assert.Empty(StoredPendingSignIn.All(application.Database));
        Assert.StartsWith(
            "__Host-ecnanib-sign-in=; expires=Thu, 01 Jan 1970",
            Assert.Single(Browser.CookiesSetBy(answer), cookie => cookie.StartsWith("__Host-ecnanib-sign-in=", StringComparison.Ordinal)));

        using var page = await client.GetAsync(SessionAuthentication.SignInCodePath, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Empty(StoredSession.All(application.Database));
    }

    [Fact]
    public async Task A_sign_in_that_races_with_turning_two_factor_on_does_not_get_in_without_a_code()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        var form = Browser.AntiforgeryTokenIn(await client.PageAsync(SessionAuthentication.SignInPath));

        // Two-factor authentication is being turned on: the transaction has changed the row of
        // the account, and has not ended.
        using var connection = application.Database.Open();
        using var turningOn = connection.BeginTransaction();
        using (var change = new NpgsqlCommand(
            """
            UPDATE users
            SET two_factor_enabled_at = registered_at, two_factor_secret = '\x00', two_factor_secret_key_id = 'k2'
            WHERE email = @email
            """,
            connection,
            turningOn))
        {
            change.Parameters.AddWithValue("email", Ada);
            Assert.Equal(1, change.ExecuteNonQuery());
        }

        // The right password arrives meanwhile. It was verified against a row that says nothing
        // of two-factor authentication yet, and it waits for the row before it starts anything.
        var signIn = client.PostAsync(
            SessionAuthentication.SignInPath, "sign-in", form, ("Email", Ada), ("Password", AccountsApplication.Password));

        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(signIn.IsCompleted);
        Assert.Empty(StoredSession.All(application.Database));

        turningOn.Commit();

        // It finds two-factor authentication on, and asks for a code.
        var (path, page) = await signIn;

        Assert.Equal(SessionAuthentication.SignInCodePath, path);
        Assert.DoesNotContain("Sign out", page);
        Assert.Empty(StoredSession.All(application.Database));
        Assert.Single(StoredPendingSignIn.All(application.Database));
    }

    [Fact]
    public async Task Giving_the_password_does_not_wait_for_a_sign_in_that_another_request_is_ending()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var earlier = application.CreateClient();
        using var client = application.CreateClient();

        // A sign-in of the account that nobody gave a code for in time.
        await earlier.SignInAsync(Ada);
        clock.Set(clock.GetUtcNow().AddMinutes(11));

        // Another request is ending it at this moment: its transaction has deleted the row,
        // and has not ended. Two passwords of one account, each with such a sign-in of its own
        // to end, would otherwise wait for each other for ever.
        using var connection = application.Database.Open();
        using var ending = connection.BeginTransaction();
        using (var delete = new NpgsqlCommand("DELETE FROM pending_sign_ins", connection, ending))
        {
            Assert.Equal(1, delete.ExecuteNonQuery());
        }

        // The password clears away what waited too long, and leaves that one to the other request.
        var (path, _) = await client.SignInAsync(Ada).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.Equal(SessionAuthentication.SignInCodePath, path);

        // The other request changed its mind. Its row is still there, beside the new one.
        ending.Rollback();
        Assert.Equal(2, StoredPendingSignIn.All(application.Database).Count);
    }

    [Fact]
    public async Task A_code_signs_nobody_in_when_the_wait_ended_while_the_code_was_judged()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);
        var form = Browser.AntiforgeryTokenIn(await client.PageAsync(SessionAuthentication.SignInCodePath));
        var code = app.CodeAt(clock.GetUtcNow());

        // Signing out in another tab is ending the wait: its transaction has deleted the row,
        // and has not ended. Signing out does not wait for the row of the account.
        using var connection = application.Database.Open();
        using var signingOut = connection.BeginTransaction();
        using (var delete = new NpgsqlCommand("DELETE FROM pending_sign_ins", connection, signingOut))
        {
            Assert.Equal(1, delete.ExecuteNonQuery());
        }

        // The right code arrives meanwhile. It finds the sign-in waiting, and then waits to end it.
        var entering = client.PostAsync(SessionAuthentication.SignInCodePath, "sign-in-code", form, ("Code", code));

        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(entering.IsCompleted);

        signingOut.Commit();

        // Nothing waited for the code any more, so nothing starts.
        var (path, page) = await entering;

        Assert.Equal(SessionAuthentication.SignInPath, path);
        Assert.DoesNotContain("Sign out", page);
        Assert.Empty(StoredSession.All(application.Database));

        // The code is not spent by it: with the password given again, it signs in.
        await client.SignInAsync(Ada);
        Assert.Equal("/", (await client.EnterCodeAsync(code)).Path);
    }

    [Fact]
    public async Task A_code_that_two_steps_of_time_share_is_still_accepted_once()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var owner = application.CreateClient();
        using var watcher = application.CreateClient();

        // About once in a million steps, the step before and the step after have the same
        // code. This is the first time that happens for the key of the app.
        var step = Totp.StepAt(clock.GetUtcNow()) + 2;
        var last = step + 50_000_000;
        while (step < last && Totp.CodeAt(app.Key, step - 1) != Totp.CodeAt(app.Key, step + 1))
        {
            step++;
        }

        Assert.True(step < last, "No two steps that near each other share a code.");
        clock.Set(DateTimeOffset.FromUnixTimeSeconds(step * Totp.StepSeconds));
        var code = Totp.CodeAt(app.Key, step - 1);

        await owner.SignInAsync(Ada);
        Assert.Equal("/", (await owner.EnterCodeAsync(code)).Path);

        // It is the code of a later step too, and that step is not left for a second use.
        await watcher.SignInAsync(Ada);
        var (path, page) = await watcher.EnterCodeAsync(code);

        Assert.Equal(SessionAuthentication.SignInCodePath, path);
        Assert.Contains(WrongCode, page);
        Assert.Single(StoredSession.All(application.Database));
        Assert.Equal(step + 1, StoredTwoFactor.Of(application.Database, Ada).LastStep);
    }

    [Fact]
    public async Task The_session_the_browser_held_ends_when_the_code_is_accepted_and_not_before()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (ada, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        var grace = await application.RegisterVerifiedAsync(Grace);
        using var client = application.CreateClient();
        await client.SignInAsync(Grace);

        // The password of another account is given on a browser that is signed in.
        var (path, _) = await client.SignInAsync(Ada);

        // The browser is still signed in to the first account, and to nothing else.
        Assert.Equal(SessionAuthentication.SignInCodePath, path);
        Assert.Contains("You are signed in as grace@example.com.", await client.PageAsync("/account"));
        Assert.Equal(grace.Id, Assert.Single(StoredSession.All(application.Database)).UserId);

        // With the code, it is signed in to the other account, and the first session has ended.
        var (after, _) = await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));

        Assert.Equal("/", after);
        Assert.Contains("You are signed in as ada@example.com.", await client.PageAsync("/account"));
        Assert.Equal(ada.Id, Assert.Single(StoredSession.All(application.Database)).UserId);
    }

    [Fact]
    public async Task A_wrong_password_gets_the_same_answer_whether_two_factor_is_on_or_not()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        await application.RegisterWithTwoFactorAsync(Ada, clock);
        await application.RegisterVerifiedAsync(Grace);
        using var client = application.CreateClient();

        var withIt = await client.SignInAsync(Ada, "WRONG-MARKER is not the password");
        var without = await client.SignInAsync(Grace, "WRONG-MARKER is not the password");

        // Only the right password tells that an account asks for a code.
        Assert.Equal(SessionAuthentication.SignInPath, withIt.Path);
        Assert.Contains("The email address or the password is not right.", withIt.Page);
        Assert.Equal(Browser.AsSeen(without.Page).Replace(Grace, Ada), Browser.AsSeen(withIt.Page));
        Assert.Empty(StoredPendingSignIn.All(application.Database));
        Assert.Empty(StoredSession.All(application.Database));
    }

    [Theory]
    [InlineData("https://evil.example/account")]
    [InlineData("//evil.example/account")]
    [InlineData("/\\evil.example/account")]
    public async Task After_the_code_signing_in_does_not_lead_to_another_site(string returnUrl)
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        var form = $"{SessionAuthentication.SignInPath}?returnUrl={Uri.EscapeDataString(returnUrl)}";

        using var afterPassword = await client.SendFormAsync(
            form,
            "sign-in",
            Browser.AntiforgeryTokenIn(await client.PageAsync(form)),
            ("Email", Ada),
            ("Password", AccountsApplication.Password));

        // The page that takes the code is one of this application, and carries the address along.
        var codeAddress = afterPassword.RequestMessage!.RequestUri!;
        Assert.Equal("localhost", codeAddress.Host);
        Assert.Equal(SessionAuthentication.SignInCodePath, codeAddress.AbsolutePath);

        using var afterCode = await client.SendFormAsync(
            codeAddress.PathAndQuery,
            "sign-in-code",
            Browser.AntiforgeryTokenIn(await afterPassword.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)),
            ("Code", app.CodeAt(clock.GetUtcNow())));

        // Signed in, and at the start page of the application.
        Assert.Equal(HttpStatusCode.OK, afterCode.StatusCode);
        Assert.Equal(new Uri("https://localhost/"), afterCode.RequestMessage!.RequestUri);
        Assert.Single(StoredSession.All(application.Database));
    }

    [Fact]
    public async Task A_post_without_the_token_against_request_forgery_signs_nobody_in()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        await client.PostAsync(
            SessionAuthentication.SignInCodePath, "sign-in-code", "not-the-token", ("Code", app.CodeAt(clock.GetUtcNow())));

        Assert.Empty(StoredSession.All(application.Database));
        Assert.DoesNotContain("Sign out", await client.PageAsync("/"));

        // The code was not looked at, so it still signs in.
        Assert.Equal("/", (await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()))).Path);
    }

    [Fact]
    public async Task Neither_the_key_nor_a_code_reaches_the_log()
    {
        // With the host's own events about each request turned on, which name its address.
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(
            clock: clock,
            settings: file => file.WithJson("Logging:Overrides", """{ "Microsoft.AspNetCore": "Information" }"""));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // Turned on, with a wrong code first.
        var page = await client.PageAsync(Browser.TwoFactorPage);
        var app = AuthenticatorApp.WithKeyOn(page);
        var sealedKey = Browser.SealedKeyIn(page);
        var wrong = app.WrongCodeAt(Morning);
        var first = app.CodeAt(Morning);

        await client.PostAsync(
            Browser.TwoFactorPage,
            "turn-on",
            Browser.AntiforgeryTokenIn(page),
            ("SealedKey", sealedKey),
            ("Code", wrong),
            ("Password", AccountsApplication.Password));
        var (_, turnedOn) = await client.PostAsync(
            Browser.TwoFactorPage,
            "turn-on",
            Browser.AntiforgeryTokenIn(page),
            ("SealedKey", sealedKey),
            ("Code", first),
            ("Password", AccountsApplication.Password));
        var recoveryCodes = Browser.RecoveryCodesIn(turnedOn);
        Assert.Equal(10, recoveryCodes.Count);
        await client.SignOutAsync();

        // Signed in with a wrong code, a code from the app, and a recovery code.
        clock.Set(Morning.AddMinutes(5));
        var second = app.CodeAt(clock.GetUtcNow());
        await client.SignInAsync(Ada);
        await client.EnterCodeAsync(wrong);
        Assert.Equal("/", (await client.EnterCodeAsync(second)).Path);
        await client.SignOutAsync();
        await client.SignInAsync(Ada);
        Assert.Equal("/", (await client.EnterCodeAsync(recoveryCodes[0])).Path);

        var log = application.LogText();

        // The requests are in the log, and what was done is.
        Assert.Contains(SessionAuthentication.SignInCodePath, log);
        Assert.Contains("turned two-factor authentication on", log);
        Assert.Contains("used a recovery code", log);

        // The key is not, in any form it has, and neither is a code of either kind.
        Assert.DoesNotContain(app.KeyText, log);
        Assert.DoesNotContain(app.KeyText[..8], log);
        Assert.DoesNotContain(Convert.ToHexString(app.Key), log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(app.Key), log);
        Assert.DoesNotContain(sealedKey, log);
        Assert.All(new[] { wrong, first, second }, code => Assert.DoesNotMatch(Standing(code), log));
        Assert.All(recoveryCodes, code =>
        {
            Assert.DoesNotContain(code, log);
            Assert.DoesNotContain(code.Replace("-", string.Empty), log);
        });
    }

    /// <summary>
    /// Six digits on their own, as a logged code would stand. The same digits inside an id or a
    /// longer number are not a code.
    /// </summary>
    private static string Standing(string code) => $"(?<![0-9A-Za-z]){code}(?![0-9A-Za-z])";
}
