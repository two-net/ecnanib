using System.Net;
using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// Sign-in, registration, and password reset are rate-limited (NFR-SEC-4): each may be tried so
/// many times from one client, and so many times for one email address, within a period. So may
/// the password of an account, by a browser that is signed in to it. The numbers come from the
/// configuration file. In these tests:
/// <list type="bullet">
/// <item>Sign-in: 5 from one client and 3 for one address, in 15 minutes.</item>
/// <item>Registration: 4 from one client and 2 for one address, in 60 minutes.</item>
/// <item>Password reset: 4 from one client and 2 for one address, in 30 minutes.</item>
/// </list>
/// </summary>
public class AttemptLimitTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string NotRight = "The email address or the password is not right.";
    private const string Refused = "Too many attempts. Try again in up to";
    private const string WrongPassword = "That is not your password.";

    private const string SignInRefused = "A sign-in was refused: too many attempts {Limit}";
    private const string RegistrationRefused = "A registration was refused: too many attempts {Limit}";
    private const string ResetRefused = "A password reset was refused: too many attempts {Limit}";

    private const string ConfirmPage = SessionAuthentication.ReauthenticationPath;

    // The header with which a request of these tests says where it comes from. A request
    // without it comes from nowhere that the connection knows, as every request of the other
    // tests does: all of those count as one client.
    private const string From = "Test-From";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_password_can_be_tried_for_an_address_only_so_often_whoever_tries()
    {
        using var application = Limited();
        await application.RegisterVerifiedAsync(Ada);

        // Three clients, one try each: no one of them is near what a client may try.
        foreach (var address in new[] { "203.0.113.1", "203.0.113.2", "203.0.113.3" })
        {
            using var guessing = ClientAt(application, address);
            var (_, page) = await guessing.SignInAsync(Ada, "WRONG-MARKER is not the password");

            Assert.Contains(NotRight, page);
        }

        // The fourth try is refused, from yet another client, and with the right password.
        using var owner = ClientAt(application, "198.51.100.7");
        using var answer = await SendSignInAsync(owner, Ada, AccountsApplication.Password);
        var refused = await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, answer.StatusCode);
        Assert.Contains($"{Refused} 15 minutes.", refused);
        Assert.DoesNotContain(NotRight, refused);
        Assert.Contains("<form method=\"post\"", refused);
        Assert.Empty(Browser.CookiesSetBy(answer));
        Assert.Empty(StoredSession.All(application.Database));

        // Nothing was looked up or hashed for it: the log has three sign-ins that failed, and one that was refused.
        Assert.Equal(3, application.Events().Count(written => written.Template.StartsWith("A sign-in failed", StringComparison.Ordinal)));
        Assert.Equal("for one address", Assert.Single(application.Events(), written => written.Template == SignInRefused).Text("Limit"));

        // The log names neither the address nor a client.
        Assert.DoesNotContain(Ada, application.LogText());
        Assert.DoesNotContain("203.0.113.", application.LogText());
        Assert.DoesNotContain("198.51.100.", application.LogText());
    }

    [Fact]
    public async Task An_address_without_an_account_is_refused_as_one_that_has_an_account_is()
    {
        using var application = Limited();
        await application.RegisterVerifiedAsync(Ada);

        // Each from a client of its own, so that it is the address that is refused.
        var answers = new Dictionary<string, (HttpStatusCode Status, string Page)>();
        foreach (var (address, from) in new[] { (Ada, "203.0.113.1"), (Grace, "203.0.113.2") })
        {
            using var client = ClientAt(application, from);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Assert.Contains(NotRight, (await client.SignInAsync(address, "WRONG-MARKER is not the password")).Page);
            }

            using var answer = await SendSignInAsync(client, address, "WRONG-MARKER is not the password");
            answers[address] = (answer.StatusCode, await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        // The same answer, so being refused does not tell whether an address has an account.
        Assert.Equal(HttpStatusCode.TooManyRequests, answers[Ada].Status);
        Assert.Equal(answers[Ada].Status, answers[Grace].Status);
        Assert.Contains(Refused, answers[Ada].Page);
        Assert.Equal(Browser.AsSeen(answers[Ada].Page), Browser.AsSeen(answers[Grace].Page).Replace(Grace, Ada));
    }

    [Fact]
    public async Task The_wait_ends_when_the_oldest_attempt_is_a_period_old_and_being_refused_does_not_make_it_longer()
    {
        var clock = new TestClock(Morning);
        using var application = Limited(clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        // Three tries, at 9:00, 9:05, and 9:10.
        foreach (var minute in new[] { 0, 5, 10 })
        {
            clock.Set(Morning.AddMinutes(minute));
            Assert.Contains(NotRight, (await client.SignInAsync(Ada, "WRONG-MARKER is not the password")).Page);
        }

        // Refused from then on, however often it is tried.
        foreach (var minute in new[] { 11, 12, 13, 14 })
        {
            clock.Set(Morning.AddMinutes(minute));
            Assert.Contains(Refused, (await client.SignInAsync(Ada)).Page);
        }

        clock.Set(Morning.AddMinutes(15).AddSeconds(-1));
        Assert.Contains(Refused, (await client.SignInAsync(Ada)).Page);

        // At 9:15 the try of 9:00 is 15 minutes old, and there is room for one.
        clock.Set(Morning.AddMinutes(15));
        var (path, page) = await client.SignInAsync(Ada);

        Assert.Equal("/", path);
        Assert.Contains("Sign out", page);

        // For one: the tries of 9:05, 9:10, and 9:15 count now, the one that signed in among them.
        using var other = application.CreateClient();
        Assert.Contains(Refused, (await other.SignInAsync(Ada)).Page);

        clock.Set(Morning.AddMinutes(20));
        Assert.Equal("/", (await other.SignInAsync(Ada)).Path);
    }

    [Fact]
    public async Task One_client_can_try_only_so_often_whatever_the_address()
    {
        using var application = Limited();
        using var guessing = ClientAt(application, "203.0.113.7");
        using var owner = ClientAt(application, "198.51.100.7");
        await application.RegisterVerifiedAsync(Ada);

        // Five addresses, one try each: no one of them is near what an address may be tried.
        for (var number = 1; number <= 5; number++)
        {
            Assert.Contains(NotRight, (await guessing.SignInAsync($"user{number}@example.com", "WRONG-MARKER is not the password")).Page);
        }

        using var answer = await SendSignInAsync(guessing, Ada, AccountsApplication.Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, answer.StatusCode);
        Assert.Contains($"{Refused} 15 minutes.", await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("from one client", Assert.Single(application.Events(), written => written.Template == SignInRefused).Text("Limit"));
        Assert.Empty(StoredSession.All(application.Database));

        // Another client is not refused. And what the first one was refused took nothing from
        // the address: it can still be tried three times.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Contains(NotRight, (await owner.SignInAsync(Ada, "WRONG-MARKER is not the password")).Page);
        }

        Assert.Equal("/", (await owner.SignInAsync(Ada)).Path);
    }

    [Theory]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:3::1")]
    [InlineData("203.0.113.7", "::ffff:203.0.113.7", "203.0.113.8")]
    public async Task A_client_is_an_address_or_the_network_that_one_subscriber_of_IPv6_is_given(string one, string same, string another)
    {
        using var application = Limited();
        await application.RegisterVerifiedAsync(Ada);

        using (var first = ClientAt(application, one))
        {
            for (var number = 1; number <= 5; number++)
            {
                Assert.Contains(NotRight, (await first.SignInAsync($"user{number}@example.com", "WRONG-MARKER is not the password")).Page);
            }
        }

        // The same client under another address: of the same network of 64 bits, or the same
        // IPv4 address as an IPv6 socket writes it.
        using var again = ClientAt(application, same);
        Assert.Contains(Refused, (await again.SignInAsync(Ada)).Page);

        using var other = ClientAt(application, another);
        Assert.Equal("/", (await other.SignInAsync(Ada)).Path);
    }

    [Fact]
    public async Task What_no_account_can_have_is_refused_for_what_it_is_and_is_not_counted()
    {
        using var application = Limited();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        // More often than either limit allows: no password, text that is not an address, and a
        // password that is longer than any can be. None of it costs anything, or tells anything.
        (string Email, string Password)[] tries =
        [
            (Ada, string.Empty),
            ("not an address", AccountsApplication.Password),
            (Ada, new string('x', PasswordRules.MaximumLength + 1)),
        ];

        for (var round = 0; round < 3; round++)
        {
            foreach (var (email, password) in tries)
            {
                // Posted as it is: a browser would not send a form without a password.
                using var answer = await SendSignInAsync(client, email, password);

                Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
                Assert.Contains(NotRight, await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            }
        }

        Assert.Equal("/", (await client.SignInAsync(Ada)).Path);
    }

    [Fact]
    public async Task The_registration_form_sends_an_address_only_so_much_email()
    {
        var clock = new TestClock(Morning);
        using var application = Limited(clock);
        using var client = application.CreateClient();

        // Twice: the account, and then a new link for it.
        Assert.Equal("/register/sent", (await RegisterOnPageAsync(client, Ada)).Path);
        Assert.Equal("/register/sent", (await RegisterOnPageAsync(client, " ADA@example.com ")).Path);
        Assert.Equal(2, application.Email.Sent.Count);
        var stored = application.EverythingStored();

        // The third is refused, however the address is typed, from wherever it comes.
        using var elsewhere = ClientAt(application, "203.0.113.7");
        using var answer = await SendRegistrationAsync(elsewhere, "Ada@Example.com");
        var page = await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, answer.StatusCode);
        Assert.Contains($"{Refused} 60 minutes.", page);
        Assert.Contains("<form method=\"post\"", page);
        Assert.Equal(2, application.Email.Sent.Count);
        Assert.Equal(stored, application.EverythingStored());
        Assert.Equal("for one address", Assert.Single(application.Events(), written => written.Template == RegistrationRefused).Text("Limit"));
        Assert.DoesNotContain(Ada, application.LogText());

        // So is one that no page made.
        Assert.Equal(RegistrationOutcome.TooManyAttempts, (await application.RegisterAsync(Ada)).Outcome);
        Assert.Equal(2, application.Email.Sent.Count);

        // An hour after the two registrations there is room again.
        clock.Set(Morning.AddMinutes(60));
        Assert.Equal("/register/sent", (await RegisterOnPageAsync(client, Ada)).Path);
        Assert.Equal(3, application.Email.Sent.Count);
    }

    [Fact]
    public async Task An_address_that_has_an_account_is_sent_only_so_many_notices_and_is_refused_as_a_new_one_is()
    {
        var clock = new TestClock(Morning);
        using var application = Limited(clock);
        await application.RegisterVerifiedAsync(Ada);

        // Later, so that the registration which made the account does not count any more.
        clock.Set(Morning.AddHours(2));

        // Each from a client of its own, so that it is the address that is refused.
        var answers = new Dictionary<string, (HttpStatusCode Status, string Page)>();
        foreach (var (address, from) in new[] { (Ada, "203.0.113.1"), (Grace, "203.0.113.2") })
        {
            using var client = ClientAt(application, from);
            Assert.Equal("/register/sent", (await RegisterOnPageAsync(client, address)).Path);
            Assert.Equal("/register/sent", (await RegisterOnPageAsync(client, address)).Path);

            using var answer = await SendRegistrationAsync(client, address);
            answers[address] = (answer.StatusCode, await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        // Two notices to the address that has an account, after the link that verified it, and
        // two links to the address that had none.
        Assert.Equal(3, application.Email.Sent.Count(message => message.To == Ada));
        Assert.Equal(1, application.Email.Sent.Count(message => message.To == Ada && message.TextBody.Contains(EmailVerification.Path)));
        Assert.Equal(2, application.Email.Sent.Count(message => message.To == Grace && message.TextBody.Contains(EmailVerification.Path)));

        Assert.Equal(HttpStatusCode.TooManyRequests, answers[Ada].Status);
        Assert.Equal(answers[Ada].Status, answers[Grace].Status);
        Assert.Equal(Browser.AsSeen(answers[Ada].Page), Browser.AsSeen(answers[Grace].Page).Replace(Grace, Ada));
    }

    [Fact]
    public async Task One_client_can_register_only_so_often()
    {
        using var application = Limited();
        using var client = ClientAt(application, "203.0.113.7");
        using var other = ClientAt(application, "198.51.100.7");

        for (var number = 1; number <= 4; number++)
        {
            Assert.Equal("/register/sent", (await RegisterOnPageAsync(client, $"user{number}@example.com")).Path);
        }

        using var answer = await SendRegistrationAsync(client, Ada);

        Assert.Equal(HttpStatusCode.TooManyRequests, answer.StatusCode);
        Assert.Contains($"{Refused} 60 minutes.", await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("from one client", Assert.Single(application.Events(), written => written.Template == RegistrationRefused).Text("Limit"));
        Assert.Null(StoredUser.Of(application.Database, Ada));
        Assert.Equal(4, application.Email.Sent.Count);

        // Another client registers the address: being refused took nothing from it.
        Assert.Equal("/register/sent", (await RegisterOnPageAsync(other, Ada)).Path);
        Assert.Equal("/register/sent", (await RegisterOnPageAsync(other, Ada)).Path);
        Assert.NotNull(StoredUser.Of(application.Database, Ada));
    }

    [Fact]
    public async Task A_registration_that_breaks_a_rule_is_not_counted()
    {
        using var application = Limited();
        using var client = application.CreateClient();

        // More often than either limit allows: each is told what is wrong with it, and nothing is sent.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var answer = await SendRegistrationAsync(client, Ada, password: "too short");

            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.Contains("Choose a password of at least", await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal("/register/sent", (await RegisterOnPageAsync(client, Ada)).Path);
        Assert.Single(application.Email.Sent);
    }

    [Fact]
    public async Task A_reset_link_can_be_asked_for_an_address_only_so_often_and_the_link_that_was_sent_still_works()
    {
        var clock = new TestClock(Morning);
        using var application = Limited(clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        var before = application.Email.Sent.Count;

        Assert.Equal("/forgot-password/sent", (await client.SubmitAsync("/forgot-password", "forgot-password", ("Email", Ada))).Path);
        clock.Set(Morning.AddMinutes(10));
        Assert.Equal("/forgot-password/sent", (await client.SubmitAsync("/forgot-password", "forgot-password", ("Email", " ADA@example.com "))).Path);
        var link = AccountsApplication.TokenIn(application.Email.Sent[^1]);
        var stored = application.EverythingStored();

        // The third is refused, however the address is typed, from wherever it comes.
        using var elsewhere = ClientAt(application, "203.0.113.7");
        using var answer = await SendResetRequestAsync(elsewhere, "Ada@Example.com");
        var page = await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, answer.StatusCode);
        Assert.Contains($"{Refused} 30 minutes.", page);
        Assert.Contains("<form method=\"post\"", page);
        Assert.Equal(before + 2, application.Email.Sent.Count);
        Assert.Equal(stored, application.EverythingStored());
        Assert.Equal("for one address", Assert.Single(application.Events(), written => written.Template == ResetRefused).Text("Limit"));

        // So is one that no page made.
        Assert.Equal(PasswordResetRequestOutcome.TooManyAttempts, await application.RequestResetAsync(Ada));
        Assert.Equal(before + 2, application.Email.Sent.Count);

        // Half an hour after the first, there is room for one more. Until then nobody can
        // take the link that was sent away from its owner by asking for another: it works.
        clock.Set(Morning.AddMinutes(29));
        Assert.Equal(PasswordResetRequestOutcome.TooManyAttempts, await application.RequestResetAsync(Ada));
        Assert.Equal(PasswordResetOutcome.Done, await application.ResetAsync(link, "a new password, chosen with the link"));

        clock.Set(Morning.AddMinutes(30));
        Assert.Equal(PasswordResetRequestOutcome.Accepted, await application.RequestResetAsync(Ada));
    }

    [Fact]
    public async Task An_address_without_an_account_is_refused_a_reset_link_as_one_that_has_an_account_is()
    {
        using var application = Limited();
        await application.RegisterVerifiedAsync(Ada);
        var before = application.Email.Sent.Count;

        // Each from a client of its own, so that it is the address that is refused.
        var answers = new Dictionary<string, (HttpStatusCode Status, string Page)>();
        foreach (var (address, from) in new[] { (Ada, "203.0.113.1"), (Grace, "203.0.113.2") })
        {
            using var client = ClientAt(application, from);
            await client.SubmitAsync("/forgot-password", "forgot-password", ("Email", address));
            await client.SubmitAsync("/forgot-password", "forgot-password", ("Email", address));

            using var answer = await SendResetRequestAsync(client, address);
            answers[address] = (answer.StatusCode, await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        // Two links, and two notices that the address has no account.
        Assert.Equal(before + 4, application.Email.Sent.Count);
        Assert.Equal(2, application.Email.Sent.Count(message => message.To == Grace));

        Assert.Equal(HttpStatusCode.TooManyRequests, answers[Ada].Status);
        Assert.Equal(answers[Ada].Status, answers[Grace].Status);
        Assert.Equal(Browser.AsSeen(answers[Ada].Page), Browser.AsSeen(answers[Grace].Page).Replace(Grace, Ada));
    }

    [Fact]
    public async Task One_client_can_ask_for_reset_links_only_so_often()
    {
        using var application = Limited();
        using var client = ClientAt(application, "203.0.113.7");
        using var other = ClientAt(application, "198.51.100.7");
        await application.RegisterVerifiedAsync(Ada);
        var before = application.Email.Sent.Count;

        for (var number = 1; number <= 4; number++)
        {
            await client.SubmitAsync("/forgot-password", "forgot-password", ("Email", $"user{number}@example.com"));
        }

        using var answer = await SendResetRequestAsync(client, Ada);

        Assert.Equal(HttpStatusCode.TooManyRequests, answer.StatusCode);
        Assert.Equal("from one client", Assert.Single(application.Events(), written => written.Template == ResetRefused).Text("Limit"));
        Assert.Equal(before + 4, application.Email.Sent.Count);
        Assert.Null(StoredUser.Of(application.Database, Ada)!.ResetTokenHash);

        // Another client is not refused.
        Assert.Equal("/forgot-password/sent", (await other.SubmitAsync("/forgot-password", "forgot-password", ("Email", Ada))).Path);
        Assert.NotNull(StoredUser.Of(application.Database, Ada)!.ResetTokenHash);
    }

    [Fact]
    public async Task The_password_of_an_account_can_be_tried_only_so_often_by_the_browsers_that_are_signed_in_to_it()
    {
        var clock = new TestClock(Morning);
        using var application = Limited(clock);
        using var left = application.CreateClient();
        using var second = application.CreateClient();
        using var graces = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        await application.RegisterVerifiedAsync(Grace);
        await left.SignInAsync(Ada);
        await second.SignInAsync(Ada);
        await graces.SignInAsync(Grace);

        // Someone at a browser that was left signed in tries passwords on the page that asks
        // for one before a sensitive action.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Contains(WrongPassword, (await left.ConfirmIdentityAsync("WRONG-MARKER is not the password")).Page);
        }

        // The fourth try is refused, and so is the right password, on every browser of the account.
        using var answer = await left.SendFormAsync(
            ConfirmPage, "confirm-identity", Browser.AntiforgeryTokenIn(await left.PageAsync(ConfirmPage)), ("Password", AccountsApplication.Password));
        var refused = await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, answer.StatusCode);
        Assert.Contains($"{Refused} 15 minutes.", refused);
        Assert.DoesNotContain(WrongPassword, refused);
        Assert.Contains(Refused, (await second.ConfirmIdentityAsync()).Page);
        Assert.All(StoredSession.All(application.Database), session => Assert.Null(session.ReauthenticatedAt));

        // Once for each browser that was refused, by the id of the account.
        var logged = application.Events()
            .Where(written => written.Template == "User {UserId} was refused confirming who they are: too many attempts {Limit}")
            .ToList();
        Assert.Equal(2, logged.Count);
        Assert.All(logged, written => Assert.Equal((ada.Id.ToString(), "for one account"), (written.Text("UserId"), written.Text("Limit"))));

        // It is the account's alone. Another user confirms who they are, and the sign-in form
        // of the account is not refused: its tries are counted apart.
        await graces.ConfirmIdentityAsync();
        Assert.Equal(Morning, Assert.Single(StoredSession.All(application.Database), session => session.ReauthenticatedAt is not null).ReauthenticatedAt);
        using var third = application.CreateClient();
        Assert.Equal("/", (await third.SignInAsync(Ada)).Path);

        // After the period the owner confirms who they are.
        clock.Set(Morning.AddMinutes(15));
        await second.ConfirmIdentityAsync();

        Assert.Equal(1, StoredSession.All(application.Database).Count(session => session.ReauthenticatedAt == Morning.AddMinutes(15)));
    }

    [Fact]
    public async Task The_password_that_turns_two_factor_on_is_counted_with_it()
    {
        var clock = new TestClock(Morning);
        using var application = Limited(clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // Two tries before a sensitive action, and a third on the form that turns two-factor
        // authentication on, with a code of the key on that form: whoever is at the browser can
        // make as many keys and codes as they like.
        Assert.Contains(WrongPassword, (await client.ConfirmIdentityAsync("WRONG-MARKER is not the password")).Page);
        Assert.Contains(WrongPassword, (await client.ConfirmIdentityAsync("WRONG-MARKER is not the password")).Page);

        using (var wrong = await SendTurnOnAsync(client, clock, "WRONG-MARKER is not the password"))
        {
            Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
            Assert.Contains("That is not your password.", await wrong.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        // The fourth is refused on both forms, with the right password.
        using var answer = await SendTurnOnAsync(client, clock, AccountsApplication.Password);
        var refused = await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, answer.StatusCode);
        Assert.Contains($"{Refused} 15 minutes.", refused);
        Assert.DoesNotContain("That is not your password.", refused);
        Assert.Contains("name=\"SealedKey\"", refused);
        Assert.True(StoredTwoFactor.Of(application.Database, Ada).IsEmpty);
        Assert.Contains(Refused, (await client.ConfirmIdentityAsync()).Page);
        Assert.Single(
            application.Events(),
            written => written.Template == "User {UserId} was refused turning two-factor authentication on: too many attempts {Limit}");

        // A code that is not right is told so, and is not a try of the password: after the
        // period, more of them than the limit allows leave room for the password.
        clock.Set(Morning.AddMinutes(15));
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var wrongCode = await SendTurnOnAsync(client, clock, AccountsApplication.Password, wrongCode: true);
            Assert.Contains("That code is not right.", await wrongCode.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        await client.TurnOnTwoFactorAsync(clock);
        Assert.NotNull(StoredTwoFactor.Of(application.Database, Ada).EnabledAt);
    }

    [Fact]
    public async Task A_proof_that_is_refused_spends_no_code_and_counts_no_wrong_one()
    {
        var clock = new TestClock(Morning);
        using var application = Limited(clock);
        using var client = application.CreateClient();
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);

        // Later, so that what it took to turn two-factor authentication on does not count any more.
        clock.Set(Morning.AddHours(1));
        await client.SignInAsync(Ada);
        await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));
        clock.Set(clock.GetUtcNow().AddMinutes(1));

        // Three proofs with a code that is not right: each is a try, and each is a wrong code.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Contains(
                "That code is not right.",
                (await client.ConfirmIdentityAsync(code: app.WrongCodeAt(clock.GetUtcNow()))).Page);
        }

        var counted = StoredTwoFactor.Of(application.Database, Ada);
        Assert.Equal(3, counted.FailedCodes);

        // The fourth is refused before its code is looked at, right or wrong.
        Assert.Contains(Refused, (await client.ConfirmIdentityAsync(code: app.CodeAt(clock.GetUtcNow()))).Page);
        Assert.Contains(Refused, (await client.ConfirmIdentityAsync(code: app.WrongCodeAt(clock.GetUtcNow()))).Page);

        // Neither was counted as a wrong code, and the right one was not spent: the last code
        // that the account accepted is still the one that signed in.
        var after = StoredTwoFactor.Of(application.Database, Ada);
        Assert.Equal((counted.FailedCodes, counted.LastStep, counted.LockedUntil), (after.FailedCodes, after.LastStep, after.LockedUntil));
        Assert.Null(Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);

        // After the period the proof is taken.
        clock.Set(clock.GetUtcNow().AddMinutes(15));
        await client.ConfirmIdentityAsync(code: app.CodeAt(clock.GetUtcNow()));

        Assert.Equal(clock.GetUtcNow(), Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);
    }

    [Fact]
    public async Task The_limits_are_those_of_the_configuration_file()
    {
        // One try for an address, where the other tests have three.
        using var application = new AccountsApplication(
            settings: file => file.WithJson("Limits:SignIn", """{ "PerClient": 5, "PerAddress": 1, "Minutes": 7 }"""));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        Assert.Contains(NotRight, (await client.SignInAsync(Ada, "WRONG-MARKER is not the password")).Page);
        Assert.Contains($"{Refused} 7 minutes.", (await client.SignInAsync(Ada)).Page);
    }

    /// <summary>
    /// The application with limits that a test reaches in a few tries, and with requests that
    /// say where they come from.
    /// </summary>
    private static AccountsApplication Limited(TestClock? clock = null) =>
        new(
            clock: clock,
            settings: file => file
                .WithJson("Limits:SignIn", """{ "PerClient": 5, "PerAddress": 3, "Minutes": 15 }""")
                .WithJson("Limits:Registration", """{ "PerClient": 4, "PerAddress": 2, "Minutes": 60 }""")
                .WithJson("Limits:PasswordReset", """{ "PerClient": 4, "PerAddress": 2, "Minutes": 30 }"""),
            host: host => host.PrepareEachRequest(request =>
            {
                if (request.Request.Headers.TryGetValue(From, out var address))
                {
                    request.Connection.RemoteIpAddress = IPAddress.Parse(address.ToString());
                }
            }));

    /// <summary>A browser whose requests arrive from an address, as a connection from that address does.</summary>
    private static HttpClient ClientAt(AccountsApplication application, string address)
    {
        var client = application.CreateClient();
        client.DefaultRequestHeaders.Add(From, address);

        return client;
    }

    /// <summary>Posts the sign-in form and returns the answer itself, with its status.</summary>
    private static async Task<HttpResponseMessage> SendSignInAsync(HttpClient client, string email, string password) =>
        await client.SendFormAsync(
            SessionAuthentication.SignInPath,
            "sign-in",
            Browser.AntiforgeryTokenIn(await client.PageAsync(SessionAuthentication.SignInPath)),
            ("Email", email),
            ("Password", password));

    /// <summary>Posts the registration form, with every document accepted, and returns the answer itself.</summary>
    private static async Task<HttpResponseMessage> SendRegistrationAsync(
        HttpClient client, string email, string password = AccountsApplication.Password) =>
        await client.SendFormAsync(
            "/register",
            "register",
            Browser.AntiforgeryTokenIn(await client.PageAsync("/register")),
            [
                ("Email", email),
                ("Password", password),
                .. AccountsApplication.EveryDocument.Select(accepted => (accepted.Document.ToString(), accepted.Version)),
            ]);

    /// <summary>Registers on the form, and follows where the answer leads.</summary>
    private static async Task<(string Path, string Page)> RegisterOnPageAsync(HttpClient client, string email)
    {
        using var answer = await SendRegistrationAsync(client, email);

        return (answer.RequestMessage!.RequestUri!.AbsolutePath, await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Posts the form that asks for a reset link and returns the answer itself.</summary>
    private static async Task<HttpResponseMessage> SendResetRequestAsync(HttpClient client, string email) =>
        await client.SendFormAsync(
            "/forgot-password",
            "forgot-password",
            Browser.AntiforgeryTokenIn(await client.PageAsync("/forgot-password")),
            ("Email", email));

    /// <summary>
    /// Posts the form that turns two-factor authentication on, with the key that the page shows
    /// and a code that an app makes from it, or one that it does not, and returns the answer itself.
    /// </summary>
    private static async Task<HttpResponseMessage> SendTurnOnAsync(
        HttpClient client, TestClock clock, string password, bool wrongCode = false)
    {
        var page = await client.PageAsync(Browser.TwoFactorPage);
        var app = AuthenticatorApp.WithKeyOn(page);

        return await client.SendFormAsync(
            Browser.TwoFactorPage,
            "turn-on",
            Browser.AntiforgeryTokenIn(page),
            ("SealedKey", Browser.SealedKeyIn(page)),
            ("Code", wrongCode ? app.WrongCodeAt(clock.GetUtcNow()) : app.CodeAt(clock.GetUtcNow())),
            ("Password", password));
    }
}
