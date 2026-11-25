using System.Security.Claims;
using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// What a user said on the setup checklist of a first-time user is stored with their account:
/// that they reviewed decisions, and that they considered Live, each with when (FR-UI-4). The
/// first time stands, a mark is of the user of the request alone (NFR-SEC-3), and an account
/// from before that switched an agent to Live has considered Live. See "The setup checklist" in
/// docs/dashboard.md.
/// </summary>
public class SetupMarkTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";
    private const string Linus = "linus@example.com";

    private const string MarkedTemplate = "User {UserId} marked {SetupMark} on the setup checklist";

    private static readonly DateTimeOffset Morning = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_mark_is_stored_for_the_user_of_the_request_as_of_now_and_the_first_time_stands()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var marks = application.SetupMarks;

        // A new account said nothing.
        Assert.Equal(new SetupMarks(null, null), await marks.ReadAsync(RequestOf(ada.Id)));
        Assert.False((await marks.ReadAsync(RequestOf(ada.Id))).Both);

        // Ada says that she reviewed decisions, at five past.
        clock.Set(Morning.AddMinutes(5));
        await marks.SetAsync(RequestOf(ada.Id), SetupMark.DecisionsReviewed);

        Assert.Equal(new SetupMarks(Morning.AddMinutes(5), null), await marks.ReadAsync(RequestOf(ada.Id)));

        // She says so again later: what was said first stands, and nothing is written.
        clock.Set(Morning.AddMinutes(10));
        await marks.SetAsync(RequestOf(ada.Id), SetupMark.DecisionsReviewed);

        Assert.Equal(new SetupMarks(Morning.AddMinutes(5), null), await marks.ReadAsync(RequestOf(ada.Id)));

        // And that she considered Live: both are said, each with its own time.
        await marks.SetAsync(RequestOf(ada.Id), SetupMark.LiveConsidered);

        var both = await marks.ReadAsync(RequestOf(ada.Id));

        Assert.Equal(new SetupMarks(Morning.AddMinutes(5), Morning.AddMinutes(10)), both);
        Assert.True(both.Both);

        // The marks are of her account alone: no request names a user, and Grace said nothing.
        Assert.Equal(new SetupMarks(null, null), await marks.ReadAsync(RequestOf(grace.Id)));

        // The log says each mark once, by the id of its user: the second time changed nothing.
        var written = application.Events().Where(written => written.Template == MarkedTemplate).ToList();

        Assert.Equal(
            [(ada.Id.ToString(), "DecisionsReviewed"), (ada.Id.ToString(), "LiveConsidered")],
            written.Select(written => (written.Text("UserId")!, written.Text("SetupMark")!)));

        // A mark exposes no key and moves no money: it is in no event of the audit trail.
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action.Contains("Setup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_request_that_is_not_signed_in_reads_and_sets_nothing_and_neither_does_a_mark_that_there_is_not()
    {
        using var application = new AccountsApplication();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var marks = application.SetupMarks;

        await Assert.ThrowsAsync<InvalidOperationException>(() => marks.ReadAsync(new DefaultHttpContext()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => marks.SetAsync(new DefaultHttpContext(), SetupMark.LiveConsidered));

        // A number that names no mark is refused, and stores nothing.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => marks.SetAsync(RequestOf(ada.Id), (SetupMark)7));

        Assert.Equal(new SetupMarks(null, null), await marks.ReadAsync(RequestOf(ada.Id)));

        // An account that was deleted since the request was signed in said nothing, and a mark
        // for it is stored nowhere.
        var gone = Guid.CreateVersion7();

        await marks.SetAsync(RequestOf(gone), SetupMark.DecisionsReviewed);

        Assert.Equal(new SetupMarks(null, null), await marks.ReadAsync(RequestOf(gone)));
    }

    [Fact]
    public async Task A_mark_that_is_recorded_in_a_transaction_is_stored_when_the_transaction_is_and_not_when_it_is_not()
    {
        using var application = new AccountsApplication();
        var ada = await application.RegisterVerifiedAsync(Ada);

        // Whoever does what a mark says records it with their own transaction, as a switch of
        // an agent to Live does. One that is rolled back leaves no mark.
        await using (var context = await application.Contexts.CreateDbContextAsync(Cancellation))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Cancellation);

            Assert.True(await SetupMarking.RecordAsync(context, ada.Id, SetupMark.LiveConsidered, Morning, Cancellation));

            await transaction.RollbackAsync(Cancellation);
        }

        Assert.Equal(new SetupMarks(null, null), await application.SetupMarks.ReadAsync(RequestOf(ada.Id)));

        // One that is committed stores it, and says so. The second time it says that the mark stood.
        await using (var context = await application.Contexts.CreateDbContextAsync(Cancellation))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Cancellation);

            Assert.True(await SetupMarking.RecordAsync(context, ada.Id, SetupMark.LiveConsidered, Morning, Cancellation));
            Assert.False(await SetupMarking.RecordAsync(context, ada.Id, SetupMark.LiveConsidered, Morning.AddHours(1), Cancellation));

            await transaction.CommitAsync(Cancellation);
        }

        Assert.Equal(new SetupMarks(null, Morning), await application.SetupMarks.ReadAsync(RequestOf(ada.Id)));
    }

    [Fact]
    public async Task An_account_from_before_that_switched_an_agent_to_Live_has_considered_Live_since_its_first_switch()
    {
        // A database of the version before, in which an account says nothing of the checklist.
        var database = PostgresServer.CreateDatabase();
        using (var before = database.Context())
        {
            before.GetService<IMigrator>().Migrate("20261124194415_AddMotion");
        }

        var ada = Guid.CreateVersion7();
        var grace = Guid.CreateVersion7();
        var linus = Guid.CreateVersion7();

        foreach (var (id, email) in new[] { (ada, Ada), (grace, Grace), (linus, Linus) })
        {
            database.Execute(
                $"""
                INSERT INTO users (id, email, password_hash, registered_at, email_verification_sets_password, two_factor_failed_codes, theme, motion)
                VALUES ('{id}', '{email}', 'not-a-hash', now(), FALSE, 0, 'System', 'System')
                """);
        }

        // Ada switched an agent to Live twice, and back between. Grace did other things, and
        // never switched one to Live. Linus did nothing that the audit trail holds.
        var agent = Guid.CreateVersion7();

        foreach (var (user, action, at) in new[]
        {
            (ada, "AgentStarted", Morning.AddDays(-9)),
            (ada, "AgentSwitchedToLive", Morning.AddDays(-7)),
            (ada, "AgentSwitchedToDemo", Morning.AddDays(-5)),
            (ada, "AgentSwitchedToLive", Morning.AddDays(-3)),
            (grace, "AgentSwitchedToDemo", Morning.AddDays(-2)),
            (grace, "KeyAdded", Morning.AddDays(-1)),
        })
        {
            database.Execute(
                $"""
                INSERT INTO audit_events (id, user_id, action, target, occurred_at)
                VALUES ('{Guid.CreateVersion7()}', '{user}', '{action}', 'agent:{agent}', '{at:O}')
                """);
        }

        // Starting this version migrates the database.
        using var application = new AccountsApplication(database: database);
        var marks = application.SetupMarks;

        // The first switch of Ada is when she considered Live. Nothing says that anyone
        // reviewed decisions, so nobody did.
        Assert.Equal(new SetupMarks(null, Morning.AddDays(-7)), await marks.ReadAsync(RequestOf(ada)));
        Assert.Equal(new SetupMarks(null, null), await marks.ReadAsync(RequestOf(grace)));
        Assert.Equal(new SetupMarks(null, null), await marks.ReadAsync(RequestOf(linus)));
    }

    /// <summary>A request that is signed in as a user, as the handler of the sessions signs one in.</summary>
    private static DefaultHttpContext RequestOf(Guid userId) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], SessionAuthentication.Scheme)),
    };
}
