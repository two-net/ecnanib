using System.Globalization;
using System.Text;
using Ecnanib.Accounts;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Notifications;

/// <summary>
/// Where the pages of the notifications are, which an email links to. The Web UI serves its
/// pages at these paths: Notifications may not use the Web UI, so the paths are here.
/// </summary>
public static class NotificationPaths
{
    /// <summary>The path of the page of the notifications of a user (FR-NTF-1).</summary>
    public const string List = "/notifications";

    /// <summary>The path of the page a user opts in to email for critical events on, and out of it (FR-NTF-3).</summary>
    public const string Email = "/account/notifications";
}

/// <summary>
/// Sends the notifications about critical events by email, to the users who opted in
/// (FR-NTF-3). See "Email for critical events" in docs/notifications.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>No email is sent without the opt-in.</b> A notification is marked for an email when it is
/// raised, for a user who has opted in by then: see <see cref="StoredNotifications"/>. As it is
/// sent, the user is asked about once more: one who opted out since, or whose address is not
/// verified, is sent nothing, and what was marked for them no longer is.
/// </para>
/// <para>
/// <b>It is sent apart from what raised it.</b> A notification is raised by a cycle, or by what
/// settles an order, and the email delivery service may take long to answer, or not answer. So
/// the notification is stored at once, and a job sends what is marked, every minute.
/// </para>
/// <para>
/// <b>One message for a user,</b> with everything that is marked for them by then, oldest first.
/// Where the email delivery service does not take a message, the run ends: it is asked again
/// with the next run, and a notification that is still marked a day after it was raised is given
/// up. A message that was taken and could not be marked as sent is sent again, so a user may be
/// told twice, and never not at all.
/// </para>
/// <para>
/// The log names the user by id, and holds neither the address nor what a notification says.
/// </para>
/// </remarks>
internal sealed class NotificationEmails(
    IDbContextFactory<EcnanibDbContext> contexts,
    IEmailAdapter email,
    ApplicationSettings application,
    TimeProvider clock,
    ILogger<NotificationEmails> logger)
{
    /// <summary>How long after it was raised a notification is no longer sent by email: what it tells of is old by then.</summary>
    public static readonly TimeSpan GivenUpAfter = TimeSpan.FromDays(1);

    /// <summary>How many notifications a run sends at most. The rest are sent with the next runs.</summary>
    public const int Most = 200;

    /// <summary>Sends what is marked for an email, to each user in one message.</summary>
    /// <returns>How many messages the email delivery service took.</returns>
    public async Task<int> SendDueAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        List<Notification> pending;

        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        {
            // Across users, oldest first: a job has no user who is signed in, and takes the
            // owner of each from its row. Most runs find none.
            pending = await context.Set<Notification>()
                .AsNoTracking()
                .Where(notification => notification.EmailPending)
                .OrderBy(notification => notification.CreatedAt)
                .ThenBy(notification => notification.Id)
                .Take(Most)
                .ToListAsync(cancellationToken);
        }

        var sent = 0;

        // Each user in turn, in the order of the oldest that is marked for them.
        foreach (var of in pending.GroupBy(notification => notification.UserId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var userId = of.Key;
            var old = of.Where(notification => notification.CreatedAt <= now - GivenUpAfter).ToList();
            var due = of.Except(old).ToList();

            if (old.Count > 0)
            {
                await UnmarkAsync(userId, old, cancellationToken);

                logger.LogWarning(
                    "{NotificationCount} notifications of user {UserId} were not sent by email within {Hours} hours, and are no longer sent",
                    old.Count,
                    userId,
                    (int)GivenUpAfter.TotalHours);
            }

            if (due.Count == 0)
            {
                continue;
            }

            // Asked as it is sent: the choice of the user as it is now, and their address when it is verified.
            if (await AddressAsync(userId, cancellationToken) is not { } address)
            {
                await UnmarkAsync(userId, due, cancellationToken);

                logger.LogInformation(
                    "{NotificationCount} notifications of user {UserId} are not sent by email: the user has not opted in, or their address is not verified",
                    due.Count,
                    userId);

                continue;
            }

            try
            {
                await email.SendAsync(MessageOf(address, due), cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // The email delivery service did not take it, and would not take the next one:
                // what is marked stays so, and the next run asks again.
                logger.LogError(
                    error,
                    "{NotificationCount} notifications of user {UserId} could not be sent by email. They are sent with a later run",
                    due.Count,
                    userId);

                return sent;
            }

            // The service has taken the message: it is not sent again, whatever stops the run now.
            await MarkSentAsync(userId, due, now, CancellationToken.None);
            sent++;

            logger.LogInformation("{NotificationCount} notifications of user {UserId} were sent by email", due.Count, userId);
        }

        return sent;
    }

    /// <summary>
    /// The address of a user who is sent email for critical events as things are now: they
    /// opted in, and their address is verified. Null for any other user.
    /// </summary>
    private async Task<string?> AddressAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var optIns = context.Set<EmailOptIn>().AsNoTracking();

        return await context.Set<User>()
            .AsNoTracking()
            .Where(user => user.Id == userId && user.EmailVerifiedAt != null && optIns.Any(optIn => optIn.UserId == user.Id))
            .Select(user => user.Email)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task UnmarkAsync(Guid userId, IReadOnlyList<Notification> notifications, CancellationToken cancellationToken)
    {
        var ids = notifications.Select(notification => notification.Id).ToList();

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        await context.Set<Notification>()
            .OwnedBy(userId)
            .Where(notification => ids.Contains(notification.Id) && notification.EmailPending)
            .ExecuteUpdateAsync(row => row.SetProperty(notification => notification.EmailPending, false), cancellationToken);
    }

    private async Task MarkSentAsync(Guid userId, IReadOnlyList<Notification> notifications, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var ids = notifications.Select(notification => notification.Id).ToList();

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        await context.Set<Notification>()
            .OwnedBy(userId)
            .Where(notification => ids.Contains(notification.Id) && notification.EmailPending)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(notification => notification.EmailPending, false)
                    .SetProperty(notification => notification.EmailedAt, at),
                cancellationToken);
    }

    /// <summary>The message that tells a user of the critical events that are marked for them, oldest first.</summary>
    private EmailMessage MessageOf(string address, IReadOnlyList<Notification> notifications)
    {
        var subject = notifications.Count == 1
            ? $"{application.Name}: {NotificationTypes.Describe(notifications[0].Type)}"
            : $"{application.Name}: {notifications.Count} critical events";

        var body = new StringBuilder();

        body.Append(CultureInfo.InvariantCulture, $"{application.Name} has something to tell you that needs you:\n");

        foreach (var notification in notifications)
        {
            // What it is about and when, and then what it says, as the page of the notifications says it.
            body.Append(CultureInfo.InvariantCulture, $"\n{NotificationTypes.Describe(notification.Type)}, {At(notification.CreatedAt)}\n");
            body.Append(notification.Content).Append('\n');
        }

        body.Append("\nAll your notifications are here:\n\n");
        body.Append(new Uri(application.PublicUrl, NotificationPaths.List).AbsoluteUri).Append('\n');
        body.Append("\nYou get this message because you chose to be sent email for critical events. You turn it off here:\n\n");
        body.Append(new Uri(application.PublicUrl, NotificationPaths.Email).AbsoluteUri).Append('\n');

        return new EmailMessage(address, subject, body.ToString());
    }

    // A moment as a person reads it, to the minute, as the pages write it: 15 November 2026, 09:20 UTC.
    private static string At(DateTimeOffset time) =>
        time.UtcDateTime.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture) + " UTC";
}

/// <summary>
/// Sends the notifications about critical events by email, every minute (FR-NTF-3). See
/// <see cref="NotificationEmails"/> and docs/notifications.md.
/// </summary>
/// <remarks>
/// A run that is done again finds what it sent no longer marked. Most runs find nothing, and
/// ask the email delivery service nothing.
/// </remarks>
internal sealed class NotificationEmailJob(NotificationEmails emails) : IBackgroundJob
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "notification-email";

    /// <summary>The time between two runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.Every(Interval);

    public async Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        await emails.SendDueAsync(cancellationToken);
    }
}
