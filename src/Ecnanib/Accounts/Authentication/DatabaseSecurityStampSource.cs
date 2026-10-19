using Ecnanib.Database;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Accounts.Authentication;

internal sealed class DatabaseSecurityStampSource : ISecurityStampSource
{
    private readonly IDbContextFactory<EcnanibDbContext> _factory;

    public DatabaseSecurityStampSource(IDbContextFactory<EcnanibDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<string?> FindAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.SecurityStamp)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
