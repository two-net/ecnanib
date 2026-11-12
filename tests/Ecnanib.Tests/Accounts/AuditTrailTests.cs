using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// The audit trail records security-relevant actions in the database, apart from the logs
/// (NFR-LOG-7): each event with the user, the action, the target, the time, and the origin, and
/// with no secret. These tests are about sign-ins, re-authentications, and new passwords, and
/// about how an event is recorded. <see cref="TwoFactorAuditTests"/> is about the changes to
/// two-factor authentication, and <see cref="AuditRulesTests"/> about what is recorded at all.
/// </summary>
public partial class AuditTrailTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string From = "203.0.113.7";
    private const string NewPassword = "another correct horse battery staple";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    [ShowsAuditOf(AuditAction.SignedIn)]
    public async Task A_sign_in_is_recorded_with_the_user_the_action_the_target_the_time_and_the_origin()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, host: host => host.From(From));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.RegisterVerifiedAsync(Grace);

        // Registering and verifying an address sign nobody in, and are not on the list.
        Assert.Empty(StoredAuditEvent.All(application.Database));

        clock.Set(Morning.AddMinutes(3));
        Assert.Equal("/", (await client.SignInAsync(Ada)).Path);

        var recorded = Assert.Single(StoredAuditEvent.All(application.Database));
        Assert.Equal(user.Id, recorded.UserId);
        Assert.Equal("SignedIn", recorded.Action);
        Assert.Equal($"user:{user.Id}", recorded.Target);
        Assert.Equal(Morning.AddMinutes(3), recorded.OccurredAt);
        Assert.Equal(From, recorded.Origin);

        // The id is one the application assigned, as the ids of its other rows are.
        Assert.Equal(7, recorded.Id.Version);

        // Signing in again, on another browser, is another event.
        using var phone = application.CreateClient();
        await phone.SignInAsync(Ada);
        Assert.Equal(["SignedIn", "SignedIn"], StoredAuditEvent.All(application.Database).Select(written => written.Action));
    }

    [Fact]
    public async Task With_two_factor_on_the_sign_in_is_recorded_when_the_code_is_accepted_and_not_when_the_password_is()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (user, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        var before = StoredAuditEvent.All(application.Database).Count;

        // The right password signs nobody in: it starts a sign-in that waits for a code.
        Assert.Equal(SessionAuthentication.SignInCodePath, (await client.SignInAsync(Ada)).Path);
        Assert.Equal(before, StoredAuditEvent.All(application.Database).Count);

        // Nor does a code that is not right.
        await client.EnterCodeAsync(app.WrongCodeAt(clock.GetUtcNow()));
        Assert.Equal(before, StoredAuditEvent.All(application.Database).Count);
        Assert.Empty(StoredSession.All(application.Database));

        // The code does, and the event is of the moment it was accepted at.
        clock.Set(clock.GetUtcNow().AddMinutes(2));
        Assert.Equal("/", (await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()))).Path);

        var events = StoredAuditEvent.All(application.Database);
        Assert.Equal(before + 1, events.Count);
        Assert.Equal("SignedIn", events[^1].Action);
        Assert.Equal(user.Id, events[^1].UserId);
        Assert.Equal(clock.GetUtcNow(), events[^1].OccurredAt);
        Assert.Single(StoredSession.All(application.Database));
    }

    [Fact]
    public async Task A_recovery_code_that_signs_in_is_recorded_and_then_the_sign_in_it_made()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (user, _, recoveryCodes) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        Assert.Equal("/", (await client.EnterCodeAsync(recoveryCodes[0])).Path);

        // Two events of one transaction, at one time. Their ids still say which came first.
        var events = StoredAuditEvent.All(application.Database);
        Assert.Equal(["TwoFactorRecoveryCodeUsed", "SignedIn"], events.TakeLast(2).Select(recorded => recorded.Action));
        Assert.All(events.TakeLast(2), recorded =>
        {
            Assert.Equal(user.Id, recorded.UserId);
            Assert.Equal(clock.GetUtcNow(), recorded.OccurredAt);
        });
    }

    [Fact]
    public async Task What_signs_nobody_in_is_not_recorded_and_neither_is_signing_out()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        // A wrong password, an address without an account, and what is not an address at all.
        // They are in the log, as before.
        await client.SignInAsync(Ada, "not the password at all");
        await client.SignInAsync(Grace);
        await client.SignInAsync("nobody");

        Assert.Empty(StoredAuditEvent.All(application.Database));
        Assert.Equal(
            3,
            application.Events().Count(written => written.Template == "A sign-in failed: the address or the password was not right"));

        // Signing out ends a session, as staying away does, which is nobody's action.
        await client.SignInAsync(Ada);
        await client.SignOutAsync();

        Assert.Equal("SignedIn", Assert.Single(StoredAuditEvent.All(application.Database)).Action);
        Assert.Empty(StoredSession.All(application.Database));
    }

    [Fact]
    [ShowsAuditOf(AuditAction.Reauthenticated)]
    public async Task A_re_authentication_is_recorded_and_a_proof_that_is_not_right_is_not()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, host: host => host.From(From));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // What is not the password proves nothing, and records nothing.
        clock.Set(Morning.AddMinutes(4));
        await client.ConfirmIdentityAsync("not the password at all");
        Assert.Equal("SignedIn", Assert.Single(StoredAuditEvent.All(application.Database)).Action);

        clock.Set(Morning.AddMinutes(5));
        Assert.Equal("/", (await client.ConfirmIdentityAsync()).Path);

        var events = StoredAuditEvent.All(application.Database);
        Assert.Equal(["SignedIn", "Reauthenticated"], events.Select(recorded => recorded.Action));

        var proof = events[^1];
        Assert.Equal(user.Id, proof.UserId);
        Assert.Equal($"user:{user.Id}", proof.Target);
        Assert.Equal(Morning.AddMinutes(5), proof.OccurredAt);
        Assert.Equal(From, proof.Origin);

        // It is the time that the session holds as its proof.
        Assert.Equal(proof.OccurredAt, Assert.Single(StoredSession.All(application.Database)).ReauthenticatedAt);
    }

    [Fact]
    [ShowsAuditOf(AuditAction.PasswordChanged)]
    public async Task A_new_password_by_reset_link_is_recorded_with_where_it_came_from()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, host: host => host.From(From));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.RegisterVerifiedAsync(Grace);

        // Asking for the link changes nothing, and neither does a password that the rules refuse.
        await client.SubmitAsync("/forgot-password", "forgot-password", ("Email", Ada));
        var link = AccountsApplication.LinkIn(application.Email.Sent[^1]);
        await client.SubmitAsync(link.PathAndQuery, "reset-password", ("Password", "too short"));
        Assert.Empty(StoredAuditEvent.All(application.Database));

        // The password that is set is recorded, in the transaction that set it.
        clock.Set(Morning.AddMinutes(20));
        var (done, _) = await client.SubmitAsync(link.PathAndQuery, "reset-password", ("Password", NewPassword));
        Assert.Equal("/reset-password/done", done);

        var recorded = Assert.Single(StoredAuditEvent.All(application.Database));
        Assert.Equal(user.Id, recorded.UserId);
        Assert.Equal("PasswordChanged", recorded.Action);
        Assert.Equal($"user:{user.Id}", recorded.Target);
        Assert.Equal(Morning.AddMinutes(20), recorded.OccurredAt);
        Assert.Equal(From, recorded.Origin);

        // The link works once: a second password is not set, and not recorded.
        await client.SubmitAsync(link.PathAndQuery, "reset-password", ("Password", "yet another password"));
        Assert.Single(StoredAuditEvent.All(application.Database));
    }

    [Fact]
    public async Task Of_several_requests_with_one_reset_link_one_new_password_is_recorded()
    {
        using var application = new AccountsApplication();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.RequestResetAsync(Ada);
        var token = AccountsApplication.TokenIn(application.Email.Sent[^1]);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(
            attempt => Task.Run(() => application.ResetAsync(token, $"the password of attempt {attempt}"))));

        Assert.Single(outcomes, outcome => outcome == PasswordResetOutcome.Done);

        var recorded = Assert.Single(StoredAuditEvent.All(application.Database));
        Assert.Equal("PasswordChanged", recorded.Action);
        Assert.Equal(user.Id, recorded.UserId);

        // Asked of the service itself and not through a page, so where it came from is not known.
        Assert.Null(recorded.Origin);
    }

    [Fact]
    [ShowsAuditOf(AuditAction.PasswordChanged)]
    public async Task A_password_chosen_with_the_link_of_a_repeat_registration_is_recorded_and_a_first_verification_is_not()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, host: host => host.From(From));
        using var client = application.CreateClient();

        // A first link verifies the address, and sets no password.
        await application.RegisterAsync(Grace);
        var first = AccountsApplication.LinkIn(application.Email.Sent[^1]);
        var (_, confirmed) = await client.SubmitAsync(first.PathAndQuery, "verify-email");
        Assert.Contains("Your email address is confirmed.", confirmed);
        Assert.Empty(StoredAuditEvent.All(application.Database));

        // The address is registered twice before it is confirmed. The later link replaces the
        // password of the account by the one that whoever confirms it chooses.
        await application.RegisterAsync(Ada);
        await application.RegisterAsync(Ada, "the password of someone else");
        var later = AccountsApplication.LinkIn(application.Email.Sent[^1]);
        var user = StoredUser.Of(application.Database, Ada)!;

        clock.Set(Morning.AddMinutes(10));
        var (_, chosen) = await client.SubmitAsync(later.PathAndQuery, "verify-email", ("Password", NewPassword));
        Assert.Contains("Your email address is confirmed.", chosen);

        var recorded = Assert.Single(StoredAuditEvent.All(application.Database));
        Assert.Equal(user.Id, recorded.UserId);
        Assert.Equal("PasswordChanged", recorded.Action);
        Assert.Equal($"user:{user.Id}", recorded.Target);
        Assert.Equal(Morning.AddMinutes(10), recorded.OccurredAt);
        Assert.Equal(From, recorded.Origin);
        Assert.True(await application.Passwords.VerifyAsync(NewPassword, StoredUser.Of(application.Database, Ada)!.PasswordHash));
    }

    [Fact]
    public async Task An_event_is_recorded_inside_the_transaction_of_its_action_and_stands_or_falls_with_it()
    {
        using var application = new AccountsApplication();
        var user = await application.RegisterVerifiedAsync(Ada);
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var context = await application.Contexts.CreateDbContextAsync(cancellationToken);

        Task RecordAsync() => AuditTrail.RecordAsync(
            context, user.Id, AuditAction.SignedIn, AuditTarget.Account(user.Id), Morning, AuditOrigin.Unknown, cancellationToken);

        // Without a transaction the event would be stored on its own, whatever became of the
        // action. It is refused, and nothing is stored.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(RecordAsync);
        Assert.Contains("inside the transaction of the action", refused.Message);
        Assert.Empty(StoredAuditEvent.All(application.Database));

        // An action that is undone leaves no event.
        await using (var undone = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            await RecordAsync();
            await undone.RollbackAsync(cancellationToken);
        }

        Assert.Empty(StoredAuditEvent.All(application.Database));

        // One that is done leaves its event.
        await using (var done = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            await RecordAsync();
            await done.CommitAsync(cancellationToken);
        }

        var recorded = Assert.Single(StoredAuditEvent.All(application.Database));
        Assert.Equal($"user:{user.Id}", recorded.Target);
        Assert.Null(recorded.Origin);
    }

    [Fact]
    public void Ids_that_are_handed_out_within_one_millisecond_are_still_in_order()
    {
        const int count = 5000;

        // The source of the ids is private to the trail, which is the only thing that has a use
        // for it. It is asked directly here: two events are recorded within one millisecond
        // only by a machine that is faster than a test can count on.
        var source = typeof(AuditTrail).GetMethod("NextId", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(
            source is not null && source.ReturnType == typeof(Guid) && source.GetParameters().Length == 0,
            "AuditTrail no longer makes the ids of events in a method of its own, so this test looks at nothing.");

        // Far more ids than milliseconds pass while they are made.
        var started = DateTimeOffset.UtcNow;
        var ids = Enumerable.Range(0, count).Select(_ => (Guid)source.Invoke(null, null)!).ToList();
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromMilliseconds(count / 2), "The ids were made too slowly to share milliseconds.");

        // As the database orders them: by their text, which starts with the time.
        var texts = ids.Select(id => id.ToString()).ToList();
        Assert.Equal(texts.Order(StringComparer.Ordinal), texts);
        Assert.Equal(count, texts.Distinct().Count());
        Assert.All(ids, id => Assert.Equal(7, id.Version));
    }

    [Fact]
    public async Task The_events_of_one_transaction_are_read_back_in_the_order_they_were_recorded_in()
    {
        const int count = 100;

        using var application = new AccountsApplication();
        var user = await application.RegisterVerifiedAsync(Ada);
        var cancellationToken = TestContext.Current.CancellationToken;

        // One after the other in one transaction, each with the number it was recorded as for
        // its time.
        await using (var context = await application.Contexts.CreateDbContextAsync(cancellationToken))
        await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            for (var number = 0; number < count; number++)
            {
                await AuditTrail.RecordAsync(
                    context,
                    user.Id,
                    AuditAction.SignedIn,
                    AuditTarget.Account(user.Id),
                    Morning.AddSeconds(number),
                    AuditOrigin.Unknown,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        // Read by id, they are in that order, and no two share an id.
        var events = StoredAuditEvent.All(application.Database);
        Assert.Equal(Enumerable.Range(0, count).Select(number => Morning.AddSeconds(number)), events.Select(recorded => recorded.OccurredAt));
        Assert.Equal(count, events.Select(recorded => recorded.Id).Distinct().Count());
        Assert.All(events, recorded => Assert.Equal(7, recorded.Id.Version));
    }

    [Fact]
    public async Task No_event_holds_anything_that_a_request_brought()
    {
        const string wrongPassword = "WRONG-PASSWORD-MARKER";

        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, host: host => host.From(From));
        var (user, app, recoveryCodes) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();

        // Every kind of action that Accounts records, with everything a request can bring: a
        // password that is right and one that is not, codes of both kinds, and a reset link.
        await client.SignInAsync(Ada, wrongPassword);
        await client.SignInAsync(Ada);
        var signInCode = app.CodeAt(clock.GetUtcNow());
        await client.EnterCodeAsync(signInCode);

        clock.Set(clock.GetUtcNow().AddMinutes(1));
        await client.ConfirmIdentityAsync(code: recoveryCodes[0]);

        await client.SubmitAsync("/forgot-password", "forgot-password", ("Email", Ada));
        var link = AccountsApplication.LinkIn(application.Email.Sent[^1]);
        var resetToken = AccountsApplication.TokenIn(application.Email.Sent[^1]);
        await client.SubmitAsync(link.PathAndQuery, "reset-password", ("Password", NewPassword));

        var events = StoredAuditEvent.All(application.Database);
        Assert.Equal(
            ["PasswordChanged", "Reauthenticated", "SignedIn", "TwoFactorRecoveryCodeUsed", "TwoFactorTurnedOn"],
            events.Select(recorded => recorded.Action).Distinct().Order());

        // What an event says is one of a fixed set of actions, about an account by its id, from
        // an address of the network.
        Assert.All(events, recorded =>
        {
            Assert.Contains(recorded.Action, Enum.GetNames<AuditAction>());
            Assert.Matches(TargetOfAnAccount(), recorded.Target);
            Assert.Equal($"user:{user.Id}", recorded.Target);
            Assert.True(IPAddress.TryParse(recorded.Origin, out _), $"The origin is not an address: {recorded.Origin}");
        });

        // The rows, as the database hands them to whoever reads them, hold nothing else.
        var rows = string.Join(
            Environment.NewLine,
            application.Database.Column("SELECT row_to_json(stored)::text FROM audit_events stored"));

        Assert.DoesNotContain(AccountsApplication.Password, rows);
        Assert.DoesNotContain(wrongPassword, rows);
        Assert.DoesNotContain(NewPassword, rows);
        Assert.DoesNotContain($"\"{signInCode}\"", rows);
        Assert.DoesNotContain(resetToken, rows);
        Assert.DoesNotContain(app.KeyText, rows);
        Assert.All(recoveryCodes, code => Assert.DoesNotContain(code, rows));
        Assert.DoesNotContain("example.com", rows);
    }

    [Fact]
    public async Task The_log_tells_of_these_actions_in_its_own_words_without_their_names_and_without_the_origin()
    {
        using var application = new AccountsApplication(host: host => host.From(From));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.SubmitAsync("/forgot-password", "forgot-password", ("Email", Ada));
        await client.SubmitAsync(
            AccountsApplication.LinkIn(application.Email.Sent[^1]).PathAndQuery, "reset-password", ("Password", NewPassword));

        // The three are in the table.
        Assert.Equal(
            ["SignedIn", "Reauthenticated", "PasswordChanged"],
            StoredAuditEvent.All(application.Database).Select(recorded => recorded.Action));

        // The log is not the audit trail: it says what happened in its own words, by the id of
        // the account, and does not hold where a request came from.
        var log = application.LogText();
        Assert.Contains("signed in", log);
        Assert.Contains("confirmed who they are", log);
        Assert.Contains("set a new password with a reset link", log);

        Assert.All(Enum.GetNames<AuditAction>(), action => Assert.DoesNotContain(action, log));
        Assert.DoesNotContain(From, log);
        Assert.DoesNotContain("example.com", log);
    }

    [GeneratedRegex("^user:[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$")]
    private static partial Regex TargetOfAnAccount();
}
