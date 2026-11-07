using System.Net;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Tests.Audit;
using Ecnanib.Tests.Email;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Identity;

/// <summary>FR-AUTH-05 at sign-in: a right password starts no session until a code follows it.</summary>
public sealed class TwoFactorSignInTests(SharedTwoFactorApp app, PostgresFixture postgres) : IClassFixture<SharedTwoFactorApp>
{
    private const string ChallengeCookie = "ecnanib.sign-in";
    private const string SessionCookie = "ecnanib.session";
    private const string Refusal = "That code didn&#x27;t work";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_password_alone_starts_no_session()
    {
        var account = await AccountAsync();
        using var visitor = Visitor.To(app);
        var sessions = (await SessionRows.OfAsync(app.ConnectionString, account.Email, Cancellation)).Count;
        var records = (await AuditAsync(account.Email)).Count;

        using var response = await visitor.SignInAsync(account.Email);
        var cookies = response.Headers.GetValues("Set-Cookie").ToList();

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(Visitor.SignInCodePage, Visitor.Target(response));
        Assert.Contains(cookies, cookie => cookie.StartsWith(ChallengeCookie + "=", StringComparison.Ordinal) && cookie.Contains("path=/sign-in", StringComparison.OrdinalIgnoreCase) && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(cookies, cookie => cookie.StartsWith(SessionCookie, StringComparison.Ordinal));
        Assert.Equal(sessions, (await SessionRows.OfAsync(app.ConnectionString, account.Email, Cancellation)).Count);
        Assert.Equal(records, (await AuditAsync(account.Email)).Count);

        using var accountPage = await visitor.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Redirect, accountPage.StatusCode);
        Assert.StartsWith("/sign-in", Visitor.Target(accountPage), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_right_code_signs_in_and_goes_where_the_sign_in_was_going()
    {
        var account = await AccountAsync();
        using var visitor = Visitor.To(app);

        using var first = await visitor.SignInAsync(account.Email, page: "/sign-in?returnUrl=%2Faccount%2Factivity");

        Assert.Equal("/sign-in/two-factor?returnUrl=%2Faccount%2Factivity", Visitor.Target(first));

        using var second = await visitor.SubmitCodeAsync(app.Clock.NextCode(account.Key), Visitor.Target(first)!);

        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.Equal("/account/activity", Visitor.Target(second));
        Assert.Contains(second.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(SessionCookie, StringComparison.Ordinal));
        Assert.Contains(second.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(ChallengeCookie + "=;", StringComparison.Ordinal));
        Assert.Contains(account.Email, await visitor.PageAsync("/account"), StringComparison.Ordinal);

        var signedIn = (await AuditAsync(account.Email)).Last(row => row.Action == "SignedIn");
        var newest = (await SessionRows.OfAsync(app.ConnectionString, account.Email, Cancellation))[^1];

        Assert.Equal(("Session", newest.ToString()), (signedIn.TargetType, signedIn.TargetId));
    }

    /// <summary>Apps show "123 456", and people type what they see.</summary>
    [Fact]
    public async Task A_code_with_a_space_in_it_works()
    {
        var account = await AccountAsync();
        using var visitor = Visitor.To(app);
        var code = app.Clock.NextCode(account.Key);

        using var response = await TwoFactorSteps.SignInAsync(visitor, account.Email, $"{code[..3]} {code[3..]}");

        Assert.Equal("/dashboard", Visitor.Target(response));
    }

    [Fact]
    public async Task A_wrong_code_is_refused_audited_and_counted()
    {
        var account = await AccountAsync();
        using var visitor = Visitor.To(app);
        var wrong = WrongCode(account.Key);

        using var response = await TwoFactorSteps.SignInAsync(visitor, account.Email, wrong);
        var page = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Refusal, page, StringComparison.Ordinal);
        Assert.DoesNotContain(wrong, page, StringComparison.Ordinal);
        Assert.Equal(2, await TwoFactorRows.FailedSignInsAsync(app.ConnectionString, account.Email, Cancellation));

        var failed = (await AuditAsync(account.Email)).Last();

        Assert.Equal(("SignInFailed", "Visitor"), (failed.Action, failed.ActorKind));
    }

    /// <summary>
    /// The password and the code share one budget. If a right password cleared the count, guessing four
    /// codes and then entering the password again would allow guesses without end.
    /// </summary>
    [Fact]
    public async Task A_right_password_doesnt_reset_the_count_of_wrong_codes()
    {
        var account = await AccountAsync();
        using var visitor = Visitor.To(app);

        (await visitor.SignInAsync(account.Email)).Dispose();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            (await visitor.SubmitCodeAsync(WrongCode(account.Key))).Dispose();
        }

        // The fifth attempt, which locks the account.
        (await visitor.SignInAsync(account.Email)).Dispose();
        using var response = await visitor.SubmitCodeAsync(app.Clock.NextCode(account.Key));

        Assert.Contains(Refusal, await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
        Assert.Null(visitor.SessionCookie);
    }

    [Fact]
    public async Task A_right_code_clears_the_count()
    {
        var account = await AccountAsync();
        using var visitor = Visitor.To(app);

        (await visitor.SignInAsync(account.Email)).Dispose();
        (await visitor.SubmitCodeAsync(WrongCode(account.Key))).Dispose();
        (await visitor.SubmitCodeAsync(app.Clock.NextCode(account.Key))).Dispose();

        Assert.Equal(0, await TwoFactorRows.FailedSignInsAsync(app.ConnectionString, account.Email, Cancellation));
    }

    /// <summary>RFC 6238 section 5.2: a code someone watched being typed mustn't work for them a moment later.</summary>
    [Fact]
    public async Task A_code_works_once()
    {
        var account = await AccountAsync();
        using var first = Visitor.To(app);
        using var second = Visitor.To(app);
        var code = app.Clock.NextCode(account.Key);

        using var used = await TwoFactorSteps.SignInAsync(first, account.Email, code);
        using var again = await TwoFactorSteps.SignInAsync(second, account.Email, code);

        Assert.Equal("/dashboard", Visitor.Target(used));
        Assert.Contains(Refusal, await again.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
    }

    /// <summary>A double submit, or someone racing the owner with a code they watched being typed.</summary>
    [Fact]
    public async Task Submissions_at_once_start_one_session()
    {
        var account = await AccountAsync();
        var accounts = app.Services.GetRequiredService<IAccounts>();
        var challenge = (await accounts.SignInAsync(account.Email, Visitor.Password, Visitor.AnyClient, Cancellation)).Challenge;
        var code = app.Clock.NextCode(account.Key);
        var sessions = (await SessionRows.OfAsync(app.ConnectionString, account.Email, Cancellation)).Count;

        var results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => accounts.CompleteSignInAsync(challenge, code, Visitor.AnyClient, Cancellation)));

        Assert.Single(results, result => result.Outcome == SignInOutcome.SignedIn);
        Assert.Equal(sessions + 1, (await SessionRows.OfAsync(app.ConnectionString, account.Email, Cancellation)).Count);
    }

    [Fact]
    public async Task A_code_from_well_before_now_is_refused()
    {
        var account = await AccountAsync();
        using var visitor = Visitor.To(app);
        var old = Authenticator.Code(account.Key, app.Clock.GetUtcNow() - TimeSpan.FromMinutes(2));
        app.Clock.Advance(TimeSpan.FromSeconds(30));

        using var response = await TwoFactorSteps.SignInAsync(visitor, account.Email, old);

        Assert.Contains(Refusal, await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once_and_is_audited()
    {
        var account = await AccountAsync();
        using var first = Visitor.To(app);
        using var second = Visitor.To(app);

        using var used = await TwoFactorSteps.SignInAsync(first, account.Email, account.RecoveryCodes[0].ToUpperInvariant());
        using var again = await TwoFactorSteps.SignInAsync(second, account.Email, account.RecoveryCodes[0]);

        Assert.Equal("/dashboard", Visitor.Target(used));
        Assert.Contains(Refusal, await again.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
        Assert.Equal(9, await TwoFactorRows.RecoveryCodesAsync(app.ConnectionString, account.Email, Cancellation));
        Assert.Single(await AuditAsync(account.Email), row => row.Action == "RecoveryCodeUsed");
    }

    [Fact]
    public async Task An_expired_challenge_sends_the_user_back_to_sign_in()
    {
        var account = await AccountAsync();
        using var visitor = Visitor.To(app);

        (await visitor.SignInAsync(account.Email, page: "/sign-in?returnUrl=%2Faccount")).Dispose();
        await TwoFactorRows.ExpireChallengeAsync(app.ConnectionString, account.Email, Cancellation);

        using var response = await visitor.GetAsync("/sign-in/two-factor?returnUrl=%2Faccount");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/sign-in?codeExpired=true&returnUrl=%2Faccount", Visitor.Target(response));
        Assert.Contains("took too long", await visitor.PageAsync(Visitor.Target(response)!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_code_for_an_expired_challenge_is_refused()
    {
        var account = await AccountAsync();
        using var visitor = Visitor.To(app);

        (await visitor.SignInAsync(account.Email)).Dispose();
        var form = await visitor.PageAsync(Visitor.SignInCodePage);
        await TwoFactorRows.ExpireChallengeAsync(app.ConnectionString, account.Email, Cancellation);

        using var response = await PostCodeAsync(visitor, form, app.Clock.NextCode(account.Key));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/sign-in?codeExpired=true", Visitor.Target(response));
        Assert.Null(visitor.SessionCookie);
    }

    [Fact]
    public async Task Without_a_challenge_the_page_sends_the_visitor_to_sign_in()
    {
        using var visitor = Visitor.To(app);

        using var response = await visitor.GetAsync("/sign-in/two-factor?returnUrl=%2Faccount");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/sign-in?returnUrl=%2Faccount", Visitor.Target(response));
    }

    /// <summary>The return URL is anyone's to write, so only a local one is carried on.</summary>
    [Fact]
    public async Task A_return_url_elsewhere_is_dropped()
    {
        var account = await AccountAsync();
        using var visitor = Visitor.To(app);

        using var response = await visitor.SignInAsync(account.Email, page: "/sign-in?returnUrl=https%3A%2F%2Fevil.example%2F");

        Assert.Equal(Visitor.SignInCodePage, Visitor.Target(response));
    }

    /// <summary>The password step started with the old password, so it mustn't finish with the new one in place.</summary>
    [Fact]
    public async Task A_password_change_ends_a_sign_in_waiting_for_its_code()
    {
        var account = await AccountAsync();
        using var owner = Visitor.To(app);
        using var waiting = Visitor.To(app);

        (await TwoFactorSteps.SignInAsync(owner, account.Email, app.Clock.NextCode(account.Key))).Dispose();
        (await waiting.SignInAsync(account.Email)).Dispose();
        var form = await waiting.PageAsync(Visitor.SignInCodePage);
        (await owner.ChangePasswordAsync(Visitor.Password, "a new password of some length")).Dispose();

        using var response = await PostCodeAsync(waiting, form, app.Clock.NextCode(account.Key));

        Assert.Equal("/sign-in?codeExpired=true", Visitor.Target(response));
        Assert.Single(await SessionRows.OfAsync(app.ConnectionString, account.Email, Cancellation));
    }

    /// <summary>Otherwise an emailed link, or an administrator's, would be enough to take over an account that has two-factor.</summary>
    [Fact]
    public async Task A_password_reset_leaves_two_factor_on()
    {
        var captured = new CapturedEmail();
        var clock = new TestClock();
        await using var emailApp = AccountApp.Create(
            postgres,
            AccountApp.EmailOn(),
            services =>
            {
                captured.ReplaceSender(services);
                TwoFactorSteps.UseClock(clock)(services);
            });
        using var visitor = Visitor.To(emailApp);
        var account = await TwoFactorSteps.SignUpWithTwoFactorAsync(visitor, clock);
        var resets = emailApp.Services.GetRequiredService<IPasswordResets>();
        var accounts = emailApp.Services.GetRequiredService<IAccounts>();

        await resets.RequestAsync(account.Email, Cancellation);
        var token = CapturedEmail.TokenIn(captured.LastPathTo(account.Email));

        Assert.Equal(PasswordResetResult.Reset, await resets.ResetAsync(token, "a new password of some length", Cancellation));
        Assert.Equal(SignInOutcome.CodeRequired, (await accounts.SignInAsync(account.Email, "a new password of some length", Visitor.AnyClient, Cancellation)).Outcome);
    }

    [Fact]
    public async Task The_contract_refuses_a_challenge_it_never_issued()
    {
        var accounts = app.Services.GetRequiredService<IAccounts>();

        Assert.Equal(SignInOutcome.ChallengeExpired, (await accounts.CompleteSignInAsync("not a challenge", "123456", Visitor.AnyClient, Cancellation)).Outcome);
        Assert.Equal(SignInOutcome.ChallengeExpired, (await accounts.CompleteSignInAsync(null, "123456", Visitor.AnyClient, Cancellation)).Outcome);
    }

    [Fact]
    public async Task The_challenge_never_shows_up_in_a_result_string()
    {
        var account = await AccountAsync();
        var accounts = app.Services.GetRequiredService<IAccounts>();

        var result = await accounts.SignInAsync(account.Email, Visitor.Password, Visitor.AnyClient, Cancellation);

        Assert.Equal(SignInOutcome.CodeRequired, result.Outcome);
        Assert.DoesNotContain(result.Challenge!, result.ToString(), StringComparison.Ordinal);
    }

    private async Task<TwoFactorAccount> AccountAsync()
    {
        using var visitor = Visitor.To(app);

        return await TwoFactorSteps.SignUpWithTwoFactorAsync(visitor, app.Clock);
    }

    /// <summary>A code that's wrong for every step in the window.</summary>
    private string WrongCode(string key)
    {
        var now = app.Clock.GetUtcNow();
        var window = new[] { -30, 0, 30, 60 }.Select(seconds => Authenticator.Code(key, now.AddSeconds(seconds))).ToHashSet();

        return Enumerable.Range(0, 10).Select(digit => new string((char)('0' + digit), 6)).First(code => !window.Contains(code));
    }

    private async Task<List<AuditRow>> AuditAsync(string email) =>
        await AuditRows.OfUserAsync(app.ConnectionString, await AccountRows.IdAsync(app.ConnectionString, email, Cancellation), Cancellation);

    /// <summary>Posts a code form fetched earlier, for when the challenge has ended since: fetching it now would redirect.</summary>
    private static async Task<HttpResponseMessage> PostCodeAsync(Visitor visitor, string form, string code)
    {
        var values = System.Text.RegularExpressions.Regex.Matches(form, """<input\b[^>]*type="hidden"[^>]*name="(?<name>[^"]*)"[^>]*value="(?<value>[^"]*)"[^>]*>""")
            .ToDictionary(match => WebUtility.HtmlDecode(match.Groups["name"].Value), match => WebUtility.HtmlDecode(match.Groups["value"].Value));

        values["Input.TwoFactorCode"] = code;

        using var content = new FormUrlEncodedContent(values);

        return await visitor.Client.PostAsync(Visitor.SignInCodePage, content, Cancellation);
    }
}
