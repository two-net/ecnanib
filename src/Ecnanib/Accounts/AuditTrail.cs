using Ecnanib.Platform.Persistence;

namespace Ecnanib.Accounts;

/// <summary>
/// Writes the audit trail: the record in the database of security- and money-relevant actions,
/// which is kept apart from the logs (NFR-LOG-7). See docs/accounts.md.
/// </summary>
internal static class AuditTrail
{
    /// <summary>
    /// Records an action inside the transaction of whoever does it, so that the event is stored
    /// when the action is, and not when it is not.
    /// </summary>
    /// <param name="context">The context that holds the transaction.</param>
    /// <param name="userId">The user who did it.</param>
    /// <param name="action">What was done.</param>
    /// <param name="target">What it was done to. See <see cref="Account"/>.</param>
    /// <param name="occurredAt">When it was done.</param>
    /// <param name="origin">Where the request came from. See <see cref="OriginOf"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static async Task RecordAsync(
        EcnanibDbContext context,
        Guid userId,
        AuditAction action,
        string target,
        DateTimeOffset occurredAt,
        string? origin,
        CancellationToken cancellationToken)
    {
        context.Set<AuditEvent>().Add(new AuditEvent(Guid.CreateVersion7(), userId, action, target, occurredAt, origin));

        // Now, and not with a later save: the statements that update and delete rows directly
        // do not write what the context has been handed.
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>An account as the target of an action.</summary>
    public static string Account(Guid userId) => $"user:{userId}";

    /// <summary>
    /// The address a request came from, as the connection knows it, or null when it does not.
    /// Behind a proxy it is the address of the browser only when the application is told to
    /// believe the proxy's headers. See docs/security.md.
    /// </summary>
    public static string? OriginOf(HttpContext request)
    {
        var address = request.Connection.RemoteIpAddress;
        if (address is null)
        {
            return null;
        }

        // An IPv4 address that arrived on an IPv6 socket is written as the IPv4 address it is.
        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }
}
