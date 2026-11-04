using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Tests.Audit;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Identity;

/// <summary>FR-AUTH-05 and FR-KEY-07: turning two-factor on, through the real page.</summary>
public sealed partial class TwoFactorSetupTests(SharedTwoFactorApp app) : IClassFixture<SharedTwoFactorApp>
{
    private const string Canary = "canary-password-1234";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_account_page_says_whether_two_factor_is_on()
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();

        Assert.Contains("Set up two-factor authentication", await visitor.PageAsync("/account"), StringComparison.Ordinal);

        await TwoFactorSteps.EnableAsync(visitor, await TwoFactorSteps.StartAsync(visitor), app.Clock);

        Assert.Contains("Manage two-factor authentication", await visitor.PageAsync("/account"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Starting_shows_a_key_a_QR_code_and_a_link_for_an_app()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();

        using var response = await visitor.SubmitAsync(Visitor.TwoFactorPage, Visitor.StartTwoFactorForm);
        var page = await response.Content.ReadAsStringAsync(Cancellation);
        var key = TwoFactorSteps.KeyIn(page);

        Assert.Matches("^[A-Z2-7]{32}$", key);
        Assert.Contains("<svg class=\"qr\"", page, StringComparison.Ordinal);
        Assert.Contains($"otpauth://totp/Ecnanib:{Uri.EscapeDataString(email)}?secret={key}&amp;issuer=Ecnanib", page, StringComparison.Ordinal);
    }

    /// <summary>Until a code confirms it, the authenticator might not exist on any phone, so sign-in mustn't need it.</summary>
    [Fact]
    public async Task A_setup_that_isnt_confirmed_changes_nothing_at_sign_in()
    {
        using var visitor = Visitor.To(app);
        using var later = Visitor.To(app);
        var email = await visitor.SignUpAsync();

        await TwoFactorSteps.StartAsync(visitor);
        using var response = await later.SignInAsync(email);

        Assert.Equal("/account", Visitor.Target(response));
    }

    [Fact]
    public async Task The_key_is_encrypted_at_rest()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();

        var key = await TwoFactorSteps.StartAsync(visitor);
        var secretId = await TwoFactorRows.SecretIdAsync(app.ConnectionString, email, Cancellation);

        Assert.Equal(1, await TwoFactorRows.SecretsWithIdAsync(app.ConnectionString, secretId, Cancellation));
        Assert.Equal(
            0,
            await Sql.ScalarAsync<int>(
                app.ConnectionString,
                $"SELECT position(convert_to('{key}', 'UTF8') in ciphertext) FROM secrets WHERE id = '{secretId}'",
                Cancellation));
    }

    /// <summary>The page shows a pending setup again rather than starting over, so this goes through the contract.</summary>
    [Fact]
    public async Task Starting_again_replaces_the_key_and_deletes_the_old_one()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var twoFactor = app.Services.GetRequiredService<ITwoFactor>();

        var first = await TwoFactorSteps.StartAsync(visitor);
        var firstSecret = await TwoFactorRows.SecretIdAsync(app.ConnectionString, email, Cancellation);
        var second = (await twoFactor.BeginSetupAsync(await AccountRows.IdAsync(app.ConnectionString, email, Cancellation), Cancellation))!.Key;

        Assert.NotEqual(first, second);
        Assert.Equal(second, TwoFactorSteps.KeyIn(await visitor.PageAsync(Visitor.TwoFactorPage)));
        Assert.Equal(0, await TwoFactorRows.SecretsWithIdAsync(app.ConnectionString, firstSecret, Cancellation));
        Assert.Equal(1, await TwoFactorRows.AuthenticatorsAsync(app.ConnectionString, email, Cancellation));

        // A code from the replaced key doesn't confirm the new one.
        using var response = await visitor.SubmitAsync(
            Visitor.TwoFactorPage,
            Visitor.EnableTwoFactorForm,
            ("Input.Password", Visitor.Password),
            ("Input.TwoFactorCode", app.Clock.NextCode(first)));

        Assert.Contains("That code didn&#x27;t work", await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turning_on_needs_the_password_and_a_wrong_one_counts_towards_the_lockout()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var key = await TwoFactorSteps.StartAsync(visitor);

        using var response = await visitor.SubmitAsync(
            Visitor.TwoFactorPage,
            Visitor.EnableTwoFactorForm,
            ("Input.Password", Canary),
            ("Input.TwoFactorCode", app.Clock.NextCode(key)));
        var page = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Contains("That isn&#x27;t your current password.", page, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, page, StringComparison.Ordinal);
        Assert.Equal(1, await TwoFactorRows.FailedSignInsAsync(app.ConnectionString, email, Cancellation));
        Assert.Empty(TwoFactorSteps.RecoveryCodesIn(page));
        Assert.Equal(key, TwoFactorSteps.KeyIn(page));
    }

    [Fact]
    public async Task Turning_on_needs_a_code_from_the_new_authenticator()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var key = await TwoFactorSteps.StartAsync(visitor);
        var wrong = Authenticator.Code(Authenticator.Base32Encode(new byte[20]), app.Clock.GetUtcNow());

        using var response = await visitor.SubmitAsync(
            Visitor.TwoFactorPage,
            Visitor.EnableTwoFactorForm,
            ("Input.Password", Visitor.Password),
            ("Input.TwoFactorCode", wrong));
        var page = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Contains("That code didn&#x27;t work", page, StringComparison.Ordinal);
        Assert.Equal(key, TwoFactorSteps.KeyIn(page));
        Assert.Equal(0, await TwoFactorRows.RecoveryCodesAsync(app.ConnectionString, email, Cancellation));
    }

    [Fact]
    public async Task Turning_on_shows_the_recovery_codes_once_and_keeps_only_their_hashes()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();

        var codes = await TwoFactorSteps.EnableAsync(visitor, await TwoFactorSteps.StartAsync(visitor), app.Clock);
        var later = await visitor.PageAsync(Visitor.TwoFactorPage);

        Assert.Equal(10, codes.Distinct().Count());
        Assert.Empty(TwoFactorSteps.RecoveryCodesIn(later));
        Assert.Contains("Recovery codes left", later, StringComparison.Ordinal);
        Assert.Equal(10, await TwoFactorRows.RecoveryCodesAsync(app.ConnectionString, email, Cancellation));
        Assert.Equal(
            0,
            await Sql.ScalarAsync<long>(
                app.ConnectionString,
                $"SELECT count(*) FROM recovery_codes WHERE position(convert_to('{codes[0]}', 'UTF8') in code_hash) > 0",
                Cancellation));
    }

    /// <summary>None of the other sessions passed two-factor, which sign-in now needs.</summary>
    [Fact]
    public async Task Turning_on_is_audited_and_ends_the_other_sessions()
    {
        using var here = Visitor.To(app);
        using var there = Visitor.To(app);
        var email = await here.SignUpAsync();
        (await there.SignInAsync(email)).Dispose();

        await TwoFactorSteps.EnableAsync(here, await TwoFactorSteps.StartAsync(here), app.Clock);

        using var other = await there.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Redirect, other.StatusCode);
        Assert.Contains(email, await here.PageAsync("/account"), StringComparison.Ordinal);
        Assert.Single(await SessionRows.OfAsync(app.ConnectionString, email, Cancellation));

        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
        var record = Assert.Single(await AuditRows.OfUserAsync(app.ConnectionString, userId, Cancellation), row => row.Action == "TwoFactorEnabled");

        Assert.Equal(("User", userId), (record.ActorKind, record.ActorId));
    }

    /// <summary>The framework appends encrypted state to a page, which a search of the HTML can't see into.</summary>
    [Fact]
    public async Task The_encrypted_state_on_the_page_carries_neither_key_nor_codes()
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();
        var key = await TwoFactorSteps.StartAsync(visitor);

        using var response = await visitor.SubmitAsync(
            Visitor.TwoFactorPage,
            Visitor.EnableTwoFactorForm,
            ("Input.Password", Visitor.Password),
            ("Input.TwoFactorCode", app.Clock.NextCode(key)));
        var page = await response.Content.ReadAsStringAsync(Cancellation);
        var codes = TwoFactorSteps.RecoveryCodesIn(page);

        Assert.NotEmpty(codes);

        var state = PersistedState().Match(page).Groups["state"].Value;
        var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Microsoft.AspNetCore.Components.Server.State");
        var json = Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(state)));
        var entries = JsonSerializer.Deserialize<Dictionary<string, byte[]>>(json)!;

        Assert.All(entries, entry =>
        {
            var text = Encoding.UTF8.GetString(entry.Value);

            Assert.DoesNotContain(key, text, StringComparison.Ordinal);
            Assert.All(codes, code => Assert.DoesNotContain(code, text, StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task The_page_stays_static()
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();

        Assert.DoesNotContain("<!--Blazor:", await visitor.PageAsync(Visitor.TwoFactorPage), StringComparison.Ordinal);
    }

    /// <summary>The page is private, so CsrfTests' visitor never reaches its antiforgery check. A user does.</summary>
    [Fact]
    public async Task A_post_without_a_token_is_refused()
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();

        using var form = new FormUrlEncodedContent([new("_handler", Visitor.StartTwoFactorForm)]);
        using var response = await visitor.Client.PostAsync(Visitor.TwoFactorPage, form, Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [GeneratedRegex("<!--Blazor-Server-Component-State:(?<state>[^-]+)-->")]
    private static partial Regex PersistedState();
}
