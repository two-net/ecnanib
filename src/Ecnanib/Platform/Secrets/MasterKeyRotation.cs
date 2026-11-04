using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Platform.Secrets;

internal sealed class MasterKeyRotation(IDbContextFactory<EcnanibDbContext> contextFactory, MasterKeys keys) : IMasterKeyRotation
{
    public async Task<MasterKeyProgress> GetProgressAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var active = keys.Active.Id;
        var since = await context.Set<MasterKeyActivation>()
            .Where(activation => activation.KeyId == active)
            .OrderByDescending(activation => activation.Id)
            .Select(activation => (DateTimeOffset?)activation.ActivatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new MasterKeyProgress(active, since, await SecretStore.CountByKeyAsync(context, cancellationToken));
    }
}
