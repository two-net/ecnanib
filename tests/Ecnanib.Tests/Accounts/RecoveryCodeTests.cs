using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// A user who has lost their authenticator app signs in with a recovery code in its place
/// (FR-ACC-3). There are ten, each works once, and the database keeps only their hashes.
/// </summary>
public class RecoveryCodeTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string WrongCode = "That code is not right.";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Turning_two_factor_on_shows_ten_recovery_codes_once()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var (_, recoveryCodes) = await client.TurnOnTwoFactorAsync(clock);

        // Ten, all different: sixteen letters and digits each, in groups of four.
        Assert.Equal(10, recoveryCodes.Distinct().Count());
        Assert.All(recoveryCodes, code => Assert.Matches("^[A-Z2-7]{4}(-[A-Z2-7]{4}){3}$", code));

        // No page shows them again.
        foreach (var path in new[] { "/account", Browser.TwoFactorPage, "/account/two-factor/recovery-codes" })
        {
            var page = await client.PageAsync(path);

            Assert.Empty(Browser.RecoveryCodesIn(page));
            Assert.All(recoveryCodes, code => Assert.DoesNotContain(code, page));
        }
    }

    [Fact]
    public async Task The_database_holds_only_the_hashes_of_the_recovery_codes()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);

        var (user, _, recoveryCodes) = await application.RegisterWithTwoFactorAsync(Ada, clock);

        // A row for each code, with the SHA-256 hash of its letters and digits.
        var stored = StoredRecoveryCode.All(application.Database);
        Assert.Equal(10, stored.Count);
        Assert.All(stored, code => Assert.Equal(user.Id, code.UserId));
        Assert.All(stored, code => Assert.Equal(Morning, code.CreatedAt));
        Assert.Equal(
            recoveryCodes.Select(code => Convert.ToHexString(StoredRecoveryCode.HashOf(code))).Order(),
            stored.Select(code => Convert.ToHexString(code.CodeHash)).Order());

        // Nothing in the database holds a code itself, so reading it gives nobody a way in.
        var everything = application.EverythingStored();
        Assert.All(recoveryCodes, code =>
        {
            Assert.DoesNotContain(code, everything);
            Assert.DoesNotContain(code.Replace("-", string.Empty), everything);
        });
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once_in_place_of_a_code_from_the_app()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (user, _, recoveryCodes) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();

        await client.SignInAsync(Ada);
        var (path, page) = await client.EnterCodeAsync(recoveryCodes[0]);

        // Signed in, and the code is spent: its row is gone, and the pages count one less.
        Assert.Equal("/", path);
        Assert.Contains("Sign out", page);
        Assert.Equal(9, StoredRecoveryCode.All(application.Database).Count);
        Assert.DoesNotContain(
            StoredRecoveryCode.All(application.Database),
            code => code.CodeHash.SequenceEqual(StoredRecoveryCode.HashOf(recoveryCodes[0])));
        Assert.Contains("You have 9 of 10 recovery codes left.", await client.PageAsync("/account"));
        Assert.Contains("You have 9 of 10 recovery codes left.", await client.PageAsync(Browser.TwoFactorPage));

        var used = Assert.Single(application.Events(), written => written.Template == "User {UserId} used a recovery code");
        Assert.Equal(user.Id.ToString(), used.Text("UserId"));

        // A second time it is a wrong code, and is counted as one.
        await client.SignOutAsync();
        await client.SignInAsync(Ada);
        var (again, refused) = await client.EnterCodeAsync(recoveryCodes[0]);

        Assert.Equal(SessionAuthentication.SignInCodePath, again);
        Assert.Contains(WrongCode, refused);
        Assert.Equal(1, StoredTwoFactor.Of(application.Database, Ada).FailedCodes);

        // Another one still works, and the count of wrong codes starts again.
        Assert.Equal("/", (await client.EnterCodeAsync(recoveryCodes[1])).Path);
        Assert.Equal(8, StoredRecoveryCode.All(application.Database).Count);
        Assert.Equal(0, StoredTwoFactor.Of(application.Database, Ada).FailedCodes);
    }

    [Theory]
    [InlineData("lower")]
    [InlineData("joined")]
    [InlineData("spaced")]
    public async Task A_recovery_code_is_the_same_however_it_is_typed(string typed)
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, _, recoveryCodes) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        var code = typed switch
        {
            "lower" => recoveryCodes[0].ToLowerInvariant(),
            "joined" => recoveryCodes[0].Replace("-", string.Empty),
            _ => $"  {recoveryCodes[0].Replace("-", " ")}  ",
        };

        Assert.Equal("/", (await client.EnterCodeAsync(code)).Path);
        Assert.Equal(9, StoredRecoveryCode.All(application.Database).Count);
    }

    [Fact]
    public async Task The_same_recovery_code_sent_twice_at_the_same_time_signs_in_once()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, _, recoveryCodes) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var one = application.CreateClient();
        using var other = application.CreateClient();

        await one.SignInAsync(Ada);
        await other.SignInAsync(Ada);
        var formOfOne = Browser.AntiforgeryTokenIn(await one.PageAsync(SessionAuthentication.SignInCodePath));
        var formOfOther = Browser.AntiforgeryTokenIn(await other.PageAsync(SessionAuthentication.SignInCodePath));

        var answers = await Task.WhenAll(
            one.PostAsync(SessionAuthentication.SignInCodePath, "sign-in-code", formOfOne, ("Code", recoveryCodes[0])),
            other.PostAsync(SessionAuthentication.SignInCodePath, "sign-in-code", formOfOther, ("Code", recoveryCodes[0])));

        // One of them is signed in, and the other finds the code spent.
        Assert.Single(answers, answer => answer.Path == "/");
        Assert.Single(answers, answer => answer.Page.Contains(WrongCode));
        Assert.Single(StoredSession.All(application.Database));
        Assert.Equal(9, StoredRecoveryCode.All(application.Database).Count);
        Assert.Single(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "TwoFactorRecoveryCodeUsed");
    }

    [Fact]
    public async Task A_recovery_code_of_one_account_signs_nobody_in_to_another()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, _, hers) = await application.RegisterWithTwoFactorAsync(Grace, clock);
        await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        var (path, page) = await client.EnterCodeAsync(hers[0]);

        Assert.Equal(SessionAuthentication.SignInCodePath, path);
        Assert.Contains(WrongCode, page);
        Assert.Empty(StoredSession.All(application.Database));

        // The code is still its owner's.
        Assert.Equal(20, StoredRecoveryCode.All(application.Database).Count);
    }

    [Fact]
    public async Task Wrong_recovery_codes_are_counted_as_wrong_codes_from_the_app_are()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, recoveryCodes) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        // Guesses of both kinds, five in a row.
        foreach (var guess in new[] { "AAAA-BBBB-CCCC-DDDD", app.WrongCodeAt(clock.GetUtcNow()), "2222-3333-4444-5555", "ZZZZ-YYYY-XXXX-WWWW" })
        {
            Assert.Contains(WrongCode, (await client.EnterCodeAsync(guess)).Page);
        }

        Assert.Contains("Too many wrong codes were entered.", (await client.EnterCodeAsync("QQQQ-RRRR-SSSS-TTTT")).Page);

        // Now a recovery code is refused like any other code, and is not spent by it.
        Assert.Contains("Too many wrong codes were entered.", (await client.EnterCodeAsync(recoveryCodes[0])).Page);
        Assert.Empty(StoredSession.All(application.Database));
        Assert.Equal(10, StoredRecoveryCode.All(application.Database).Count);
    }

    [Fact]
    public async Task New_recovery_codes_take_a_code_and_replace_the_earlier_ones()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (user, app, earlier) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);
        await client.EnterCodeAsync(earlier[0]);

        // The page that says it is on leads to the form.
        Assert.Contains("href=\"/account/two-factor/recovery-codes\"", await client.PageAsync(Browser.TwoFactorPage));

        // A wrong code replaces nothing.
        var (_, refused) = await client.SubmitAsync(
            "/account/two-factor/recovery-codes", "new-recovery-codes", ("Code", app.WrongCodeAt(clock.GetUtcNow())));
        Assert.Contains(WrongCode, refused);
        Assert.Empty(Browser.RecoveryCodesIn(refused));
        Assert.Equal(9, StoredRecoveryCode.All(application.Database).Count);

        // A right one makes ten new ones, and the answer shows them.
        clock.Set(Morning.AddMinutes(30));
        var (path, page) = await client.SubmitAsync(
            "/account/two-factor/recovery-codes", "new-recovery-codes", ("Code", app.CodeAt(clock.GetUtcNow())));
        var replaced = Browser.RecoveryCodesIn(page);

        Assert.Equal("/account/two-factor/recovery-codes", path);
        Assert.Contains("Your recovery codes were replaced.", page);
        Assert.Equal(10, replaced.Distinct().Count());
        Assert.Empty(replaced.Intersect(earlier));
        Assert.DoesNotContain("name=\"Code\"", page);

        var stored = StoredRecoveryCode.All(application.Database);
        Assert.Equal(
            replaced.Select(code => Convert.ToHexString(StoredRecoveryCode.HashOf(code))).Order(),
            stored.Select(code => Convert.ToHexString(code.CodeHash)).Order());
        Assert.All(stored, code => Assert.Equal(Morning.AddMinutes(30), code.CreatedAt));
        Assert.Contains("You have 10 of 10 recovery codes left.", await client.PageAsync(Browser.TwoFactorPage));

        var logged = Assert.Single(application.Events(), written => written.Template == "User {UserId} replaced their recovery codes");
        Assert.Equal(user.Id.ToString(), logged.Text("UserId"));

        // An earlier one, used or not, no longer signs in. A new one does.
        await client.SignOutAsync();
        await client.SignInAsync(Ada);
        Assert.Contains(WrongCode, (await client.EnterCodeAsync(earlier[0])).Page);
        Assert.Contains(WrongCode, (await client.EnterCodeAsync(earlier[1])).Page);
        Assert.Equal("/", (await client.EnterCodeAsync(replaced[0])).Path);
    }

    [Fact]
    public async Task New_recovery_codes_can_be_made_with_a_recovery_code()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, _, earlier) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);
        await client.EnterCodeAsync(earlier[0]);

        // Someone without their app, who is down to the last codes.
        var (_, page) = await client.SubmitAsync(
            "/account/two-factor/recovery-codes", "new-recovery-codes", ("Code", earlier[1]));

        Assert.Equal(10, Browser.RecoveryCodesIn(page).Count);
        Assert.Equal(10, StoredRecoveryCode.All(application.Database).Count);
    }
}
