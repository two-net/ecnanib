using System.Security.Cryptography;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Database.Live;

/// <summary>
/// FR-KEY-04, FR-CFG-05: the start-up step over a real database. The first start encrypts the rows the migration left
/// plain and creates the check value; every later start proves the key against it; a rotation re-encrypts every row
/// under the new key; a key that opens nothing refuses the start and changes no byte. Its own class, so the check value
/// never reaches the databases the other tests share, and every test leaves the key as it found it.
/// </summary>
[Trait("Category", "Live")]
public class StoredKeyProtectionLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private const string ApiKey = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd";
    private const string Secret = "S3CR3T-VALUE-THAT-MUST-NEVER-LEAK-9876";
    private const string ProviderApiKey = "sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789wxyz";
    private static readonly StoredKeyProtector Other = new(Convert.FromBase64String(DatabaseTestHost.OtherEncryptionKey), null);
    private static readonly IStoredKeyProtector OtherRows = Other;
    private readonly DatabaseLiveFixture _fixture;

    public StoredKeyProtectionLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheFirstStartEncryptsThePlainRowsAndCreatesTheCheckValueAndLaterStartsFindBoth()
    {
        await StartAsync();
        // A first start: no check value yet, whatever an earlier test of this class left behind.
        await using (var db = _fixture.CreateContext())
            await db.SystemKeys.Where(k => k.Purpose == StoredKeyProtection.Purpose).ExecuteDeleteAsync(Ct);
        var user = LiveSeed.NewUser("protect-first@example.com");
        Guid exchangeId, providerId, encryptedId;
        await using (var db = _fixture.CreateContext())
        {
            // Two rows as the ProtectStoredKeys migration leaves them, and one the application already wrote.
            var exchange = PlainExchangeKey(user, "plain");
            var plainProvider = PlainProviderKey(user, AiProvider.Claude);
            var encrypted = StoredKeySeed.Protect(new ProviderKey { User = user, Provider = AiProvider.Gpt, ProtectedApiKey = [], Status = KeyStatus.Valid, CreatedAt = LiveSeed.Now }, ProviderApiKey);
            db.AddRange(user, exchange, plainProvider, encrypted);
            await db.SaveChangesAsync(Ct);
            (exchangeId, providerId, encryptedId) = (exchange.Id, plainProvider.Id, encrypted.Id);
            // Until the step has run, a plain row is refused by every reader.
            Assert.Throws<StoredKeyProtectionException>(() => StoredKeySeed.Protector.Unprotect(exchange));
        }

        var logs = new TestLoggerProvider();
        await ProtectAsync(logs);

        await using (var db = _fixture.CreateContext())
        {
            Assert.True(StoredKeySeed.Keyed.CheckValueOpens(StoredKeyProtection.Purpose, Convert.FromBase64String((await CheckValueAsync())!), withPrevious: false));
            var exchange = await db.ExchangeKeys.SingleAsync(k => k.Id == exchangeId, Ct);
            Assert.Equal(StoredKeyCipher.AesGcm, exchange.Cipher);
            Assert.Equal((ApiKey, Secret), StoredKeySeed.Protector.Unprotect(exchange));
            StoredKeySeed.AssertHidden(exchange.ProtectedApiKey, ApiKey);
            StoredKeySeed.AssertHidden(exchange.ProtectedApiSecret, Secret);
            var provider = await db.ProviderKeys.SingleAsync(k => k.Id == providerId, Ct);
            Assert.Equal(StoredKeyCipher.AesGcm, provider.Cipher);
            Assert.Equal(ProviderApiKey, StoredKeySeed.Protector.Unprotect(provider));
            StoredKeySeed.AssertHidden(provider.ProtectedApiKey, ProviderApiKey);
            Assert.Equal(ProviderApiKey, StoredKeySeed.Protector.Unprotect(await db.ProviderKeys.SingleAsync(k => k.Id == encryptedId, Ct)));
        }

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message == "Stored key protection check value created (first start under this Database:EncryptionKey, FR-CFG-05)");
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message == "Stored keys protected: 2 plain row(s) encrypted (FR-KEY-04)");
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning);
        AssertNoSecrets(logs);

        // The next start finds the check value and leaves the rows as they are.
        var before = await BytesAsync(exchangeId, providerId);
        logs = new TestLoggerProvider();
        await ProtectAsync(logs);
        Assert.Equal(before, await BytesAsync(exchangeId, providerId));
        Assert.Contains(logs.Entries, e => e.Message == "Stored keys protected: 0 plain row(s) encrypted (FR-KEY-04)");
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("check value created"));
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning);

        await DeleteAsync(user.Id);
    }

    [Fact]
    public async Task ARotationReEncryptsEveryRowUnderTheNewKeyAndWarnsWhenThePreviousKeyLingers()
    {
        await StartAsync();
        await ProtectAsync(new TestLoggerProvider());
        var user = LiveSeed.NewUser("protect-rotate@example.com");
        Guid exchangeId, providerId;
        await using (var db = _fixture.CreateContext())
        {
            var exchange = StoredKeySeed.Protect(new ExchangeKey { User = user, Label = "rotated", Environment = ExchangeEnvironment.Production, AllowSpot = true, AllowFutures = true, ProtectedApiKey = [], ProtectedApiSecret = [], Status = KeyStatus.Valid, CreatedAt = LiveSeed.Now }, ApiKey, Secret);
            var provider = StoredKeySeed.Protect(new ProviderKey { User = user, Provider = AiProvider.Claude, ProtectedApiKey = [], Status = KeyStatus.Valid, CreatedAt = LiveSeed.Now }, ProviderApiKey);
            db.AddRange(user, exchange, provider);
            await db.SaveChangesAsync(Ct);
            (exchangeId, providerId) = (exchange.Id, provider.Id);
        }

        var before = await BytesAsync(exchangeId, providerId);
        var logs = new TestLoggerProvider();
        await ProtectAsync(logs, DatabaseTestHost.OtherEncryptionKey, DatabaseTestHost.EncryptionKey);

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message == "Database:EncryptionKey changed; 2 stored key row(s) and the check value were re-encrypted under it. Remove Database:PreviousEncryptionKey");
        Assert.Contains(logs.Entries, e => e.Message == "Stored keys protected: 0 plain row(s) encrypted (FR-KEY-04)");
        AssertNoSecrets(logs);
        var after = await BytesAsync(exchangeId, providerId);
        Assert.NotEqual(before, after);
        await using (var db = _fixture.CreateContext())
        {
            var exchange = await db.ExchangeKeys.SingleAsync(k => k.Id == exchangeId, Ct);
            Assert.Equal((ApiKey, Secret), OtherRows.Unprotect(exchange));
            Assert.Throws<StoredKeyProtectionException>(() => StoredKeySeed.Protector.Unprotect(exchange));
            Assert.Equal(ProviderApiKey, OtherRows.Unprotect(await db.ProviderKeys.SingleAsync(k => k.Id == providerId, Ct)));
            Assert.True(Other.CheckValueOpens(StoredKeyProtection.Purpose, Convert.FromBase64String((await CheckValueAsync())!), withPrevious: false));
        }

        // The new key alone is enough from now on; a previous key left behind is pointed out, and changes nothing.
        logs = new TestLoggerProvider();
        await ProtectAsync(logs, DatabaseTestHost.OtherEncryptionKey);
        Assert.Equal(after, await BytesAsync(exchangeId, providerId));
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning);
        logs = new TestLoggerProvider();
        await ProtectAsync(logs, DatabaseTestHost.OtherEncryptionKey, DatabaseTestHost.EncryptionKey);
        Assert.Equal(after, await BytesAsync(exchangeId, providerId));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message == "Database:PreviousEncryptionKey is set, but the stored keys already open under Database:EncryptionKey; remove it");

        // Back to the hosts' key, so the other tests find the database as they expect it.
        await ProtectAsync(new TestLoggerProvider(), DatabaseTestHost.EncryptionKey, DatabaseTestHost.OtherEncryptionKey);
        await using (var db = _fixture.CreateContext())
            Assert.Equal((ApiKey, Secret), StoredKeySeed.Protector.Unprotect(await db.ExchangeKeys.SingleAsync(k => k.Id == exchangeId, Ct)));
        await DeleteAsync(user.Id);
    }

    [Fact]
    public async Task AKeyThatOpensNothingRefusesTheStartAndChangesNoByte()
    {
        await StartAsync();
        await ProtectAsync(new TestLoggerProvider());
        var user = LiveSeed.NewUser("protect-wrong@example.com");
        Guid exchangeId, providerId;
        await using (var db = _fixture.CreateContext())
        {
            var exchange = PlainExchangeKey(user, "still-plain");
            var provider = StoredKeySeed.Protect(new ProviderKey { User = user, Provider = AiProvider.Claude, ProtectedApiKey = [], Status = KeyStatus.Valid, CreatedAt = LiveSeed.Now }, ProviderApiKey);
            db.AddRange(user, exchange, provider);
            await db.SaveChangesAsync(Ct);
            (exchangeId, providerId) = (exchange.Id, provider.Id);
        }

        var before = await BytesAsync(exchangeId, providerId);
        var checkBefore = await CheckValueAsync();
        var logs = new TestLoggerProvider();
        var ex = await Assert.ThrowsAsync<StoredKeyProtectionException>(() => ProtectAsync(logs, DatabaseTestHost.OtherEncryptionKey));

        Assert.Contains("neither Database:EncryptionKey nor Database:PreviousEncryptionKey", ex.Message);
        var critical = Assert.Single(logs.Entries, e => e.Level == LogLevel.Critical);
        Assert.StartsWith("Stored key protection failed; the application will not start: The stored API keys were encrypted under a key that is neither", critical.Message);
        AssertNoSecrets(logs);
        Assert.Equal(before, await BytesAsync(exchangeId, providerId));
        Assert.Equal(checkBefore, await CheckValueAsync());

        // A rotation that meets a row the previous key does not open rolls back whole: nothing moves to the new key.
        Guid strayId;
        await using (var db = _fixture.CreateContext())
        {
            var stray = new ProviderKey { Id = Guid.CreateVersion7(), UserId = user.Id, Provider = AiProvider.Gemini, ProtectedApiKey = [], Status = KeyStatus.Valid, CreatedAt = LiveSeed.Now };
            ((IStoredKeyProtector)new StoredKeyProtector(RandomNumberGenerator.GetBytes(StoredKeyProtector.KeySize), null)).Protect(stray, ProviderApiKey);
            db.Add(stray);
            await db.SaveChangesAsync(Ct);
            strayId = stray.Id;
        }

        logs = new TestLoggerProvider();
        ex = await Assert.ThrowsAsync<StoredKeyProtectionException>(() => ProtectAsync(logs, DatabaseTestHost.OtherEncryptionKey, DatabaseTestHost.EncryptionKey));

        Assert.Contains($"Provider key {strayId} does not open under Database:PreviousEncryptionKey; nothing was rotated.", ex.Message);
        Assert.Single(logs.Entries, e => e.Level == LogLevel.Critical);
        AssertNoSecrets(logs);
        Assert.Equal(before, await BytesAsync(exchangeId, providerId));
        Assert.Equal(checkBefore, await CheckValueAsync());
        await DeleteAsync(user.Id);
    }

    [Fact]
    public async Task AMissingCheckValueIsRecreatedOnlyWhenEveryEncryptedRowOpensUnderTheKey()
    {
        await StartAsync();
        await ProtectAsync(new TestLoggerProvider());
        var user = LiveSeed.NewUser("protect-check@example.com");
        Guid strayId;
        await using (var db = _fixture.CreateContext())
        {
            await db.SystemKeys.Where(k => k.Purpose == StoredKeyProtection.Purpose).ExecuteDeleteAsync(Ct);
            var stray = new ProviderKey { Id = Guid.CreateVersion7(), User = user, Provider = AiProvider.Grok, ProtectedApiKey = [], Status = KeyStatus.Valid, CreatedAt = LiveSeed.Now };
            OtherRows.Protect(stray, ProviderApiKey);
            db.AddRange(user, stray);
            await db.SaveChangesAsync(Ct);
            strayId = stray.Id;
        }

        var logs = new TestLoggerProvider();
        var ex = await Assert.ThrowsAsync<StoredKeyProtectionException>(() => ProtectAsync(logs));

        Assert.Contains($"Provider key {strayId} was encrypted under another key than Database:EncryptionKey and the check value is missing from system_keys", ex.Message);
        Assert.Single(logs.Entries, e => e.Level == LogLevel.Critical);
        Assert.Null(await CheckValueAsync());
        AssertNoSecrets(logs);

        await DeleteAsync(user.Id);
        logs = new TestLoggerProvider();
        await ProtectAsync(logs);
        Assert.NotNull(await CheckValueAsync());
        Assert.Contains(logs.Entries, e => e.Message.Contains("check value created"));
    }

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }

    /// <summary>The step as the host runs it, under the hosts' key unless the test names another, with or without a previous one.</summary>
    private async Task ProtectAsync(TestLoggerProvider logs, string? key = null, string? previous = null)
    {
        var config = new Dictionary<string, string?>();
        if (key is not null)
            config["Database:EncryptionKey"] = key;
        if (previous is not null)
            config["Database:PreviousEncryptionKey"] = previous;
        await using var services = _fixture.BuildServices(logs, config: config);
        await services.GetRequiredService<StoredKeyProtection>().ProtectAsync(Ct);
    }

    private static ExchangeKey PlainExchangeKey(User user, string label) => new()
    {
        User = user, Label = label, Environment = ExchangeEnvironment.Testnet, AllowSpot = true, AllowFutures = false,
        ProtectedApiKey = StoredKeySeed.Plain(ApiKey), ProtectedApiSecret = StoredKeySeed.Plain(Secret), Cipher = StoredKeyCipher.Plain,
        Status = KeyStatus.Valid, LastValidatedAt = LiveSeed.Now, CreatedAt = LiveSeed.Now,
    };

    private static ProviderKey PlainProviderKey(User user, AiProvider provider) => new()
    {
        User = user, Provider = provider, ProtectedApiKey = StoredKeySeed.Plain(ProviderApiKey), Cipher = StoredKeyCipher.Plain,
        Status = KeyStatus.Valid, LastValidatedAt = LiveSeed.Now, CreatedAt = LiveSeed.Now,
    };

    /// <summary>Every stored byte of the two rows and their markers, as one comparable string.</summary>
    private async Task<string> BytesAsync(Guid exchangeId, Guid providerId)
    {
        await using var db = _fixture.CreateContext();
        var exchange = await db.ExchangeKeys.AsNoTracking().SingleAsync(k => k.Id == exchangeId, Ct);
        var provider = await db.ProviderKeys.AsNoTracking().SingleAsync(k => k.Id == providerId, Ct);
        return string.Join("|", Convert.ToBase64String(exchange.ProtectedApiKey), Convert.ToBase64String(exchange.ProtectedApiSecret), exchange.Cipher, Convert.ToBase64String(provider.ProtectedApiKey), provider.Cipher);
    }

    private async Task<string?> CheckValueAsync()
    {
        await using var db = _fixture.CreateContext();
        return await db.SystemKeys.AsNoTracking().Where(k => k.Purpose == StoredKeyProtection.Purpose).Select(k => k.Value).SingleOrDefaultAsync(Ct);
    }

    private async Task DeleteAsync(Guid userId)
    {
        await using var db = _fixture.CreateContext();
        await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(Ct);
    }

    /// <summary>FR-KEY-04, FR-LOG-05: the step logs counts and ids, never a value or a key.</summary>
    private static void AssertNoSecrets(TestLoggerProvider logs)
    {
        Assert.DoesNotContain(ApiKey, logs.AllText);
        Assert.DoesNotContain(Secret, logs.AllText);
        Assert.DoesNotContain(ProviderApiKey, logs.AllText);
        Assert.DoesNotContain(DatabaseTestHost.EncryptionKey, logs.AllText);
        Assert.DoesNotContain(DatabaseTestHost.OtherEncryptionKey, logs.AllText);
    }
}
