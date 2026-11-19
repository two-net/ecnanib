using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecnanib.Platform.Persistence;

/// <summary>
/// Reads that must agree with each other: several statements that see the database as it was at
/// one moment. See "Reads that agree" in docs/persistence.md.
/// </summary>
/// <remarks>
/// <para>
/// Each statement of an ordinary transaction sees what was committed before the statement
/// began, so two statements can stand on either side of a write of another transaction: a sum
/// that is read first and a row that is read second may then count the same thing twice, or
/// not at all. Inside a snapshot every statement sees what was committed before the first of
/// them, and nothing later.
/// </para>
/// <para>
/// It is for reading. A snapshot that writes may be refused by the database when another
/// transaction wrote the same row meanwhile: what writes takes the lock that guards its rows,
/// in a transaction of its own.
/// </para>
/// </remarks>
public static class Snapshots
{
    /// <summary>
    /// Begins a transaction whose statements all read the database as it was when the first of
    /// them began. Whoever asks disposes of it when the reads are over: it holds no lock that
    /// keeps a writer waiting.
    /// </summary>
    /// <param name="context">The context whose reads are to agree.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    public static Task<IDbContextTransaction> BeginSnapshotAsync(this EcnanibDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
    }
}
