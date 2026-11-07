using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Experience;

/// <summary>
/// Serialises writing a user's metrics with deleting one of their agents, so a build that read an agent's calls just
/// before the agent was deleted can't write them back after. It's a PostgreSQL advisory lock for the transaction,
/// keyed on the user, in a key space of its own.
/// </summary>
internal static class ExperienceLock
{
    /// <summary>The first half of every key, so these locks can't collide with another feature's.</summary>
    private const int Space = 0x45585045;

    /// <summary>Waits for the user's lock, which the caller's transaction then holds until it ends.</summary>
    /// <exception cref="InvalidOperationException">There's no transaction, which would release the lock at once.</exception>
    public static async Task TakeAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The experience lock needs a transaction to hold it.");
        }

        var key = KeyOf(userId);

        await context.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({Space}, {key})", cancellationToken);
    }

    private static int KeyOf(Guid userId)
    {
        Span<byte> bytes = stackalloc byte[16];
        userId.TryWriteBytes(bytes);

        return BitConverter.ToInt32(bytes[..4]) ^ BitConverter.ToInt32(bytes[4..8])
            ^ BitConverter.ToInt32(bytes[8..12]) ^ BitConverter.ToInt32(bytes[12..]);
    }
}
