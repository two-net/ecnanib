using System.Net;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Tests.Audit;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Identity;

/// <summary>
/// FR-AUTH-05's check for sensitive actions, through its contract. That's the path a circuit takes,
/// where no HTTP limiter reaches, so the throttle behind it is what's tested here.
/// </summary>
public sealed class TwoFactorCheckTests(SharedTwoFactorApp app) : IClassFixture<SharedTwoFactorApp>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private ITwoFactor TwoFactor => app.Services.GetRequiredService<ITwoFactor>();

    [Fact]
    public async Task Without_two_factor_nothing_is_required()
    {
        using var visitor = Visitor.To(app);
        var userId = await UserIdAsync(await visitor.SignUpAsync());

        Assert.False(await TwoFactor.IsEnabledAsync(userId, Cancellation));
        Assert.Equal(TwoFactorCheck.NotRequired, await TwoFactor.CheckAsync(userId, null, Cancellation));
        Assert.True(TwoFactorCheck.NotRequired.Allows());
    }

    [Fact]
    public async Task A_pending_setup_requires_nothing_yet()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();

        await TwoFactorSteps.StartAsync(visitor);

        Assert.Equal(TwoFactorCheck.NotRequired, await TwoFactor.CheckAsync(await UserIdAsync(email), null, Cancellation));
    }

    [Fact]
    public async Task A_right_code_passes_once()
    {
        var (account, userId) = await AccountAsync();
        var code = app.Clock.NextCode(account.Key);

        Assert.True(await TwoFactor.IsEnabledAsync(userId, Cancellation));
        Assert.Equal(TwoFactorCheck.Passed, await TwoFactor.CheckAsync(userId, code, Cancellation));
        Assert.Equal(TwoFactorCheck.Refused, await TwoFactor.CheckAsync(userId, code, Cancellation));
        Assert.True(TwoFactorCheck.Passed.Allows());
        Assert.False(TwoFactorCheck.Refused.Allows());
        Assert.False(TwoFactorCheck.Throttled.Allows());
    }

    [Fact]
    public async Task Checks_at_once_with_one_code_pass_once()
    {
        var (account, userId) = await AccountAsync();
        var code = app.Clock.NextCode(account.Key);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => TwoFactor.CheckAsync(userId, code, Cancellation)));

        Assert.Single(results, result => result == TwoFactorCheck.Passed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("not a code")]
    public async Task Something_that_isnt_a_code_is_refused(string? code)
    {
        var (_, userId) = await AccountAsync();

        Assert.Equal(TwoFactorCheck.Refused, await TwoFactor.CheckAsync(userId, code, Cancellation));
    }

    [Fact]
    public async Task A_code_for_another_account_is_refused()
    {
        var (mine, _) = await AccountAsync();
        var (_, theirs) = await AccountAsync();

        Assert.Equal(TwoFactorCheck.Refused, await TwoFactor.CheckAsync(theirs, app.Clock.NextCode(mine.Key), Cancellation));
        Assert.Equal(TwoFactorCheck.Refused, await TwoFactor.CheckAsync(theirs, mine.RecoveryCodes[0], Cancellation));
    }

    /// <summary>A lock stops guessing, and recovery codes still work, which keeps the kill switch reachable.</summary>
    [Fact]
    public async Task Wrong_codes_pause_checks_and_a_recovery_code_still_works()
    {
        var (account, userId) = await AccountAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(TwoFactorCheck.Refused, await TwoFactor.CheckAsync(userId, WrongCode(account.Key), Cancellation));
        }

        Assert.Equal(TwoFactorCheck.Throttled, await TwoFactor.CheckAsync(userId, app.Clock.NextCode(account.Key), Cancellation));
        Assert.Equal(TwoFactorCheck.Passed, await TwoFactor.CheckAsync(userId, account.RecoveryCodes[0], Cancellation));

        // The recovery code cleared the lock.
        Assert.Equal(TwoFactorCheck.Passed, await TwoFactor.CheckAsync(userId, app.Clock.NextCode(account.Key), Cancellation));
    }

    /// <summary>Strangers can trip the sign-in lockout with wrong passwords, and that mustn't keep the owner from the kill switch.</summary>
    [Fact]
    public async Task The_sign_in_lockout_doesnt_pause_checks()
    {
        var (account, userId) = await AccountAsync();
        var accounts = app.Services.GetRequiredService<IAccounts>();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await accounts.SignInAsync(account.Email, "not the password at all", Visitor.AnyClient, Cancellation);
        }

        Assert.Equal(SignInOutcome.Refused, (await accounts.SignInAsync(account.Email, Visitor.Password, Visitor.AnyClient, Cancellation)).Outcome);
        Assert.Equal(TwoFactorCheck.Passed, await TwoFactor.CheckAsync(userId, app.Clock.NextCode(account.Key), Cancellation));
    }

    [Fact]
    public async Task A_recovery_code_passes_once_whatever_its_case_and_is_audited()
    {
        var (account, userId) = await AccountAsync();
        var code = account.RecoveryCodes[3];

        Assert.Equal(TwoFactorCheck.Passed, await TwoFactor.CheckAsync(userId, $" {code.ToUpperInvariant().Replace("-", " ", StringComparison.Ordinal)} ", Cancellation));
        Assert.Equal(TwoFactorCheck.Refused, await TwoFactor.CheckAsync(userId, code, Cancellation));
        Assert.Equal(new TwoFactorStatus(TwoFactorState.On, 9), await TwoFactor.GetStatusAsync(userId, Cancellation));
        Assert.Single(await AuditRows.OfUserAsync(app.ConnectionString, userId, Cancellation), row => row.Action == "RecoveryCodeUsed");
    }

    [Fact]
    public async Task Turning_off_needs_a_code_and_removes_the_key_and_the_codes()
    {
        var (account, userId) = await AccountAsync();
        var secretId = await TwoFactorRows.SecretIdAsync(app.ConnectionString, account.Email, Cancellation);

        Assert.Equal(TwoFactorCheck.Refused, await TwoFactor.DisableAsync(userId, WrongCode(account.Key), Cancellation));
        Assert.True(await TwoFactor.IsEnabledAsync(userId, Cancellation));

        Assert.Equal(TwoFactorCheck.Passed, await TwoFactor.DisableAsync(userId, app.Clock.NextCode(account.Key), Cancellation));

        Assert.False(await TwoFactor.IsEnabledAsync(userId, Cancellation));
        Assert.Equal(0, await TwoFactorRows.SecretsWithIdAsync(app.ConnectionString, secretId, Cancellation));
        Assert.Equal(0, await TwoFactorRows.RecoveryCodesAsync(app.ConnectionString, account.Email, Cancellation));
        Assert.Equal(0, await TwoFactorRows.AuthenticatorsAsync(app.ConnectionString, account.Email, Cancellation));
        Assert.Single(await AuditRows.OfUserAsync(app.ConnectionString, userId, Cancellation), row => row.Action == "TwoFactorDisabled");
        Assert.Equal(TwoFactorCheck.NotRequired, await TwoFactor.DisableAsync(userId, null, Cancellation));

        var signIn = await app.Services.GetRequiredService<IAccounts>().SignInAsync(account.Email, Visitor.Password, Visitor.AnyClient, Cancellation);

        Assert.Equal(SignInOutcome.SignedIn, signIn.Outcome);
    }

    [Fact]
    public async Task New_recovery_codes_need_a_code_and_replace_the_old_ones()
    {
        var (account, userId) = await AccountAsync();

        var refused = await TwoFactor.RegenerateRecoveryCodesAsync(userId, WrongCode(account.Key), Cancellation);

        Assert.Equal(TwoFactorCheck.Refused, refused.Check);
        Assert.Null(refused.Codes);

        var replaced = await TwoFactor.RegenerateRecoveryCodesAsync(userId, app.Clock.NextCode(account.Key), Cancellation);

        Assert.Equal(TwoFactorCheck.Passed, replaced.Check);
        Assert.Equal(10, replaced.Codes!.Codes.Count);
        Assert.DoesNotContain(replaced.Codes.Codes[0], replaced.Codes.ToString(), StringComparison.Ordinal);
        Assert.Equal(TwoFactorCheck.Refused, await TwoFactor.CheckAsync(userId, account.RecoveryCodes[0], Cancellation));
        Assert.Equal(TwoFactorCheck.Passed, await TwoFactor.CheckAsync(userId, replaced.Codes.Codes[0], Cancellation));
        Assert.Single(await AuditRows.OfUserAsync(app.ConnectionString, userId, Cancellation), row => row.Action == "RecoveryCodesRegenerated");
    }

    [Fact]
    public async Task The_page_turns_two_factor_off()
    {
        using var visitor = Visitor.To(app);
        var account = await TwoFactorSteps.SignUpWithTwoFactorAsync(visitor, app.Clock);

        using var wrong = await visitor.SubmitAsync(Visitor.TwoFactorPage, Visitor.DisableTwoFactorForm, ("Input.TwoFactorCode", WrongCode(account.Key)));

        Assert.Contains("That code didn&#x27;t work", await wrong.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);

        using var right = await visitor.SubmitAsync(
            Visitor.TwoFactorPage,
            Visitor.DisableTwoFactorForm,
            ("Input.TwoFactorCode", app.Clock.NextCode(account.Key)));

        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Contains("Two-factor authentication is off.", await right.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
        Assert.False(await TwoFactor.IsEnabledAsync(await UserIdAsync(account.Email), Cancellation));
    }

    [Fact]
    public async Task The_page_replaces_the_recovery_codes()
    {
        using var visitor = Visitor.To(app);
        var account = await TwoFactorSteps.SignUpWithTwoFactorAsync(visitor, app.Clock);

        using var response = await visitor.SubmitAsync(
            Visitor.TwoFactorPage,
            Visitor.RegenerateCodesForm,
            ("Input.TwoFactorCode", app.Clock.NextCode(account.Key)));
        var codes = TwoFactorSteps.RecoveryCodesIn(await response.Content.ReadAsStringAsync(Cancellation));

        Assert.Equal(10, codes.Count);
        Assert.Empty(codes.Intersect(account.RecoveryCodes));
    }

    private async Task<(TwoFactorAccount Account, Guid UserId)> AccountAsync()
    {
        using var visitor = Visitor.To(app);
        var account = await TwoFactorSteps.SignUpWithTwoFactorAsync(visitor, app.Clock);

        return (account, await UserIdAsync(account.Email));
    }

    private Task<Guid> UserIdAsync(string email) => AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

    /// <summary>A code that's wrong for every step in the window.</summary>
    private string WrongCode(string key)
    {
        var now = app.Clock.GetUtcNow();
        var window = new[] { -30, 0, 30, 60 }.Select(seconds => Authenticator.Code(key, now.AddSeconds(seconds))).ToHashSet();

        return Enumerable.Range(0, 10).Select(digit => new string((char)('0' + digit), 6)).First(code => !window.Contains(code));
    }
}
