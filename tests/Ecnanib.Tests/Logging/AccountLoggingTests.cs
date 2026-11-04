using Ecnanib.Tests.Email;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging;

/// <summary>
/// AC-03 and FR-LOG-05, through the real forms with logging as loud as it goes. An address isn't a secret
/// that the rules mask, so keeping it out relies on nothing logging it, which is what this proves.
/// </summary>
public sealed class AccountLoggingTests(PostgresFixture postgres)
{
    private const string PasswordCanary = "canary-password-1234";
    private const string WrongCanary = "canary-wrong-password-5678";
    private const string SecretCanary = "canary bootstrap secret of plenty of characters";
    private const string NewPasswordCanary = "canary-new-password-9012";

    [Fact]
    public async Task Passwords_addresses_and_the_secret_stay_out_of_the_logs()
    {
        var email = Visitor.NewEmail();

        await using var app = new LoggedApp(
            postgres.NewConnectionString(),
            AccountApp.Settings(new()
            {
                ["Logging:MinimumLevel"] = "Verbose",
                ["Logging:Overrides:Microsoft.AspNetCore"] = "Verbose",
                ["Logging:Overrides:Microsoft.EntityFrameworkCore.Database.Command"] = "Verbose",
                ["Logging:Sinks:Database:Enabled"] = "false",
                ["Admin:AdminEmails:0"] = email,
                ["Admin:BootstrapSecret"] = SecretCanary,
            }));
        using var visitor = Visitor.To(app.App);

        (await visitor.RegisterAsync(email, PasswordCanary)).Dispose();
        (await visitor.SignInAsync(email, WrongCanary)).Dispose();
        (await visitor.SignInAsync(email, PasswordCanary)).Dispose();
        (await visitor.SubmitAsync("/account/administrator", Visitor.ConfirmForm, ("Input.Secret", WrongCanary))).Dispose();
        (await visitor.SubmitAsync("/account/administrator", Visitor.ConfirmForm, ("Input.Secret", SecretCanary))).Dispose();
        (await visitor.ChangePasswordAsync(WrongCanary, NewPasswordCanary)).Dispose();
        (await visitor.ChangePasswordAsync(PasswordCanary, NewPasswordCanary)).Dispose();
        (await visitor.SignOutAsync()).Dispose();

        app.Logger("Ecnanib.Tests.Accounts").LogError("Logged by mistake: {Value}", SecretCanary);
        await app.StopAsync();

        var text = app.FileText();

        Assert.Contains("Signed in", text, StringComparison.Ordinal);
        Assert.Contains("Confirmed an administrator", text, StringComparison.Ordinal);
        Assert.Contains("Password changed", text, StringComparison.Ordinal);
        Assert.Contains("Password change refused", text, StringComparison.Ordinal);
        Assert.Contains("Signed out", text, StringComparison.Ordinal);
        Assert.DoesNotContain(PasswordCanary, text, StringComparison.Ordinal);
        Assert.DoesNotContain(NewPasswordCanary, text, StringComparison.Ordinal);
        Assert.DoesNotContain(WrongCanary, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretCanary, text, StringComparison.Ordinal);
        Assert.DoesNotContain(email, text, StringComparison.Ordinal);
        Assert.DoesNotContain("$argon2id$", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A key, a code and a recovery code are each as good as an authenticator while they last, and none
    /// of them has a marker the masking could rely on in free text, so nothing may log them.
    /// </summary>
    [Fact]
    public async Task Keys_and_codes_stay_out_of_the_logs()
    {
        var clock = new TestClock();

        await using var app = new LoggedApp(
            postgres.NewConnectionString(),
            AccountApp.Settings(new()
            {
                ["Logging:MinimumLevel"] = "Verbose",
                ["Logging:Overrides:Microsoft.AspNetCore"] = "Verbose",
                ["Logging:Overrides:Microsoft.EntityFrameworkCore.Database.Command"] = "Verbose",
                ["Logging:Sinks:Database:Enabled"] = "false",
            }),
            TwoFactorSteps.UseClock(clock));
        using var visitor = Visitor.To(app.App);
        using var later = Visitor.To(app.App);

        var account = await TwoFactorSteps.SignUpWithTwoFactorAsync(visitor, clock);
        var signInCode = clock.NextCode(account.Key);
        (await TwoFactorSteps.SignInAsync(later, account.Email, signInCode)).Dispose();
        var recoveryCode = account.RecoveryCodes[0];
        (await later.SubmitAsync(Visitor.TwoFactorPage, Visitor.RegenerateCodesForm, ("Input.TwoFactorCode", recoveryCode))).Dispose();
        var disableCode = clock.NextCode(account.Key);
        (await later.SubmitAsync(Visitor.TwoFactorPage, Visitor.DisableTwoFactorForm, ("Input.TwoFactorCode", disableCode))).Dispose();

        await app.StopAsync();

        var text = app.FileText();

        Assert.Contains("Turned two-factor on", text, StringComparison.Ordinal);
        Assert.Contains("Signed in with two-factor", text, StringComparison.Ordinal);
        Assert.Contains("Used a recovery code", text, StringComparison.Ordinal);
        Assert.Contains("Turned two-factor off", text, StringComparison.Ordinal);
        Assert.DoesNotContain(account.Key, text, StringComparison.Ordinal);
        Assert.False(Standalone(text, signInCode), "The sign-in code was logged");
        Assert.False(Standalone(text, disableCode), "The code that turned two-factor off was logged");
        Assert.All(account.RecoveryCodes, code => Assert.DoesNotContain(code, text, StringComparison.Ordinal));
        Assert.DoesNotContain(recoveryCode.Replace("-", string.Empty, StringComparison.Ordinal), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sign_in_event_carries_the_user_id()
    {
        await using var app = new LoggedApp(postgres.NewConnectionString(), AccountApp.Settings());
        using var visitor = Visitor.To(app.App);

        await visitor.SignUpAsync();
        await app.StopAsync();

        var entry = Assert.Single(app.FileEvents(), candidate => candidate.GetProperty("MessageTemplate").GetString() == "Signed in");

        Assert.True(Guid.TryParse(entry.GetProperty("Properties").GetProperty("UserId").GetString(), out _));
    }

    /// <summary>At Warning, a visitor could write a database row per attempt.</summary>
    [Fact]
    public async Task A_refused_sign_in_stays_below_the_database_sink()
    {
        await using var app = new LoggedApp(postgres.NewConnectionString(), AccountApp.Settings());
        using var visitor = Visitor.To(app.App);

        (await visitor.SignInAsync(Visitor.NewEmail())).Dispose();
        await app.StopAsync();

        var entry = Assert.Single(app.FileEvents(), candidate => candidate.GetProperty("MessageTemplate").GetString()!.StartsWith("Sign-in refused", StringComparison.Ordinal));

        Assert.Equal("Information", entry.GetProperty("Level").GetString());
    }

    /// <summary>
    /// A link's token is as good as a password until it's used, and an address isn't masked, so neither
    /// may reach a log. That includes a link carried through sign-in in returnUrl, where it's URL-encoded.
    /// </summary>
    [Fact]
    public async Task Links_and_addresses_from_email_stay_out_of_the_logs()
    {
        var email = Visitor.NewEmail();
        var captured = new CapturedEmail();

        await using var app = new LoggedApp(
            postgres.NewConnectionString(),
            AccountApp.Settings(AccountApp.EmailOn(new()
            {
                ["Logging:MinimumLevel"] = "Verbose",
                ["Logging:Overrides:Microsoft.AspNetCore"] = "Verbose",
                ["Logging:Overrides:Microsoft.EntityFrameworkCore.Database.Command"] = "Verbose",
                ["Logging:Sinks:Database:Enabled"] = "false",
            })),
            captured.ReplaceSender);
        using var registrant = Visitor.To(app.App);
        using var visitor = Visitor.To(app.App);

        (await registrant.RegisterAsync(email, PasswordCanary)).Dispose();
        var verificationLink = captured.LastPathTo(email);

        using (var challenge = await visitor.GetAsync(verificationLink))
        {
            (await visitor.SignInAsync(email, PasswordCanary, page: Visitor.Target(challenge)!)).Dispose();
        }

        (await visitor.VerifyEmailAsync(verificationLink)).Dispose();
        (await visitor.ForgotPasswordAsync(email)).Dispose();
        var resetLink = captured.LastPathTo(email);
        (await visitor.ResetPasswordAsync(resetLink, NewPasswordCanary)).Dispose();
        (await visitor.GetAsync(resetLink)).Dispose();

        await app.StopAsync();

        var text = app.FileText();

        Assert.Contains("Sent a link to verify the address", text, StringComparison.Ordinal);
        Assert.Contains("Verified the address", text, StringComparison.Ordinal);
        Assert.Contains("Sent a password reset link", text, StringComparison.Ordinal);
        Assert.Contains("Reset the password through a link", text, StringComparison.Ordinal);
        Assert.DoesNotContain(CapturedEmail.TokenIn(verificationLink), text, StringComparison.Ordinal);
        Assert.DoesNotContain(CapturedEmail.TokenIn(resetLink), text, StringComparison.Ordinal);
        Assert.DoesNotContain(email, text, StringComparison.Ordinal);
        Assert.DoesNotContain(PasswordCanary, text, StringComparison.Ordinal);
        Assert.DoesNotContain(NewPasswordCanary, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether six digits appear with no digit either side. Timestamps hold runs of seven digits, so a
    /// plain search would sometimes find a code that was never logged.
    /// </summary>
    private static bool Standalone(string text, string code) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, $"(?<![0-9]){code}(?![0-9])");
}
