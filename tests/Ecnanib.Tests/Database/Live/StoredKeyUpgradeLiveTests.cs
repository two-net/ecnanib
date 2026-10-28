using System.Text;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Database.Migrations;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Database.Live;

/// <summary>
/// FR-KEY-04, DB-02: a database an earlier version filled with plain text keys is carried over whole. The migrations up
/// to that version's last one are applied, rows are written as that version wrote them, in the columns it had, then
/// the ProtectStoredKeys migration converts the columns in place and the first start encrypts what it finds. Its own
/// class, so the fixture's database is never migrated past the earlier version before the rows are in.
/// </summary>
[Trait("Category", "Live")]
public class StoredKeyUpgradeLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private const string LastMigrationBeforeEncryption = "20261026105003_AddRoleOutputAttempts";
    private const string ApiKey = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd";
    private const string Secret = "S3CR3T-VALUE-THAT-MUST-NEVER-LEAK-9876";
    private const string ProviderApiKey = "sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789wxyz";
    private readonly DatabaseLiveFixture _fixture;

    public StoredKeyUpgradeLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APlainTextDatabaseIsConvertedInPlaceAndEncryptedOnTheFirstStart()
    {
        _fixture.Require();
        var userId = Guid.CreateVersion7();
        var exchangeId = Guid.CreateVersion7();
        var providerId = Guid.CreateVersion7();
        await using (var db = _fixture.CreateContext())
        {
            await db.Database.MigrateAsync(LastMigrationBeforeEncryption, Ct);
            await db.Database.ExecuteSqlAsync($"INSERT INTO users (id, email, normalized_email, password_hash, security_stamp, theme, reduced_motion, created_at) VALUES ({userId}, 'upgrade@example.com', 'UPGRADE@EXAMPLE.COM', 'hash', 'stamp', 'System', false, now())", Ct);
            await db.Database.ExecuteSqlAsync($"INSERT INTO exchange_keys (id, user_id, label, environment, allow_spot, allow_futures, api_key, api_secret, status, created_at) VALUES ({exchangeId}, {userId}, 'old', 'Testnet', true, false, {ApiKey}, {Secret}, 'Valid', now())", Ct);
            await db.Database.ExecuteSqlAsync($"INSERT INTO provider_keys (id, user_id, provider, api_key, status, created_at) VALUES ({providerId}, {userId}, 'Claude', {ProviderApiKey}, 'Valid', now())", Ct);
        }

        var logs = new TestLoggerProvider();
        await using var services = _fixture.BuildServices(logs);
        await services.GetRequiredService<DatabaseMigrator>().MigrateAsync(Ct);

        Assert.Contains(logs.Entries, e => e.Message.StartsWith("Applying 1 database migration") && e.Message.Contains("ProtectStoredKeys"));
        await using (var db = _fixture.CreateContext())
        {
            // Converted in place: the same bytes, marked plain, and refused by every reader until the step has run.
            var exchange = await db.ExchangeKeys.AsNoTracking().SingleAsync(k => k.Id == exchangeId, Ct);
            Assert.Equal(StoredKeyCipher.Plain, exchange.Cipher);
            Assert.Equal(Encoding.UTF8.GetBytes(ApiKey), exchange.ProtectedApiKey);
            Assert.Equal(Encoding.UTF8.GetBytes(Secret), exchange.ProtectedApiSecret);
            var provider = await db.ProviderKeys.AsNoTracking().SingleAsync(k => k.Id == providerId, Ct);
            Assert.Equal(StoredKeyCipher.Plain, provider.Cipher);
            Assert.Equal(Encoding.UTF8.GetBytes(ProviderApiKey), provider.ProtectedApiKey);
            Assert.Throws<StoredKeyProtectionException>(() => StoredKeySeed.Protector.Unprotect(exchange));
            Assert.Throws<StoredKeyProtectionException>(() => StoredKeySeed.Protector.Unprotect(provider));
        }

        await services.GetRequiredService<StoredKeyProtection>().ProtectAsync(Ct);

        Assert.Contains(logs.Entries, e => e.Message == "Stored key protection check value created (first start under this Database:EncryptionKey, FR-CFG-05)");
        Assert.Contains(logs.Entries, e => e.Message == "Stored keys protected: 2 plain row(s) encrypted (FR-KEY-04)");
        await using (var db = _fixture.CreateContext())
        {
            var exchange = await db.ExchangeKeys.AsNoTracking().SingleAsync(k => k.Id == exchangeId, Ct);
            Assert.Equal(StoredKeyCipher.AesGcm, exchange.Cipher);
            Assert.Equal((ApiKey, Secret), StoredKeySeed.Protector.Unprotect(exchange));
            StoredKeySeed.AssertHidden(exchange.ProtectedApiKey, ApiKey);
            StoredKeySeed.AssertHidden(exchange.ProtectedApiSecret, Secret);
            var provider = await db.ProviderKeys.AsNoTracking().SingleAsync(k => k.Id == providerId, Ct);
            Assert.Equal(StoredKeyCipher.AesGcm, provider.Cipher);
            Assert.Equal(ProviderApiKey, StoredKeySeed.Protector.Unprotect(provider));
            StoredKeySeed.AssertHidden(provider.ProtectedApiKey, ProviderApiKey);
        }

        Assert.DoesNotContain(ApiKey, logs.AllText);
        Assert.DoesNotContain(Secret, logs.AllText);
        Assert.DoesNotContain(ProviderApiKey, logs.AllText);
    }
}
