using Ecnanib.Kernel;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Notifications;

/// <summary>
/// Emails what's new each minute, one message per user, which is the throttle: one event can fire for
/// many agents, and a storm becomes one email (FR-NTF-02). Only verified addresses, and only the types
/// each user takes. Rows are cleared before the message goes, so a failed write can never send the
/// same email every minute. It never logs an address or a subject. See Notifications in
/// docs/architecture.md.
/// </summary>
internal sealed class NotificationEmailJob(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    IEmailVerification email,
    IEmailRecipients recipients,
    IEmailSender sender,
    NotificationEmails emails,
    ILogger<NotificationEmailJob> logger) : IRecurringJob
{
    public const string JobName = "notification-email";

    /// <summary>
    /// Each message is a send-email run of its own, with its own SMTP connection, so this bounds how many
    /// start at once. The rest wait for the next run, oldest first.
    /// </summary>
    public const int UsersPerRun = 50;

    /// <summary>Older than this isn't news, so after an outage it stays in the app and isn't emailed.</summary>
    public static readonly TimeSpan Cutoff = TimeSpan.FromHours(24);

    public string Name => JobName;

    /// <summary>With email off there's nothing to send, so it never runs, and adds no row a minute to job_runs.</summary>
    public JobSchedule Schedule { get; } = email.IsEnabled ? JobSchedule.Every(TimeSpan.FromMinutes(1)) : new Never();

    public TimeSpan Timeout { get; } = TimeSpan.FromMinutes(1);

    public async Task RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        // Rows raised while email was on stay due, and the cutoff deals with them if it comes back on.
        if (!email.IsEnabled)
        {
            return;
        }

        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var oldest = now - Cutoff;

        var expired = await database.Set<Notification>()
            .Where(row => row.EmailDue && row.CreatedAt < oldest)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.EmailDue, false), cancellationToken);

        var userIds = await database.Set<Notification>()
            .Where(row => row.EmailDue)
            .GroupBy(row => row.UserId)
            .OrderBy(group => group.Min(row => row.Id))
            .Select(group => group.Key)
            .Take(UsersPerRun)
            .ToListAsync(cancellationToken);

        var rows = userIds.Count == 0
            ? []
            : await database.Set<Notification>()
                .AsNoTracking()
                .Where(row => row.EmailDue && userIds.Contains(row.UserId))
                .ToListAsync(cancellationToken);

        var found = (await recipients.FindAsync(userIds, cancellationToken)).ToDictionary(recipient => recipient.UserId);
        var sent = 0;

        foreach (var due in rows.GroupBy(row => row.UserId))
        {
            context.Stopping.ThrowIfCancellationRequested();

            using var scope = logger.BeginScope(new Dictionary<string, object> { [LogProperties.UserId] = due.Key });

            var recipient = found.GetValueOrDefault(due.Key);
            var wanted = recipient is null
                ? []
                : due.Where(row => recipient.Types.Contains(row.Type)).OrderByDescending(row => row.Id).ToList();

            await ClearAsync(database, [.. due.Select(row => row.Id)], [.. wanted.Select(row => row.Id)], now, cancellationToken);

            if (recipient is null || wanted.Count == 0)
            {
                continue;
            }

            sender.Send(emails.For(recipient.Address, [.. wanted.Select(Entry)]));
            sent++;

            logger.LogInformation("Emailed {NotificationCount} notifications", wanted.Count);
        }

        context.Details = $"{sent} emails, {rows.Count} notifications, {expired} expired";
    }

    /// <summary>In one transaction, so a row is never marked emailed while it's still due.</summary>
    private static async Task ClearAsync(
        EcnanibDbContext database,
        List<long> due,
        List<long> emailed,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        await database.Set<Notification>()
            .Where(row => due.Contains(row.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.EmailDue, false), cancellationToken);

        if (emailed.Count > 0)
        {
            await database.Set<Notification>()
                .Where(row => emailed.Contains(row.Id))
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.EmailedAt, now), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>The runner wakes now and then to ask again, and the answer stays the same until a restart.</summary>
    private sealed class Never : JobSchedule
    {
        public override DateTimeOffset NextRun(DateTimeOffset? lastStart, DateTimeOffset now) => DateTimeOffset.MaxValue;
    }

    private static NotificationEntry Entry(Notification row)
    {
        var payload = NotificationPayload.FromJson(row.Payload);

        return new NotificationEntry(row.Type, payload.Subject, payload.Demo, row.Link, row.CreatedAt);
    }
}
