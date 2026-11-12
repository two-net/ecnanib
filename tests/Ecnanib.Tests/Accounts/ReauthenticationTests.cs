using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// Sensitive actions require re-authentication (FR-ACC-4): adding or replacing a key, switching an
/// agent to Live, and deleting the account are done only for a user who has just proved again who
/// they are, with their password and, when they have two-factor authentication on, a code. The
/// check is made on the server. Deleting the account is built, and <see cref="AccountDeletionTests"/>
/// is about it. The other two actions are not yet, so a handler of the tests stands for all
/// three here: it asks first, as they do.
/// </summary>
public class ReauthenticationTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string ConfirmPage = SessionAuthentication.ReauthenticationPath;

    private const string WrongPassword = "That is not your password.";
    private const string WrongCode = "That code is not right.";
    private const string Refused = "Too many wrong codes were entered.";

    private const string AskedTemplate = "User {UserId} is asked to confirm who they are before {Action}";
    private const string ConfirmedTemplate = "User {UserId} confirmed who they are";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    public static TheoryData<SensitiveAction> EverySensitiveAction => new(Enum.GetValues<SensitiveAction>());

    [Fact]
    public void The_sensitive_actions_are_the_three_of_the_requirement()
    {
        Assert.Equal(
            [SensitiveAction.AddOrReplaceKey, SensitiveAction.SwitchAgentToLive, SensitiveAction.DeleteAccount],
            Enum.GetValues<SensitiveAction>());
    }

    [Theory]
    [MemberData(nameof(EverySensitiveAction))]
    public async Task A_sensitive_action_is_not_done_until_its_user_has_proved_again_who_they_are(SensitiveAction action)
    {
        var clock = new TestClock(Morning);
        var actions = new SensitiveActions();
        using var application = new AccountsApplication(clock: clock, host: host => host.HandleAtTheEnd(actions.HandleAsync));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // Asked for directly, as something that is not a page of the application can. Being
        // signed in is not enough: the action is not done, and the browser is sent to the page
        // that takes the proof.
        clock.Set(Morning.AddMinutes(1));
        var (asked, page) = await client.OpenAsync(SensitiveActions.PathOf(action));

        Assert.Equal($"{ConfirmPage}?returnUrl={Uri.EscapeDataString(SensitiveActions.PathOf(action))}", asked);
        Assert.Contains("<h1>Confirm that it is you</h1>", page);
        Assert.Empty(actions.Done);
        Assert.Null(Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);

        var held = Assert.Single(application.Events(), written => written.Template == AskedTemplate);
        Assert.Equal(user.Id.ToString(), held.Text("UserId"));
        Assert.Equal(action.ToString(), held.Text("Action"));

        // An account without two-factor authentication is asked for its password, and for no code.
        Assert.Contains("name=\"Password\"", page);
        Assert.DoesNotContain("name=\"Code\"", page);

        // The password leads back to the action, which is done now.
        var (path, answer) = await client.ConfirmIdentityAsync(page: asked);

        Assert.Equal(SensitiveActions.PathOf(action), path);
        Assert.Equal($"Done: {action}", answer);
        Assert.Equal([action], actions.Done);

        // The proof is written into the session, with the time it was given at.
        Assert.Equal(Morning.AddMinutes(1), Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);

        var confirmed = Assert.Single(application.Events(), written => written.Template == ConfirmedTemplate);
        Assert.Equal(user.Id.ToString(), confirmed.Text("UserId"));
    }

    [Fact]
    public async Task The_proof_holds_for_ten_minutes_and_no_longer()
    {
        var clock = new TestClock(Morning);
        var actions = new SensitiveActions();
        using var application = new AccountsApplication(clock: clock, host: host => host.HandleAtTheEnd(actions.HandleAsync));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        Assert.Equal(TimeSpan.FromMinutes(10), ReauthenticationRules.HoldsFor);
        Assert.Contains("holds for 10 minutes", await client.PageAsync(ConfirmPage));
        await client.ConfirmIdentityAsync();

        // Every sensitive action in that time is done, without another proof.
        var key = SensitiveActions.PathOf(SensitiveAction.AddOrReplaceKey);
        var live = SensitiveActions.PathOf(SensitiveAction.SwitchAgentToLive);

        Assert.Equal(key, (await client.OpenAsync(key)).Address);

        clock.Set(Morning + ReauthenticationRules.HoldsFor - TimeSpan.FromSeconds(1));
        Assert.Equal(live, (await client.OpenAsync(live)).Address);
        Assert.Equal([SensitiveAction.AddOrReplaceKey, SensitiveAction.SwitchAgentToLive], actions.Done);

        // The time counts from the proof. An action that was done does not make it hold longer.
        clock.Set(Morning + ReauthenticationRules.HoldsFor);
        var (asked, _) = await client.OpenAsync(key);

        Assert.StartsWith(ConfirmPage, asked);
        Assert.Equal(2, actions.Done.Count);

        // Another proof holds from when it is given.
        Assert.Equal(key, (await client.ConfirmIdentityAsync(page: asked)).Path);
        Assert.Equal(3, actions.Done.Count);
        Assert.Equal(
            Morning + ReauthenticationRules.HoldsFor, Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);
    }

    [Fact]
    [ProvesOwnershipOf("sessions")]
    public async Task The_proof_holds_for_the_browser_that_gave_it_and_for_no_other()
    {
        var actions = new SensitiveActions();
        using var application = new AccountsApplication(host: host => host.HandleAtTheEnd(actions.HandleAsync));
        using var desk = application.CreateClient();
        using var phone = application.CreateClient();
        using var stranger = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        await application.RegisterVerifiedAsync(Grace);

        await desk.SignInAsync(Ada);
        await phone.SignInAsync(Ada);
        await stranger.SignInAsync(Grace);
        await desk.ConfirmIdentityAsync();

        var delete = SensitiveActions.PathOf(SensitiveAction.DeleteAccount);

        // Another browser of the same user is asked, and so is another user.
        Assert.StartsWith(ConfirmPage, (await phone.OpenAsync(delete)).Address);
        Assert.StartsWith(ConfirmPage, (await stranger.OpenAsync(delete)).Address);
        Assert.Empty(actions.Done);

        // One session holds the proof, and it is the one that gave it.
        var sessions = StoredSession.All(application.Database);
        Assert.Equal(3, sessions.Count);
        Assert.Equal(ada.Id, Assert.Single(sessions, session => session.ReauthenticatedAt is not null).UserId);

        Assert.Equal(delete, (await desk.OpenAsync(delete)).Address);
        Assert.Equal([SensitiveAction.DeleteAccount], actions.Done);
    }

    [Fact]
    public async Task Signing_in_is_not_a_proof_so_signing_out_and_in_again_asks_again()
    {
        var actions = new SensitiveActions();
        using var application = new AccountsApplication(host: host => host.HandleAtTheEnd(actions.HandleAsync));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        await client.SignOutAsync();
        await client.SignInAsync(Ada);

        // The proof ended with the session that held it, and the password that signed in is not one.
        Assert.Null(Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);
        Assert.StartsWith(ConfirmPage, (await client.OpenAsync(SensitiveActions.PathOf(SensitiveAction.AddOrReplaceKey))).Address);
        Assert.Empty(actions.Done);
    }

    [Fact]
    public async Task A_new_password_ends_the_proof_with_the_session_that_held_it()
    {
        const string newPassword = "a new password for ada";

        var actions = new SensitiveActions();
        using var application = new AccountsApplication(host: host => host.HandleAtTheEnd(actions.HandleAsync));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        await application.RequestResetAsync(Ada);
        Assert.Equal(
            PasswordResetOutcome.Done,
            await application.ResetAsync(AccountsApplication.TokenIn(application.Email.Sent[^1]), newPassword));

        // Nobody is signed in on the browser any more, so nothing is done for it.
        var delete = SensitiveActions.PathOf(SensitiveAction.DeleteAccount);
        Assert.StartsWith(SessionAuthentication.SignInPath, (await client.OpenAsync(delete)).Address);

        // Signed in with the new password, the user is asked again.
        await client.SignInAsync(Ada, newPassword);
        Assert.StartsWith(ConfirmPage, (await client.OpenAsync(delete)).Address);
        Assert.Empty(actions.Done);
    }

    [Theory]
    [InlineData("")]
    [InlineData("correct horse battery")]
    [InlineData("correct horse battery staple ")]
    [InlineData("CORRECT HORSE BATTERY STAPLE")]
    public async Task What_is_not_the_password_proves_nothing(string password)
    {
        var actions = new SensitiveActions();
        using var application = new AccountsApplication(host: host => host.HandleAtTheEnd(actions.HandleAsync));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // Posted as it is: a browser would not send a form without a password.
        using var answer = await client.SendFormAsync(
            ConfirmPage, "confirm-identity", Browser.AntiforgeryTokenIn(await client.PageAsync(ConfirmPage)), ("Password", password));
        var page = await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // The form again, and not an error. No code was used, so the page does not speak of one.
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(ConfirmPage, answer.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains(WrongPassword, page);
        Assert.DoesNotContain("The code was used up", page);

        Assert.Null(Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);
        Assert.StartsWith(ConfirmPage, (await client.OpenAsync(SensitiveActions.PathOf(SensitiveAction.DeleteAccount))).Address);
        Assert.Empty(actions.Done);
    }

    [Fact]
    public async Task A_password_longer_than_any_account_has_proves_nothing()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var (path, page) = await client.ConfirmIdentityAsync(new string('x', PasswordRules.MaximumLength + 1));

        Assert.Equal(ConfirmPage, path);
        Assert.Contains(WrongPassword, page);
        Assert.Null(Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);
    }

    [Fact]
    public async Task With_two_factor_on_the_proof_is_the_password_and_a_code_from_the_app()
    {
        var clock = new TestClock(Morning);
        var actions = new SensitiveActions();
        using var application = new AccountsApplication(clock: clock, host: host => host.HandleAtTheEnd(actions.HandleAsync));
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await SignInWithCodeAsync(client, app, clock);

        // The password and the code that signed in are not a proof.
        clock.Set(Morning.AddMinutes(5));
        var delete = SensitiveActions.PathOf(SensitiveAction.DeleteAccount);
        var (asked, form) = await client.OpenAsync(delete);

        Assert.StartsWith(ConfirmPage, asked);
        Assert.Contains("name=\"Password\"", form);
        Assert.Contains("name=\"Code\"", form);

        // The password alone proves nothing. Nothing is not a guess at a code, and is not counted.
        var (path, page) = await client.ConfirmIdentityAsync(page: asked);

        Assert.Equal(ConfirmPage, path);
        Assert.Contains(WrongCode, page);
        Assert.DoesNotContain(WrongPassword, page);
        Assert.Null(Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);
        Assert.Equal(0, StoredTwoFactor.Of(application.Database, Ada).FailedCodes);
        Assert.Empty(actions.Done);

        // With the code of the app as well, the action is done.
        (path, _) = await client.ConfirmIdentityAsync(code: app.CodeAt(clock.GetUtcNow()), page: asked);

        Assert.Equal(delete, path);
        Assert.Equal([SensitiveAction.DeleteAccount], actions.Done);
        Assert.Equal(Morning.AddMinutes(5), Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);

        // The code is spent by it, as by a sign-in.
        Assert.Equal(Totp.StepAt(clock.GetUtcNow()), StoredTwoFactor.Of(application.Database, Ada).LastStep);
    }

    [Fact]
    public async Task A_wrong_code_proves_nothing_is_counted_and_gets_the_same_answer_whatever_the_password_is()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await SignInWithCodeAsync(client, app, clock);

        clock.Set(Morning.AddMinutes(5));
        var wrong = app.WrongCodeAt(clock.GetUtcNow());

        var (path, withThePassword) = await client.ConfirmIdentityAsync(code: wrong);
        var (_, withAnother) = await client.ConfirmIdentityAsync("not the password at all", wrong);

        // The form again. The code is not written back.
        Assert.Equal(ConfirmPage, path);
        Assert.Contains(WrongCode, withThePassword);
        Assert.DoesNotContain(wrong, withThePassword);

        // Someone who does not have the app learns nothing about the password from the form.
        Assert.DoesNotContain(WrongPassword, withThePassword);
        Assert.Equal(Browser.AsSeen(withThePassword), Browser.AsSeen(withAnother));

        Assert.Equal(2, StoredTwoFactor.Of(application.Database, Ada).FailedCodes);
        Assert.Null(Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);
    }

    [Fact]
    public async Task A_right_code_with_a_wrong_password_proves_nothing_and_the_code_is_spent()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await SignInWithCodeAsync(client, app, clock);

        clock.Set(Morning.AddMinutes(5));
        var code = app.CodeAt(clock.GetUtcNow());
        var (path, page) = await client.ConfirmIdentityAsync("not the password at all", code);

        Assert.Equal(ConfirmPage, path);
        Assert.Contains(WrongPassword, page);
        Assert.Contains("The code was used up by this try", page);
        Assert.Null(Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);
        Assert.Equal(Totp.StepAt(clock.GetUtcNow()), StoredTwoFactor.Of(application.Database, Ada).LastStep);

        // So one code does not serve for a second password. It was the right code, and is not
        // counted as a guess.
        (path, page) = await client.ConfirmIdentityAsync(code: code);

        Assert.Equal(ConfirmPage, path);
        Assert.Contains(WrongCode, page);
        Assert.Null(Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);
        Assert.Equal(0, StoredTwoFactor.Of(application.Database, Ada).FailedCodes);

        // The next code of the app, with the password, is the proof.
        clock.Set(clock.GetUtcNow().AddSeconds(30));
        Assert.Equal("/", (await client.ConfirmIdentityAsync(code: app.CodeAt(clock.GetUtcNow()))).Path);
        Assert.NotNull(Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);
    }

    [Fact]
    public async Task A_recovery_code_stands_for_the_code_of_the_app_once_and_is_recorded()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (user, app, recoveryCodes) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await SignInWithCodeAsync(client, app, clock);

        clock.Set(Morning.AddMinutes(5));
        Assert.Equal("/", (await client.ConfirmIdentityAsync(code: recoveryCodes[2])).Path);

        Assert.Equal(Morning.AddMinutes(5), Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);
        Assert.Equal(9, StoredRecoveryCode.All(application.Database).Count);

        // The audit trail has the code that was spent, and then the proof it gave, after what
        // led here: a sign-in to turn two-factor authentication on, and one with a code.
        var events = StoredAuditEvent.All(application.Database);
        Assert.Equal(
            ["SignedIn", "TwoFactorTurnedOn", "SignedIn", "TwoFactorRecoveryCodeUsed", "Reauthenticated"],
            events.Select(recorded => recorded.Action));
        Assert.All(events.TakeLast(2), recorded =>
        {
            Assert.Equal(user.Id, recorded.UserId);
            Assert.Equal(Morning.AddMinutes(5), recorded.OccurredAt);
        });

        // When the proof no longer holds, the same recovery code is a wrong code.
        clock.Set(clock.GetUtcNow() + ReauthenticationRules.HoldsFor);
        var (path, page) = await client.ConfirmIdentityAsync(code: recoveryCodes[2]);

        Assert.Equal(ConfirmPage, path);
        Assert.Contains(WrongCode, page);
        Assert.Equal(1, StoredTwoFactor.Of(application.Database, Ada).FailedCodes);
    }

    [Fact]
    public async Task Too_many_wrong_codes_are_refused_here_as_everywhere()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await SignInWithCodeAsync(client, app, clock);

        clock.Set(Morning.AddMinutes(5));
        var wrong = app.WrongCodeAt(clock.GetUtcNow());

        var page = string.Empty;
        for (var tries = 0; tries < TwoFactorRules.WrongCodesAllowed; tries++)
        {
            (_, page) = await client.ConfirmIdentityAsync(code: wrong);
        }

        // The last of them starts the wait.
        Assert.Contains(Refused, page);
        Assert.Equal(clock.GetUtcNow() + TwoFactorRules.RefusedFor, StoredTwoFactor.Of(application.Database, Ada).LockedUntil);

        // While it lasts, the right code and the password prove nothing, and the code is not spent.
        var spent = StoredTwoFactor.Of(application.Database, Ada).LastStep;
        (_, page) = await client.ConfirmIdentityAsync(code: app.CodeAt(clock.GetUtcNow()));

        Assert.Contains(Refused, page);
        Assert.Null(Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);
        Assert.Equal(spent, StoredTwoFactor.Of(application.Database, Ada).LastStep);
    }

    [Fact]
    public async Task Nothing_is_done_for_a_visitor_who_is_sent_to_sign_in_and_then_to_prove_it()
    {
        var actions = new SensitiveActions();
        using var application = new AccountsApplication(host: host => host.HandleAtTheEnd(actions.HandleAsync));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        // The step itself says no for a request that is not signed in.
        Assert.False(await application.Reauthentication.IsRecentAsync(new DefaultHttpContext(), SensitiveAction.DeleteAccount));

        var delete = SensitiveActions.PathOf(SensitiveAction.DeleteAccount);
        var proof = $"{ConfirmPage}?returnUrl={Uri.EscapeDataString(delete)}";
        var (asked, form) = await client.OpenAsync(delete);

        // The page that takes the proof is for signed-in users.
        Assert.Equal($"{SessionAuthentication.SignInPath}?returnUrl={Uri.EscapeDataString(proof)}", asked);
        Assert.Contains("<h1>Sign in</h1>", form);
        Assert.Empty(actions.Done);

        // Signing in leads to that page, and not past it.
        using var signedIn = await client.SendFormAsync(
            asked, "sign-in", Browser.AntiforgeryTokenIn(form), ("Email", Ada), ("Password", AccountsApplication.Password));

        Assert.Equal(proof, signedIn.RequestMessage!.RequestUri!.PathAndQuery);
        Assert.Empty(actions.Done);

        // And the proof leads to the action.
        Assert.Equal(delete, (await client.ConfirmIdentityAsync(page: proof)).Path);
        Assert.Equal([SensitiveAction.DeleteAccount], actions.Done);
    }

    [Fact]
    public async Task Someone_who_has_just_proved_it_is_sent_on_from_the_page()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // Not before: the page is shown to whoever has not proved it.
        Assert.Equal($"{ConfirmPage}?returnUrl=%2Faccount", (await client.OpenAsync($"{ConfirmPage}?returnUrl=%2Faccount")).Address);

        Assert.Equal("/account", (await client.ConfirmIdentityAsync(page: $"{ConfirmPage}?returnUrl=%2Faccount")).Path);

        Assert.Equal("/account", (await client.OpenAsync($"{ConfirmPage}?returnUrl=%2Faccount")).Address);
        Assert.Equal("/", (await client.OpenAsync(ConfirmPage)).Address);
    }

    [Theory]
    [InlineData("https://evil.example/account")]
    [InlineData("//evil.example/account")]
    [InlineData("/\\evil.example/account")]
    [InlineData("\\\\evil.example\\account")]
    [InlineData("evil.example")]
    public async Task Proving_it_does_not_lead_to_another_site(string returnUrl)
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var form = $"{ConfirmPage}?returnUrl={Uri.EscapeDataString(returnUrl)}";

        using var answer = await client.SendFormAsync(
            form,
            "confirm-identity",
            Browser.AntiforgeryTokenIn(await client.PageAsync(form)),
            ("Password", AccountsApplication.Password));

        // Proved, and at the start page of the application.
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(new Uri("https://localhost/"), answer.RequestMessage!.RequestUri);
        Assert.NotNull(Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);

        // The same for someone who has proved it already and opens the link.
        using var opened = await client.GetAsync(form, TestContext.Current.CancellationToken);
        Assert.Equal(new Uri("https://localhost/"), opened.RequestMessage!.RequestUri);
    }

    [Fact]
    public async Task A_post_without_the_token_against_request_forgery_proves_nothing()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // From a page of another site, which has no token of this one.
        await client.PostAsync(ConfirmPage, "confirm-identity", "not-the-token", ("Password", AccountsApplication.Password));

        Assert.Null(Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);
    }

    [Fact]
    public async Task A_proof_for_a_session_that_has_ended_is_kept_nowhere_and_its_code_is_not_spent()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (user, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);

        // A request that was signed in with a session which has ended by the time the proof is
        // looked at: the user signed out on another tab in between.
        var request = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], SessionAuthentication.Scheme)),
        };
        request.Request.Headers.Cookie = $"__Host-ecnanib-session={Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32))}";

        clock.Set(Morning.AddMinutes(5));
        var spent = StoredTwoFactor.Of(application.Database, Ada).LastStep;

        var outcome = await application.Reauthentication.ConfirmAsync(
            request, AccountsApplication.Password, app.CodeAt(clock.GetUtcNow()));

        Assert.Equal(ReauthenticationOutcome.SessionEnded, outcome);
        Assert.Empty(StoredSession.All(application.Database));
        Assert.Equal(spent, StoredTwoFactor.Of(application.Database, Ada).LastStep);
        Assert.DoesNotContain(application.Events(), written => written.Template == ConfirmedTemplate);
    }

    [Fact]
    public async Task The_password_and_the_code_are_in_neither_the_page_nor_the_log_and_the_log_names_no_address()
    {
        const string typed = "WRONG-PASSWORD-MARKER";

        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, recoveryCodes) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await SignInWithCodeAsync(client, app, clock);

        clock.Set(Morning.AddMinutes(5));
        var code = app.CodeAt(clock.GetUtcNow());
        var (_, wrongPassword) = await client.ConfirmIdentityAsync(typed, code);

        clock.Set(Morning.AddMinutes(6));
        var (path, _) = await client.ConfirmIdentityAsync(code: recoveryCodes[0]);
        Assert.Equal("/", path);

        // Nothing that was typed is written back into the page.
        Assert.DoesNotContain(typed, wrongPassword);
        Assert.DoesNotContain(code, wrongPassword);

        var log = application.LogText();
        Assert.DoesNotContain(typed, log);
        Assert.DoesNotContain(AccountsApplication.Password, log);
        Assert.DoesNotContain(recoveryCodes[0], log);
        Assert.DoesNotContain("example.com", log);

        // The database holds when the proof was given, and nothing of what it was.
        var stored = application.EverythingStored();
        Assert.DoesNotContain(AccountsApplication.Password, stored);
        Assert.DoesNotContain(recoveryCodes[0], stored);
    }

    /// <summary>Signs in to an account with two-factor authentication: the password, and then the code of the app.</summary>
    private static async Task SignInWithCodeAsync(HttpClient client, AuthenticatorApp app, TestClock clock)
    {
        await client.SignInAsync(Ada);

        Assert.Equal("/", (await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()))).Path);
    }

    /// <summary>
    /// Stands for the sensitive actions, which are not built yet. It does what each of them will:
    /// it asks first, on the server, and does nothing when the answer is no. It keeps what it did.
    /// </summary>
    private sealed class SensitiveActions
    {
        private const string Prefix = "/does-something-sensitive";

        private readonly ConcurrentQueue<SensitiveAction> _done = new();

        /// <summary>The actions that were done, in order.</summary>
        public IReadOnlyList<SensitiveAction> Done => [.. _done];

        public static string PathOf(SensitiveAction action) => $"{Prefix}/{action}";

        public async Task<bool> HandleAsync(HttpContext request)
        {
            if (!request.Request.Path.StartsWithSegments(Prefix, out var rest)
                || !Enum.TryParse<SensitiveAction>(rest.Value?.TrimStart('/'), out var action))
            {
                return false;
            }

            var reauthentication = request.RequestServices.GetRequiredService<IReauthentication>();
            if (!await reauthentication.IsRecentAsync(request, action))
            {
                // Not done. As a page will: to the page that takes the proof, and from there back.
                request.Response.Redirect(
                    SessionAuthentication.PageWith(SessionAuthentication.ReauthenticationPath, request.Request.Path));

                return true;
            }

            _done.Enqueue(action);
            await request.Response.WriteAsync($"Done: {action}");

            return true;
        }
    }
}
