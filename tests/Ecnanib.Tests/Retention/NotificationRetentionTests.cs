using Ecnanib.Notifications;
using Ecnanib.Platform.Persistence;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Retention;

/// <summary>
/// Notifications are kept for the days of <c>Retention:NotificationDays</c>, and a scheduled job
/// deletes the older ones in small batches, found by an index (NFR-DAT-5). See docs/retention.md.
/// </summary>
public class NotificationRetentionTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string Area = "Ecnanib.Notifications.NotificationRetentionJob";

    // What the file of these tests keeps a notification for, which is not what the tracked file says.
    private const int KeptDays = 45;

    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 20, 0, TimeSpan.Zero);

    [Fact]
    public void The_notifications_that_are_kept_no_longer_are_deleted_by_a_job_every_hour()
    {
        using var application = Application();
        var job = application.NotificationRetention;

        // The application registers it with the background work. The scheduler of a test does
        // not run it: see EcnanibApplicationFactory.
        Assert.Equal("notification-retention", job.Name);
        Assert.Equal("every 01:00:00", job.Schedule.ToString());
        Assert.Equal(Now.AddHours(1), job.Schedule.NextRun(Now, Now.AddMinutes(30)));
    }

    [Fact]
    public async Task A_notification_is_deleted_when_it_is_older_than_notifications_are_kept()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var limit = Now.AddDays(-KeptDays);

        // Of every user, read or not: a notification goes by its age alone.
        var recent = await application.GiveNotificationAsync(ada.Id, agentId: null, Now.AddHours(-1));
        var atTheLimit = await application.GiveNotificationAsync(ada.Id, agentId: null, limit);
        var past = await application.GiveNotificationAsync(ada.Id, agentId: null, limit.AddSeconds(-1));
        var read = await application.GiveNotificationAsync(ada.Id, agentId: null, limit.AddDays(-30));
        var ofGrace = await application.GiveNotificationAsync(grace.Id, agentId: null, limit.AddDays(-1));
        var recentOfGrace = await application.GiveNotificationAsync(grace.Id, agentId: null, Now.AddDays(-KeptDays + 1));

        application.Database.Execute($"UPDATE notifications SET is_read = true WHERE id = '{read}'");

        await application.DeleteAgedNotificationsAsync();

        // One that is exactly as old as they are kept stays, and one that is a second older goes.
        Assert.Equal(
            new[] { recent, atTheLimit, recentOfGrace }.Order(),
            (await StoredAsync(application)).Order());
        Assert.DoesNotContain(past, await StoredAsync(application));
        Assert.DoesNotContain(ofGrace, await StoredAsync(application));

        // The job says how many went, once.
        var summary = Assert.Single(application.Summaries(Area));

        Assert.Equal("{Notifications} notifications were deleted, which were kept no longer", summary.Template);
        Assert.Equal("Information", summary.Level);
        Assert.Equal(3, summary.Count("Notifications"));

        // A run that finds nothing deletes nothing, and says nothing.
        await application.DeleteAgedNotificationsAsync();

        Assert.Equal(3, (await StoredAsync(application)).Count);
        Assert.Single(application.Summaries(Area));
    }

    [Fact]
    public async Task The_notifications_are_deleted_in_small_batches()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var recent = await application.GiveNotificationAsync(ada.Id, agentId: null, Now.AddHours(-1));

        // More than two batches of them, each of a moment of its own, all older than they are kept.
        const int aged = (2 * AgedRows.BatchSize) + 201;

        application.Database.Execute(
            $"""
            INSERT INTO notifications (id, user_id, type, agent_id, mode, content, is_read, created_at, email_pending, emailed_at)
            SELECT gen_random_uuid(), '{ada.Id}', 'ProtectionFailure', NULL, NULL, 'Of long ago', false,
                   '{Now.AddDays(-KeptDays):O}'::timestamptz - make_interval(secs => n), false, NULL
            FROM generate_series(1, {aged}) AS n
            """);

        await application.DeleteAgedNotificationsAsync();

        Assert.Equal([recent], await StoredAsync(application));
        Assert.Equal(aged, Assert.Single(application.Summaries(Area)).Count("Notifications"));

        // Two full batches and what was left, each a statement of its own with a limit. The
        // last one was not full, so nothing was asked after it.
        var statements = application.DeletesFrom("notifications");

        Assert.Equal(3, statements.Count);
        Assert.All(statements, statement => Assert.Contains("LIMIT", statement));
    }

    [Fact]
    public async Task The_notifications_of_every_user_are_found_by_when_they_were_raised()
    {
        using var application = Application();

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        // The model has two indexes of the column: this one, of every notification, and the
        // one of those that wait for an email.
        Assert.Equal(
            2,
            context.Model.FindEntityType(typeof(Notification))!.GetIndexes().Count(index =>
                index.Properties.Select(property => property.Name).SequenceEqual([nameof(Notification.CreatedAt)])));

        // The database has it as the model says it, of every row: the job asks by no owner.
        var definition = application.IndexDefinition("notifications", "ix_notifications_created_at_all");

        Assert.EndsWith("USING btree (created_at)", definition);

        // The one of those that wait for an email is as it was.
        Assert.EndsWith(
            "USING btree (created_at) WHERE email_pending",
            application.IndexDefinition("notifications", "ix_notifications_created_at"));
    }

    /// <summary>The application at <see cref="Now"/>, which keeps a notification for <see cref="KeptDays"/> days.</summary>
    private static AccountsApplication Application() =>
        new(clock: new TestClock(Now), settings: file => file.With("Retention:NotificationDays", KeptDays));

    /// <summary>The ids of the notifications that are stored, of every user.</summary>
    private static async Task<IReadOnlyList<Guid>> StoredAsync(AccountsApplication application)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        return await context.Set<Notification>()
            .AsNoTracking()
            .Select(notification => notification.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
    }
}
