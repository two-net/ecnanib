using Ecnanib.Accounts;
using Ecnanib.Platform.Persistence;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;

namespace Ecnanib.Tests.Retention;

/// <summary>
/// The events of the audit trail are kept for the days of <c>Retention:AuditDays</c>, and a
/// scheduled job deletes the older ones in small batches, found by an index (NFR-DAT-5). See
/// docs/retention.md.
/// </summary>
public class AuditRetentionTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string Area = "Ecnanib.Accounts.AuditRetentionJob";

    // What the file of these tests keeps an event for, which is not what the tracked file says.
    private const int KeptDays = 365;

    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 20, 0, TimeSpan.Zero);

    [Fact]
    public void The_audit_events_that_are_kept_no_longer_are_deleted_by_a_job_every_hour()
    {
        using var application = Application();
        var job = application.AuditRetention;

        // The application registers it with the background work. The scheduler of a test does
        // not run it: see EcnanibApplicationFactory.
        Assert.Equal("audit-retention", job.Name);
        Assert.Equal("every 01:00:00", job.Schedule.ToString());
        Assert.Equal(Now.AddHours(1), job.Schedule.NextRun(Now, Now.AddMinutes(30)));
    }

    [Fact]
    public async Task An_audit_event_is_deleted_when_it_is_older_than_audit_events_are_kept()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var limit = Now.AddDays(-KeptDays);

        // Of every user, whatever it records: an event goes by its age alone.
        var recent = Give(application, ada.Id, "SignedIn", Now.AddHours(-1));
        var atTheLimit = Give(application, ada.Id, "KeyAdded", limit);
        var past = Give(application, ada.Id, "KeyAdded", limit.AddSeconds(-1));
        var ofLongAgo = Give(application, ada.Id, "SignedIn", limit.AddDays(-400));
        var ofGrace = Give(application, grace.Id, "SignedIn", limit.AddDays(-1));
        var recentOfGrace = Give(application, grace.Id, "SignedIn", Now.AddDays(-KeptDays + 1));

        await application.DeleteAgedAuditEventsAsync();

        // One that is exactly as old as they are kept stays, and one that is a second older goes.
        var left = StoredAuditEvent.All(application.Database).Select(stored => stored.Id).ToList();

        Assert.Equal(new[] { recent, atTheLimit, recentOfGrace }.Order(), left.Order());
        Assert.DoesNotContain(past, left);
        Assert.DoesNotContain(ofLongAgo, left);
        Assert.DoesNotContain(ofGrace, left);

        // The job says how many went, once, and records nothing in the trail for it.
        var summary = Assert.Single(application.Summaries(Area));

        Assert.Equal("{Events} events of the audit trail were deleted, which were kept no longer", summary.Template);
        Assert.Equal("Information", summary.Level);
        Assert.Equal(3, summary.Count("Events"));

        // A run that finds nothing deletes nothing, and says nothing.
        await application.DeleteAgedAuditEventsAsync();

        Assert.Equal(left, StoredAuditEvent.All(application.Database).Select(stored => stored.Id));
        Assert.Single(application.Summaries(Area));
    }

    [Fact]
    public async Task The_audit_events_are_deleted_in_small_batches()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var recent = Give(application, ada.Id, "SignedIn", Now.AddHours(-1));

        // A full batch of them and some more, each of a moment of its own, all older than they are kept.
        const int aged = AgedRows.BatchSize + 100;

        application.Database.Execute(
            $"""
            INSERT INTO audit_events (id, user_id, action, target, occurred_at)
            SELECT gen_random_uuid(), '{ada.Id}', 'SignedIn', 'user:{ada.Id}',
                   '{Now.AddDays(-KeptDays):O}'::timestamptz - make_interval(secs => n)
            FROM generate_series(1, {aged}) AS n
            """);

        await application.DeleteAgedAuditEventsAsync();

        Assert.Equal([recent], StoredAuditEvent.All(application.Database).Select(stored => stored.Id));
        Assert.Equal(aged, Assert.Single(application.Summaries(Area)).Count("Events"));

        // A full batch and what was left, each a statement of its own with a limit.
        var statements = application.DeletesFrom("audit_events");

        Assert.Equal(2, statements.Count);
        Assert.All(statements, statement => Assert.Contains("LIMIT", statement));
    }

    [Fact]
    public async Task The_audit_events_of_every_user_are_found_by_when_they_happened()
    {
        using var application = Application();

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        Assert.Contains(
            context.Model.FindEntityType(typeof(AuditEvent))!.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual([nameof(AuditEvent.OccurredAt)]));

        // The database has it as the model says it, of every row: the job asks by no owner.
        Assert.EndsWith(
            "USING btree (occurred_at)",
            application.IndexDefinition("audit_events", "ix_audit_events_occurred_at"));
    }

    /// <summary>The application at <see cref="Now"/>, which keeps an event for <see cref="KeptDays"/> days.</summary>
    private static AccountsApplication Application() =>
        new(clock: new TestClock(Now), settings: file => file.With("Retention:AuditDays", KeptDays));

    /// <summary>
    /// Gives a user an event of a moment, stored as the trail stores one. The trail itself
    /// records what happens now, and takes no moment of the past.
    /// </summary>
    /// <returns>The id of the event, which is of its moment as the ids of the trail are.</returns>
    private static Guid Give(AccountsApplication application, Guid userId, string action, DateTimeOffset at)
    {
        var id = Guid.CreateVersion7(at);

        application.Database.Execute(
            $"""
            INSERT INTO audit_events (id, user_id, action, target, occurred_at)
            VALUES ('{id}', '{userId}', '{action}', 'user:{userId}', '{at:O}')
            """);

        return id;
    }
}
