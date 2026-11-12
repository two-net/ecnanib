using Ecnanib.Platform.Persistence;

namespace Ecnanib.Accounts;

/// <summary>
/// Writes the audit trail: the record in the database of security- and money-relevant actions,
/// which is kept apart from the logs (NFR-LOG-7). It is the one way an event is recorded, for
/// Accounts and for every component that may use it. See docs/accounts.md.
/// </summary>
/// <remarks>
/// Nothing here takes text. The action is one of a fixed set, the target is made from an id, and
/// the origin is read from a connection. So an event cannot hold a secret, whatever records it.
/// </remarks>
public static class AuditTrail
{
    // The millisecond of the id that was handed out last.
    private static long _lastMillisecond;

    /// <summary>
    /// Records an action inside the transaction of whoever does it, so that the event is stored
    /// when the action is, and not when it is not.
    /// </summary>
    /// <remarks>
    /// It saves the context: the statements that update and delete rows directly do not write
    /// what the context has been handed, so the event is written now and not with a later save.
    /// </remarks>
    /// <param name="context">The context that holds the transaction.</param>
    /// <param name="userId">The user who did it.</param>
    /// <param name="action">What was done.</param>
    /// <param name="target">What it was done to.</param>
    /// <param name="occurredAt">When it was done.</param>
    /// <param name="origin">Where the request came from.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="InvalidOperationException">
    /// The context holds no transaction. An event that is written on its own would stand when
    /// the action it records fails.
    /// </exception>
    public static async Task RecordAsync(
        EcnanibDbContext context,
        Guid userId,
        AuditAction action,
        AuditTarget target,
        DateTimeOffset occurredAt,
        AuditOrigin origin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(origin);

        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "An audit event is recorded inside the transaction of the action it records, and this context holds none.");
        }

        context.Set<AuditEvent>().Add(AuditEvent.Of(NextId(), userId, action, target, occurredAt, origin));

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The id of the next event. An id starts with a time in milliseconds, and each one that is
    /// handed out is of a later millisecond than the one before. So the ids of the events that
    /// this process records are in the order the events were recorded in, also for two events
    /// of one transaction, which are recorded within the same millisecond.
    /// </summary>
    private static Guid NextId()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        long last;
        long next;
        do
        {
            last = Volatile.Read(ref _lastMillisecond);
            next = Math.Max(now, last + 1);
        }
        while (Interlocked.CompareExchange(ref _lastMillisecond, next, last) != last);

        return Guid.CreateVersion7(DateTimeOffset.FromUnixTimeMilliseconds(next));
    }
}
