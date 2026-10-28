using Ecnanib.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ecnanib.Tests.Database.Live;

/// <summary>NFR-SEC-03 in the schema: an agent can reference its own user's exchange key and no other user's, and the reference blocks nothing a user may do.</summary>
[Trait("Category", "Live")]
public class OwnershipConstraintLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public OwnershipConstraintLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAgentMayReferenceItsOwnUsersKeyOnly()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        Guid ownerId, intruderId, keyId;
        await using (var db = _fixture.CreateContext())
        {
            var owner = LiveSeed.NewUser("fk-owner@example.com");
            var intruder = LiveSeed.NewUser("fk-intruder@example.com");
            var key = StoredKeySeed.Protect(new ExchangeKey { User = owner, Label = "main", Environment = Ecnanib.Exchange.Abstractions.ExchangeEnvironment.Production, AllowSpot = true, AllowFutures = true, ProtectedApiKey = [], ProtectedApiSecret = [], Status = KeyStatus.Valid, CreatedAt = LiveSeed.Now }, "fk-key", "fk-secret");
            db.AddRange(owner, intruder, key);
            await db.SaveChangesAsync(Ct);
            (ownerId, intruderId, keyId) = (owner.Id, intruder.Id, key.Id);
        }

        // The owner's own agent on the key: accepted.
        await using (var db = _fixture.CreateContext())
        {
            var agent = LiveSeed.NewAgent(await db.Users.SingleAsync(u => u.Id == ownerId, Ct), "owner's");
            agent.Mode = AgentMode.Live;
            agent.ExchangeKeyId = keyId;
            db.Add(agent);
            await db.SaveChangesAsync(Ct);
        }

        // Another user's agent on the same key: refused by the database, whatever the application forgot to check.
        await using (var db = _fixture.CreateContext())
        {
            var agent = LiveSeed.NewAgent(await db.Users.SingleAsync(u => u.Id == intruderId, Ct), "intruder's");
            agent.Mode = AgentMode.Live;
            agent.ExchangeKeyId = keyId;
            db.Add(agent);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
            var postgres = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres.SqlState);
            Assert.Equal("fk_agents_exchange_keys_exchange_key_id_user_id", postgres.ConstraintName);
        }

        // The key cannot vanish from under its agents by a plain delete (NO ACTION); the keys module clears the references first.
        await using (var db = _fixture.CreateContext())
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.ExchangeKeys.Where(k => k.Id == keyId).ExecuteDeleteAsync(Ct));
            var postgres = Assert.IsType<PostgresException>(ex is DbUpdateException ? ex.InnerException : ex);
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres.SqlState);
            await db.Agents.Where(a => a.ExchangeKeyId == keyId).ExecuteUpdateAsync(s => s.SetProperty(a => a.ExchangeKeyId, (Guid?)null), Ct);
            Assert.Equal(1, await db.ExchangeKeys.Where(k => k.Id == keyId).ExecuteDeleteAsync(Ct));
            Assert.Equal(ownerId, (await db.Agents.SingleAsync(a => a.UserId == ownerId, Ct)).UserId);
        }

        await using (var db = _fixture.CreateContext())
            await db.Users.Where(u => u.Id == ownerId || u.Id == intruderId).ExecuteDeleteAsync(Ct);
    }

    /// <summary>Deleting a user cascades to their keys and agents in one statement: the agent-to-key constraint is checked at the end, not in between.</summary>
    [Fact]
    public async Task DeletingAUserWithAgentsOnTheirKeysCascades()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        Guid userId;
        await using (var db = _fixture.CreateContext())
        {
            var (user, agent, _, _) = await LiveSeed.SeedEverythingAsync(db, Ct);
            userId = user.Id;
            Assert.NotNull(agent.ExchangeKeyId);
        }

        await using (var db = _fixture.CreateContext())
        {
            Assert.Equal(1, await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(Ct));
            Assert.Equal(0, await db.Agents.CountAsync(a => a.UserId == userId, Ct));
            Assert.Equal(0, await db.ExchangeKeys.CountAsync(k => k.UserId == userId, Ct));
            await db.LogEntries.ExecuteDeleteAsync(Ct);
            await db.Candles.ExecuteDeleteAsync(Ct);
            await db.ProviderModels.ExecuteDeleteAsync(Ct);
            await db.ProviderSyncStates.ExecuteDeleteAsync(Ct);
            await db.SystemKeys.ExecuteDeleteAsync(Ct);
            await db.ExperienceStats.ExecuteDeleteAsync(Ct);
        }
    }
}
