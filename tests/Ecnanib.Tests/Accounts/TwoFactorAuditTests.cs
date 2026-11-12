using System.Net;
using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// Two-factor changes are recorded in the audit trail (FR-ACC-3, NFR-LOG-7): in the database, and
/// apart from the logs. Each event stores the user, the action, the target, the time, and the
/// origin, and no secret.
/// </summary>
public class TwoFactorAuditTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    [ShowsAuditOf(AuditAction.TwoFactorTurnedOn)]
    public async Task Turning_two_factor_on_is_recorded_with_the_user_the_action_the_target_the_time_and_the_origin()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, host: host => host.From("203.0.113.7"));
        await application.RegisterVerifiedAsync(Grace);

        var (user, _, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);

        var recorded = Assert.Single(StoredAuditEvent.OfTwoFactor(application.Database));
        Assert.Equal(user.Id, recorded.UserId);
        Assert.Equal("TwoFactorTurnedOn", recorded.Action);
        Assert.Equal($"user:{user.Id}", recorded.Target);
        Assert.Equal(Morning, recorded.OccurredAt);
        Assert.Equal("203.0.113.7", recorded.Origin);

        // The id is one the application assigned, as the ids of its other rows are.
        Assert.Equal(7, recorded.Id.Version);
    }

    [Fact]
    [ShowsAuditOf(AuditAction.TwoFactorTurnedOff)]
    public async Task Turning_it_off_is_recorded()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, host: host => host.From("203.0.113.7"));
        var (user, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);
        await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));

        clock.Set(Morning.AddMinutes(30));
        await client.SubmitAsync("/account/two-factor/turn-off", "turn-off", ("Code", app.CodeAt(clock.GetUtcNow())));

        var events = StoredAuditEvent.OfTwoFactor(application.Database);
        Assert.Equal(["TwoFactorTurnedOn", "TwoFactorTurnedOff"], events.Select(recorded => recorded.Action));

        var turnedOff = events[^1];
        Assert.Equal(user.Id, turnedOff.UserId);
        Assert.Equal($"user:{user.Id}", turnedOff.Target);
        Assert.Equal(Morning.AddMinutes(30), turnedOff.OccurredAt);
        Assert.Equal("203.0.113.7", turnedOff.Origin);
    }

    [Fact]
    [ShowsAuditOf(AuditAction.TwoFactorRecoveryCodesReplaced)]
    public async Task Making_new_recovery_codes_is_recorded()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (user, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);
        await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));

        clock.Set(Morning.AddMinutes(30));
        await client.SubmitAsync(
            "/account/two-factor/recovery-codes", "new-recovery-codes", ("Code", app.CodeAt(clock.GetUtcNow())));

        var events = StoredAuditEvent.OfTwoFactor(application.Database);
        Assert.Equal(["TwoFactorTurnedOn", "TwoFactorRecoveryCodesReplaced"], events.Select(recorded => recorded.Action));
        Assert.Equal(user.Id, events[^1].UserId);
        Assert.Equal(Morning.AddMinutes(30), events[^1].OccurredAt);
    }

    [Fact]
    [ShowsAuditOf(AuditAction.TwoFactorRecoveryCodeUsed)]
    public async Task Signing_in_with_a_recovery_code_is_recorded()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (user, app, recoveryCodes) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();

        // A code from the app changes nothing about two-factor authentication. It is recorded
        // as the sign-in it is, and as nothing else.
        await client.SignInAsync(Ada);
        await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));
        await client.SignOutAsync();
        Assert.Single(StoredAuditEvent.OfTwoFactor(application.Database));

        // A recovery code is spent by it.
        clock.Set(Morning.AddMinutes(30));
        await client.SignInAsync(Ada);
        Assert.Equal("/", (await client.EnterCodeAsync(recoveryCodes[3])).Path);

        var events = StoredAuditEvent.OfTwoFactor(application.Database);
        Assert.Equal(["TwoFactorTurnedOn", "TwoFactorRecoveryCodeUsed"], events.Select(recorded => recorded.Action));
        Assert.Equal(user.Id, events[^1].UserId);
        Assert.Equal($"user:{user.Id}", events[^1].Target);
        Assert.Equal(Morning.AddMinutes(30), events[^1].OccurredAt);
    }

    [Fact]
    public async Task What_changed_nothing_is_not_recorded()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // Opening the page, a wrong code, and a wrong password turn nothing on.
        var page = await client.PageAsync(Browser.TwoFactorPage);
        var app = AuthenticatorApp.WithKeyOn(page);
        foreach (var (code, password) in new[]
        {
            (app.WrongCodeAt(Morning), AccountsApplication.Password),
            (app.CodeAt(Morning), "not the password at all"),
        })
        {
            await client.PostAsync(
                Browser.TwoFactorPage,
                "turn-on",
                Browser.AntiforgeryTokenIn(page),
                ("SealedKey", Browser.SealedKeyIn(page)),
                ("Code", code),
                ("Password", password));
        }

        Assert.Empty(StoredAuditEvent.OfTwoFactor(application.Database));

        // Once it is on, a wrong code changes nothing either: not to turn it off, not for new
        // recovery codes, and not to sign in.
        (app, _) = await client.TurnOnTwoFactorAsync(clock);
        clock.Set(Morning.AddMinutes(30));
        var wrong = app.WrongCodeAt(clock.GetUtcNow());

        await client.SubmitAsync("/account/two-factor/turn-off", "turn-off", ("Code", wrong));
        await client.SubmitAsync("/account/two-factor/recovery-codes", "new-recovery-codes", ("Code", wrong));
        await client.SignOutAsync();
        await client.SignInAsync(Ada);
        await client.EnterCodeAsync(wrong);
        await client.EnterCodeAsync("ABCD-EFGH-IJKL-MNOP");

        Assert.Equal("TwoFactorTurnedOn", Assert.Single(StoredAuditEvent.OfTwoFactor(application.Database)).Action);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")] // An IPv4 address that arrived on an IPv6 socket.
    [InlineData("2001:db8::7", "2001:db8::7")]
    [InlineData(null, null)] // The connection does not know, as under the server of the tests.
    public async Task The_origin_is_the_address_the_request_came_from(string? address, string? origin)
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(
            clock: clock, host: host => { if (address is not null) { host.From(address); } });

        await application.RegisterWithTwoFactorAsync(Ada, clock);

        Assert.Equal(origin, Assert.Single(StoredAuditEvent.OfTwoFactor(application.Database)).Origin);
    }

    [Fact]
    public async Task An_event_holds_no_key_no_code_and_no_recovery_code()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, recoveryCodes) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();

        // Every kind of change: a recovery code used, new recovery codes, and turned off.
        await client.SignInAsync(Ada);
        await client.EnterCodeAsync(recoveryCodes[0]);
        var replacing = app.CodeAt(clock.GetUtcNow());
        var (_, replaced) = await client.SubmitAsync(
            "/account/two-factor/recovery-codes", "new-recovery-codes", ("Code", replacing));
        clock.Set(clock.GetUtcNow().AddMinutes(1));
        var turningOff = app.CodeAt(clock.GetUtcNow());
        await client.SubmitAsync("/account/two-factor/turn-off", "turn-off", ("Code", turningOff));

        Assert.Equal(4, StoredAuditEvent.OfTwoFactor(application.Database).Count);

        // The rows, as the database hands them to whoever reads them.
        var events = string.Join(
            Environment.NewLine,
            application.Database.Column("SELECT row_to_json(stored)::text FROM audit_events stored"));

        Assert.DoesNotContain(app.KeyText, events);
        Assert.DoesNotContain(Convert.ToHexString(app.Key), events, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"\"{replacing}\"", events);
        Assert.DoesNotContain($"\"{turningOff}\"", events);
        Assert.All(recoveryCodes.Concat(Browser.RecoveryCodesIn(replaced)), code => Assert.DoesNotContain(code, events));

        // Nor the address of the account: the event names it by its id.
        Assert.DoesNotContain("example.com", events);
    }

    [Fact]
    public async Task The_audit_trail_is_in_the_database_and_the_log_is_not_it()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, host: host => host.From("203.0.113.7"));

        await application.RegisterWithTwoFactorAsync(Ada, clock);

        // The change is in the table. The log tells of it in its own words, without the origin.
        Assert.Single(StoredAuditEvent.OfTwoFactor(application.Database));
        Assert.Contains("turned two-factor authentication on", application.LogText());
        Assert.DoesNotContain("TwoFactorTurnedOn", application.LogText());
        Assert.DoesNotContain("203.0.113.7", application.LogText());
    }

    [Fact]
    public async Task The_events_of_an_account_are_deleted_with_it()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, host: host => host.From("203.0.113.7"));
        await application.RegisterWithTwoFactorAsync(Ada, clock);
        var (grace, _, _) = await application.RegisterWithTwoFactorAsync(Grace, clock);

        // An origin is where a user was, which is personal data: it does not outlast the account.
        application.Database.Execute($"DELETE FROM users WHERE email = '{Ada}'");

        Assert.Equal(grace.Id, Assert.Single(StoredAuditEvent.OfTwoFactor(application.Database)).UserId);
        Assert.All(StoredAuditEvent.All(application.Database), recorded => Assert.Equal(grace.Id, recorded.UserId));
        Assert.All(StoredRecoveryCode.All(application.Database), code => Assert.Equal(grace.Id, code.UserId));
    }
}

/// <summary>Where the requests of a test come from.</summary>
internal static class Origin
{
    /// <summary>Makes every request arrive from an address, as a connection from that address does.</summary>
    public static void From(this Microsoft.AspNetCore.Hosting.IWebHostBuilder host, string address) =>
        host.PrepareEachRequest(request => request.Connection.RemoteIpAddress = IPAddress.Parse(address));
}
