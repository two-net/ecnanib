using System.Security.Cryptography;
using System.Text;
using Ecnanib.Credentials;
using Ecnanib.Platform.Security;
using Ecnanib.Platform.Settings;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;

namespace Ecnanib.Tests.Credentials;

/// <summary>
/// A user's keys are stored encrypted (NFR-SEC-5): with authenticated encryption under a master
/// key of the configuration file, with the id of that key beside each, and bound to the row and
/// the user they are stored for (FR-KEY-6). They follow the active master key when it changes
/// (FR-KEY-7). The file of the tests lists the keys k1 and k2, and marks k2 active.
/// </summary>
public class KeyStorageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_keys_are_in_the_database_only_encrypted_with_the_id_of_the_active_master_key()
    {
        using var application = new AccountsApplication();
        var user = await WithKeysAsync(application, Ada);

        // A nonce of 12 bytes, the key encrypted, and a tag of 16, with the id of the master
        // key that encrypted them. For Binance, the two keys with a line feed between them.
        var aiKey = StoredAiCredential.Of(application.Database, user.Id, "claude");
        Assert.Equal("k2", aiKey.MasterKeyId);
        Assert.Equal(12 + Keys.Claude.Length + 16, aiKey.EncryptedValue.Length);
        Assert.False(aiKey.EncryptedValue.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Keys.Claude)) >= 0);

        var exchangeKey = StoredExchangeCredential.Of(application.Database, user.Id, "Demo");
        Assert.Equal("k2", exchangeKey.MasterKeyId);
        Assert.Equal(12 + Keys.BinanceApi.Length + 1 + Keys.BinanceSecret.Length + 16, exchangeKey.EncryptedValue.Length);
        Assert.False(exchangeKey.EncryptedValue.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Keys.BinanceSecret)) >= 0);

        // Nothing in the database, in any column of any table, holds a key in a form that a
        // reader could use.
        var everything = application.EverythingStored();
        Assert.Contains(Convert.ToHexString(aiKey.EncryptedValue), everything, StringComparison.OrdinalIgnoreCase);
        Assert.All(
            new[] { Keys.Claude, Keys.BinanceApi, Keys.BinanceSecret }.SelectMany(Keys.FormsOf),
            form => Assert.DoesNotContain(form, everything));
    }

    [Fact]
    public async Task A_stored_key_decrypts_with_its_master_key_for_its_row_and_its_owner_and_for_nothing_else()
    {
        using var application = new AccountsApplication();
        var user = await WithKeysAsync(application, Ada);
        var masterKey = Convert.FromBase64String(SettingsJson.MasterKey);

        var aiKey = StoredAiCredential.Of(application.Database, user.Id, "claude");
        var aiValue = new EncryptedSecret(aiKey.MasterKeyId, aiKey.EncryptedValue);
        var exchangeKey = StoredExchangeCredential.Of(application.Database, user.Id, "Demo");
        var exchangeValue = new EncryptedSecret(exchangeKey.MasterKeyId, exchangeKey.EncryptedValue);

        // With the key that the file lists under that id, it is what was entered.
        Assert.Equal(
            Keys.Claude,
            Encoding.UTF8.GetString(Secrets("k2", masterKey).Decrypt(aiValue, AiCredential.BoundTo(aiKey.Id, user.Id))));
        Assert.Equal(
            $"{Keys.BinanceApi}\n{Keys.BinanceSecret}",
            Encoding.UTF8.GetString(Secrets("k2", masterKey).Decrypt(exchangeValue, ExchangeCredential.BoundTo(exchangeKey.Id, user.Id))));

        // With another key under that id, it is nothing.
        Assert.ThrowsAny<CryptographicException>(
            () => Secrets("k2", new byte[32]).Decrypt(aiValue, AiCredential.BoundTo(aiKey.Id, user.Id)));

        // Nor as the value of another row, of another user, or of the other kind of key: a value
        // that is copied elsewhere, or a row that is handed to someone else, is of no use there.
        Assert.All(
            new[]
            {
                AiCredential.BoundTo(Guid.CreateVersion7(), user.Id),
                AiCredential.BoundTo(aiKey.Id, Guid.CreateVersion7()),
                ExchangeCredential.BoundTo(aiKey.Id, user.Id),
            },
            boundTo => Assert.ThrowsAny<CryptographicException>(() => Secrets("k2", masterKey).Decrypt(aiValue, boundTo)));
        Assert.All(
            new[]
            {
                ExchangeCredential.BoundTo(Guid.CreateVersion7(), user.Id),
                ExchangeCredential.BoundTo(exchangeKey.Id, Guid.CreateVersion7()),
                AiCredential.BoundTo(exchangeKey.Id, user.Id),
            },
            boundTo => Assert.ThrowsAny<CryptographicException>(() => Secrets("k2", masterKey).Decrypt(exchangeValue, boundTo)));
    }

    [Fact]
    public async Task Two_users_who_store_the_same_key_do_not_store_the_same_value()
    {
        using var application = new AccountsApplication();
        var ada = await WithKeysAsync(application, Ada);
        var grace = await WithKeysAsync(application, Grace);

        // Each value has a nonce of its own, so nothing of one is in the other.
        var adas = StoredAiCredential.Of(application.Database, ada.Id, "claude").EncryptedValue;
        var graces = StoredAiCredential.Of(application.Database, grace.Id, "claude").EncryptedValue;

        Assert.NotEqual(Convert.ToHexString(adas.AsSpan(0, 12)), Convert.ToHexString(graces.AsSpan(0, 12)));
        Assert.NotEqual(Convert.ToHexString(adas.AsSpan(12)), Convert.ToHexString(graces.AsSpan(12)));
    }

    [Fact]
    public async Task A_key_encrypted_under_an_earlier_master_key_follows_the_active_one()
    {
        var clock = new TestClock(Morning);
        var database = PostgresServer.CreateDatabase();
        StoredUser user;
        StoredAiCredential earlierAi;
        StoredExchangeCredential earlierExchange;

        // The file marks k1 active when the keys are stored.
        using (var earlier = new AccountsApplication(
            clock: clock, database: database, settings: file => file.With("Security:ActiveKeyId", "k1")))
        {
            user = await WithKeysAsync(earlier, Ada);
            earlierAi = StoredAiCredential.Of(database, user.Id, "claude");
            earlierExchange = StoredExchangeCredential.Of(database, user.Id, "Demo");

            Assert.Equal(("k1", "k1"), (earlierAi.MasterKeyId, earlierExchange.MasterKeyId));
        }

        // Then the file marks k2 active, and still lists k1. The columns are declared, so the
        // values are encrypted again when the application starts (FR-KEY-7).
        using var application = new AccountsApplication(clock: clock, database: database);
        using var client = application.CreateClient();
        await client.PageAsync("/");

        var ai = StoredAiCredential.Of(database, user.Id, "claude");
        var exchange = StoredExchangeCredential.Of(database, user.Id, "Demo");

        Assert.Equal(("k2", "k2"), (ai.MasterKeyId, exchange.MasterKeyId));
        Assert.NotEqual(Convert.ToHexString(earlierAi.EncryptedValue), Convert.ToHexString(ai.EncryptedValue));

        // They are the same keys, in the same rows, bound to what they were bound to.
        Assert.Equal((earlierAi.Id, earlierAi.MaskedSuffix, earlierAi.SavedAt), (ai.Id, ai.MaskedSuffix, ai.SavedAt));
        Assert.Equal(
            Keys.Claude,
            Encoding.UTF8.GetString(application.Secrets.Decrypt(
                new EncryptedSecret(ai.MasterKeyId, ai.EncryptedValue), AiCredential.BoundTo(ai.Id, user.Id))));
        Assert.Equal(
            $"{Keys.BinanceApi}\n{Keys.BinanceSecret}",
            Encoding.UTF8.GetString(application.Secrets.Decrypt(
                new EncryptedSecret(exchange.MasterKeyId, exchange.EncryptedValue), ExchangeCredential.BoundTo(exchange.Id, user.Id))));

        // The log says so, by the column, and holds nothing of a key.
        const string moved = "Encrypted {ValueCount} stored secrets of {Column} again, with the active master key {ActiveKeyId}";
        Assert.Contains(
            application.Events(),
            written => written.Template == moved && written.Text("Column") == "ai_credentials.encrypted_value");
        Assert.Contains(
            application.Events(),
            written => written.Template == moved && written.Text("Column") == "exchange_credentials.encrypted_value");
        Assert.All(
            new[] { Keys.Claude, Keys.BinanceApi, Keys.BinanceSecret }.SelectMany(Keys.FormsOf),
            form => Assert.DoesNotContain(form, application.LogText()));

        // A key that is stored from now on is encrypted with the active one.
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreAiKeyAsync("gemini", Keys.Of("ada", "gemini"));
        Assert.Equal("k2", StoredAiCredential.Of(database, user.Id, "gemini").MasterKeyId);
    }

    /// <summary>Registers a user, and stores a key for Claude and a Binance credential for Demo as the user does.</summary>
    private static async Task<StoredUser> WithKeysAsync(AccountsApplication application, string email)
    {
        var user = await application.RegisterVerifiedAsync(email);
        using var client = application.CreateClient();
        await client.SignInAsync(email);
        await client.ConfirmIdentityAsync();

        Assert.Equal(KeyBrowser.KeysPage, (await client.StoreAiKeyAsync("claude", Keys.Claude)).Path);
        Assert.Equal(KeyBrowser.KeysPage, (await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret)).Path);

        return user;
    }

    private static StoredSecrets Secrets(string id, byte[] key) =>
        new(new SecuritySettings(
            id,
            new Dictionary<string, Secret>(StringComparer.OrdinalIgnoreCase) { [id] = new(Convert.ToBase64String(key)) },
            SessionIdleMinutes: 45));
}
