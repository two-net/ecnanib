using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Catalog;

/// <summary>NFR-SEC-04: what a user's keys can use, and a user's sync, are theirs alone.</summary>
public sealed class CatalogIsolationTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => CatalogApp.Cancellation;

    [Fact]
    public async Task A_user_sees_and_syncs_only_their_own_keys()
    {
        await using var app = new CatalogApp(postgres, observers: false);
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var myKey = await app.AddAsync(mine);
        var theirKey = await app.AddAsync(theirs, CatalogApp.OtherAiKey);
        await app.RunScheduleAsync();
        var theirRequests = app.Ai.WithKey(CatalogApp.OtherAiKey).Count;

        var result = await app.Sync.SyncUserAsync(mine, Cancellation);

        Assert.Equal(new UserSyncResult(UserSyncOutcome.Synced, 1, 0, 1), result);
        Assert.Equal(theirRequests, app.Ai.WithKey(CatalogApp.OtherAiKey).Count);
        Assert.Equal([myKey.Id], (await app.Catalog.ListAccessAsync(mine, Cancellation)).Select(access => access.CredentialId));
        Assert.Equal([theirKey.Id], (await app.Catalog.ListAccessAsync(theirs, Cancellation)).Select(access => access.CredentialId));
        Assert.Empty(await app.Catalog.ListAccessAsync(Guid.NewGuid(), Cancellation));
    }
}
