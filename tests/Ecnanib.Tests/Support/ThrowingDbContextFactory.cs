using Ecnanib.Database;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Support;

/// <summary>Stands in for the database: every attempt to open a context counts and fails.</summary>
internal sealed class ThrowingDbContextFactory : IDbContextFactory<EcnanibDbContext>
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public EcnanibDbContext CreateDbContext()
    {
        Interlocked.Increment(ref _calls);
        throw new InvalidOperationException("database down");
    }
}
