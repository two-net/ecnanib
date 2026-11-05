using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Agents;

/// <summary>
/// Serialises the writes to one user's agents that could break a rule across rows: the most agents a user
/// may have, unique names, and one agent per exchange key, market and pair (FR-AGT-01, FR-AGT-10). A
/// credential belongs to one user, so every such conflict is within one user, and one lock per user covers
/// them all. It's a PostgreSQL advisory lock for the transaction, so it ends with it, and it doesn't touch
/// <c>users</c>, which belongs to Identity. Two users can share a key, which only makes them wait for each other.
/// </summary>
internal static class AgentLock
{
    /// <summary>The first half of every key, so these locks can't collide with another feature's.</summary>
    private const int Space = 0x41474E54;

    /// <summary>Waits for the user's lock, which the caller's transaction then holds until it ends.</summary>
    /// <exception cref="InvalidOperationException">There's no transaction, which would release the lock at once.</exception>
    public static async Task TakeAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The agent lock needs a transaction to hold it.");
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
