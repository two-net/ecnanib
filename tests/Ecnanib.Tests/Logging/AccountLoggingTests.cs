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
        (await visitor.SignOutAsync()).Dispose();

        app.Logger("Ecnanib.Tests.Accounts").LogError("Logged by mistake: {Value}", SecretCanary);
        await app.StopAsync();

        var text = app.FileText();

        Assert.Contains("Signed in", text, StringComparison.Ordinal);
        Assert.Contains("Confirmed an administrator", text, StringComparison.Ordinal);
        Assert.DoesNotContain(PasswordCanary, text, StringComparison.Ordinal);
        Assert.DoesNotContain(WrongCanary, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretCanary, text, StringComparison.Ordinal);
        Assert.DoesNotContain(email, text, StringComparison.Ordinal);
        Assert.DoesNotContain("$argon2id$", text, StringComparison.Ordinal);
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
}
