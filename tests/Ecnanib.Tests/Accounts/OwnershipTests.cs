using System.Security.Claims;
using Ecnanib.Accounts;
using Ecnanib.Platform.Persistence;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// Every resource belongs to exactly one user (FR-ACC-5). A request reaches the rows of the user
/// it is signed in as, and a row of another user is out of its reach (NFR-SEC-3, NFR-DAT-9).
/// These tests are about the kinds of rows that Accounts keeps for a user. Each later kind of
/// resource brings its own, which <see cref="OwnershipModelTests"/> asks for.
/// </summary>
public class OwnershipTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string WrongCode = "That code is not right.";
    private const string WhoAsks = "/who-asks";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    [ProvesOwnershipOf("audit_events", "legal_acceptances", "pending_sign_ins", "sessions", "two_factor_recovery_codes")]
    public async Task Nothing_a_user_does_changes_a_row_of_another_user()
    {
        const string newPassword = "another correct horse battery staple";

        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);

        // Grace has a row in every table that holds rows of users: what she accepted, her
        // recovery codes, the event of turning two-factor authentication on, a session on her
        // desk, and a sign-in on her phone that waits for its code.
        var (grace, gracesApp, _) = await application.RegisterWithTwoFactorAsync(Grace, clock);
        using var gracesDesk = application.CreateClient();
        using var gracesPhone = application.CreateClient();
        await gracesDesk.SignInAsync(Grace);
        Assert.Equal("/", (await gracesDesk.EnterCodeAsync(gracesApp.CodeAt(clock.GetUtcNow()))).Path);
        await gracesPhone.SignInAsync(Grace);

        var before = application.RowsOf(grace.Id);
        Assert.All(before, table => Assert.True(table.Value.Count > 0, $"Grace has no row in {table.Key}, so nothing is compared there."));

        // Ada does everything an account can do, on two browsers.
        var ada = await application.RegisterVerifiedAsync(Ada);
        using var desk = application.CreateClient();
        using var phone = application.CreateClient();
        await desk.SignInAsync(Ada);
        await phone.SignInAsync(Ada);
        await desk.ConfirmIdentityAsync();

        // Turning two-factor authentication on ends every other session of the account.
        var (app, recoveryCodes) = await desk.TurnOnTwoFactorAsync(clock);

        // A sign-in that waits for a code, and takes a recovery code.
        clock.Set(clock.GetUtcNow().AddMinutes(1));
        await phone.SignInAsync(Ada);
        Assert.Equal("/", (await phone.EnterCodeAsync(recoveryCodes[0])).Path);

        // New recovery codes replace every one of the account.
        await desk.SubmitAsync(
            "/account/two-factor/recovery-codes", "new-recovery-codes", ("Code", app.CodeAt(clock.GetUtcNow())));

        // Turning it off deletes the codes of the account, and its sign-ins that wait.
        clock.Set(clock.GetUtcNow().AddMinutes(1));
        await desk.SubmitAsync("/account/two-factor/turn-off", "turn-off", ("Code", app.CodeAt(clock.GetUtcNow())));

        // A new password ends every session of the account.
        await application.RequestResetAsync(Ada);
        Assert.Equal(
            PasswordResetOutcome.Done,
            await application.ResetAsync(AccountsApplication.TokenIn(application.Email.Sent[^1]), newPassword));

        await application.RegisterAsync(Ada);
        Assert.Equal("/", (await desk.SignInAsync(Ada, newPassword)).Path);
        await desk.SignOutAsync();

        // All of it happened, to Ada.
        Assert.Equal(
            [
                nameof(AuditAction.TwoFactorTurnedOn),
                nameof(AuditAction.TwoFactorRecoveryCodeUsed),
                nameof(AuditAction.TwoFactorRecoveryCodesReplaced),
                nameof(AuditAction.TwoFactorTurnedOff),
            ],
            StoredAuditEvent.OfTwoFactor(application.Database).Where(recorded => recorded.UserId == ada.Id).Select(recorded => recorded.Action));
        Assert.DoesNotContain(StoredSession.All(application.Database), session => session.UserId == ada.Id);
        Assert.DoesNotContain(StoredRecoveryCode.All(application.Database), code => code.UserId == ada.Id);

        // And to nobody else: every row of Grace is as it was, to the last column.
        Assert.Equal(TextOf(before), TextOf(application.RowsOf(grace.Id)));

        static string TextOf(IReadOnlyDictionary<string, IReadOnlyList<string>> rows) =>
            string.Join(
                Environment.NewLine,
                rows.OrderBy(table => table.Key, StringComparer.Ordinal).SelectMany(table => table.Value.Select(row => $"{table.Key}: {row}")));
    }

    [Fact]
    [ProvesOwnershipOf("two_factor_recovery_codes")]
    public async Task A_recovery_code_of_another_user_is_a_wrong_code_wherever_a_code_is_taken_and_is_not_spent()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (grace, _, gracesCodes) = await application.RegisterWithTwoFactorAsync(Grace, clock);
        var (ada, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();

        // Signing in.
        await client.SignInAsync(Ada);
        Assert.Contains(WrongCode, (await client.EnterCodeAsync(gracesCodes[0])).Page);
        Assert.Empty(StoredSession.All(application.Database));
        Assert.Equal("/", (await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()))).Path);

        // Proving again who one is, making new recovery codes, and turning it off.
        Assert.Contains(WrongCode, (await client.ConfirmIdentityAsync(code: gracesCodes[0])).Page);
        Assert.Contains(
            WrongCode,
            (await client.SubmitAsync("/account/two-factor/recovery-codes", "new-recovery-codes", ("Code", gracesCodes[0]))).Page);
        Assert.Contains(
            WrongCode,
            (await client.SubmitAsync("/account/two-factor/turn-off", "turn-off", ("Code", gracesCodes[0]))).Page);

        // Nothing of it was done: Ada proved nothing, and has it on with her own ten codes.
        Assert.Null(Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);
        Assert.NotNull(StoredTwoFactor.Of(application.Database, Ada).EnabledAt);

        var codes = StoredRecoveryCode.All(application.Database);
        Assert.Equal(10, codes.Count(code => code.UserId == ada.Id));
        Assert.Equal(10, codes.Count(code => code.UserId == grace.Id));

        // The wrong codes are counted for the account that gave them: the three since the right one.
        Assert.Equal(3, StoredTwoFactor.Of(application.Database, Ada).FailedCodes);
        Assert.Equal(0, StoredTwoFactor.Of(application.Database, Grace).FailedCodes);

        // The code is still Grace's to use.
        using var hers = application.CreateClient();
        await hers.SignInAsync(Grace);
        Assert.Equal("/", (await hers.EnterCodeAsync(gracesCodes[0])).Path);
    }

    [Fact]
    [ProvesOwnershipOf("pending_sign_ins")]
    public async Task A_sign_in_that_waits_for_a_code_takes_the_code_of_its_own_account_and_of_no_other()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (grace, gracesApp, _) = await application.RegisterWithTwoFactorAsync(Grace, clock);
        var (ada, app, recoveryCodes) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();

        // Someone who knows the password of Grace, and has the app and the recovery codes of Ada.
        await client.SignInAsync(Grace);
        var spent = StoredTwoFactor.Of(application.Database, Ada).LastStep;

        Assert.Contains(WrongCode, (await client.EnterCodeAsync(CodeOfOneThatIsNoCodeOfTheOther(app, gracesApp, clock))).Page);
        Assert.Contains(WrongCode, (await client.EnterCodeAsync(recoveryCodes[0])).Page);

        // Nobody is signed in, and nothing of Ada is spent or counted.
        Assert.Empty(StoredSession.All(application.Database));
        Assert.Equal(spent, StoredTwoFactor.Of(application.Database, Ada).LastStep);
        Assert.Equal(0, StoredTwoFactor.Of(application.Database, Ada).FailedCodes);
        Assert.Equal(10, StoredRecoveryCode.All(application.Database).Count(code => code.UserId == ada.Id));
        Assert.Equal(2, StoredTwoFactor.Of(application.Database, Grace).FailedCodes);

        // The sign-in still waits, for the code of the account whose password was given.
        Assert.Equal(grace.Id, Assert.Single(StoredPendingSignIn.All(application.Database)).UserId);
        Assert.Equal("/", (await client.EnterCodeAsync(gracesApp.CodeAt(clock.GetUtcNow()))).Path);
        Assert.Equal(grace.Id, Assert.Single(StoredSession.All(application.Database)).UserId);
    }

    [Fact]
    [ProvesOwnershipOf("sessions")]
    public async Task The_rows_of_a_user_are_asked_for_by_owner_and_a_row_of_another_user_is_not_found()
    {
        using var application = new AccountsApplication();
        using var desk = application.CreateClient();
        using var phone = application.CreateClient();
        using var hers = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        await desk.SignInAsync(Ada);
        await phone.SignInAsync(Ada);
        await hers.SignInAsync(Grace);

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await application.Contexts.CreateDbContextAsync(cancellationToken);

        // As a component asks: the rows of one user.
        var sessions = await context.Set<Session>().OwnedBy(ada.Id).ToListAsync(cancellationToken);
        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, session => Assert.Equal(ada.Id, session.UserId));
        Assert.Equal(
            LegalDocuments.All.Count, await context.Set<LegalAcceptance>().OwnedBy(grace.Id).CountAsync(cancellationToken));

        // A row of another user is not found, though its id is the right one. Whoever asks cannot
        // tell it from a row that does not exist.
        var gracesSession = Assert.Single(StoredSession.All(application.Database), session => session.UserId == grace.Id);
        Assert.Null(await context.Set<Session>().OwnedBy(ada.Id)
            .SingleOrDefaultAsync(session => session.Id == gracesSession.Id, cancellationToken));
        Assert.NotNull(await context.Set<Session>().OwnedBy(grace.Id)
            .SingleOrDefaultAsync(session => session.Id == gracesSession.Id, cancellationToken));
        Assert.Empty(await context.Set<Session>().OwnedBy(Guid.CreateVersion7()).ToListAsync(cancellationToken));

        // What is changed is scoped as what is read is.
        Assert.Equal(2, await context.Set<Session>().OwnedBy(ada.Id).ExecuteDeleteAsync(cancellationToken));
        Assert.Equal(grace.Id, Assert.Single(StoredSession.All(application.Database)).UserId);
    }

    [Fact]
    public async Task The_user_of_a_request_is_the_one_of_its_session_whatever_the_request_says_and_nobody_for_a_visitor()
    {
        using var application = new AccountsApplication(host: host => host.HandleAtTheEnd(WhoAsksAsync));
        using var adas = application.CreateClient();
        using var graces = application.CreateClient();
        using var visitor = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        await adas.SignInAsync(Ada);
        await graces.SignInAsync(Grace);

        Assert.Equal(ada.Id.ToString(), await adas.PageAsync(WhoAsks));
        Assert.Equal(grace.Id.ToString(), await graces.PageAsync(WhoAsks));
        Assert.Equal("nobody", await visitor.PageAsync(WhoAsks));

        // The user comes from the session. What a request says about itself changes nothing.
        Assert.Equal(ada.Id.ToString(), await adas.PageAsync($"{WhoAsks}?userId={grace.Id}"));
        Assert.Equal("nobody", await visitor.PageAsync($"{WhoAsks}?userId={grace.Id}"));
    }

    [Fact]
    public void Someone_who_is_not_signed_in_is_nobody_even_with_the_id_of_an_account()
    {
        var id = new Claim(ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString());

        // An identity without an authentication type is one that nothing vouched for.
        Assert.Null(SignedInUser.IdOf(new ClaimsPrincipal(new ClaimsIdentity([id]))));
        Assert.Null(SignedInUser.IdOf(new ClaimsPrincipal()));
        Assert.Null(SignedInUser.IdOf(new DefaultHttpContext()));
        Assert.Throws<InvalidOperationException>(() => SignedInUser.RequiredIdOf(new DefaultHttpContext()));

        Assert.Equal(
            Guid.Parse(id.Value),
            SignedInUser.IdOf(new ClaimsPrincipal(new ClaimsIdentity([id], SessionAuthentication.Scheme))));
    }

    /// <summary>Stands for whatever serves a request for a user's data: it says who it would serve.</summary>
    private static async Task<bool> WhoAsksAsync(HttpContext request)
    {
        if (!request.Request.Path.StartsWithSegments(WhoAsks))
        {
            return false;
        }

        await request.Response.WriteAsync(SignedInUser.IdOf(request)?.ToString() ?? "nobody");

        return true;
    }

    /// <summary>
    /// A code that the app of one user shows, at a moment when it is no code of the other user's
    /// app. Two apps show the same six digits once in a few hundred thousand times.
    /// </summary>
    private static string CodeOfOneThatIsNoCodeOfTheOther(AuthenticatorApp one, AuthenticatorApp other, TestClock clock)
    {
        while (true)
        {
            var now = clock.GetUtcNow();
            var code = one.CodeAt(now);

            if (new[] { -30, 0, 30 }.All(seconds => other.CodeAt(now.AddSeconds(seconds)) != code))
            {
                return code;
            }

            clock.Set(now.AddSeconds(30));
        }
    }
}
