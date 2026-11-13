using System.Net;
using System.Text;
using Ecnanib.Credentials;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Persistence;
using Ecnanib.WebUi;
using Npgsql;

namespace Ecnanib.Tests.Credentials;

/// <summary>A row of the <c>ai_credentials</c> table, read by a test directly.</summary>
internal sealed record StoredAiCredential(
    Guid Id, Guid UserId, string Provider, byte[] EncryptedValue, string MasterKeyId, string MaskedSuffix, DateTimeOffset SavedAt)
{
    /// <summary>Every key of an AI provider, by user and provider.</summary>
    public static IReadOnlyList<StoredAiCredential> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            """
            SELECT id, user_id, provider, encrypted_value, master_key_id, masked_suffix, saved_at
            FROM ai_credentials ORDER BY user_id, provider
            """,
            connection);
        using var reader = command.ExecuteReader();

        var credentials = new List<StoredAiCredential>();
        while (reader.Read())
        {
            credentials.Add(new StoredAiCredential(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetFieldValue<byte[]>(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return credentials;
    }

    /// <summary>The key of a user for a provider, which must be stored.</summary>
    public static StoredAiCredential Of(TestDatabase database, Guid userId, string provider) =>
        All(database).Single(credential => credential.UserId == userId && credential.Provider == provider);
}

/// <summary>A row of the <c>exchange_credentials</c> table, read by a test directly.</summary>
internal sealed record StoredExchangeCredential(
    Guid Id, Guid UserId, string Environment, byte[] EncryptedValue, string MasterKeyId, string MaskedSuffix, DateTimeOffset SavedAt)
{
    /// <summary>Every Binance credential, by user and environment.</summary>
    public static IReadOnlyList<StoredExchangeCredential> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            """
            SELECT id, user_id, environment, encrypted_value, master_key_id, masked_suffix, saved_at
            FROM exchange_credentials ORDER BY user_id, environment
            """,
            connection);
        using var reader = command.ExecuteReader();

        var credentials = new List<StoredExchangeCredential>();
        while (reader.Read())
        {
            credentials.Add(new StoredExchangeCredential(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetFieldValue<byte[]>(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return credentials;
    }

    /// <summary>The credential of a user for an environment, which must be stored.</summary>
    public static StoredExchangeCredential Of(TestDatabase database, Guid userId, string environment) =>
        All(database).Single(credential => credential.UserId == userId && credential.Environment == environment);
}

/// <summary>The keys that the tests store. None has the shape of a real key, so that nothing masks one for its shape.</summary>
internal static class Keys
{
    /// <summary>A key that the rules accept, which ends in <c>Wx7Z</c>.</summary>
    public const string Claude = "claude-0123456789-abcdefghij-klmnop-Wx7Z";

    /// <summary>Another one, which ends in <c>Qr5T</c>.</summary>
    public const string AnotherClaude = "claude-9876543210-jihgfedcba-ponmlk-Qr5T";

    /// <summary>An API key of Binance that the rules accept, which ends in <c>Lm3N</c>.</summary>
    public const string BinanceApi = "binance-api-0123456789-abcdefghij-Lm3N";

    /// <summary>The secret key that belongs to it, which ends in <c>Zz9Y</c>.</summary>
    public const string BinanceSecret = "binance-sealed-0123456789-abcdefghij-Zz9Y";

    /// <summary>A key of a provider for a user, different for each, which ends in four characters of the provider's code.</summary>
    public static string Of(string owner, string provider) => $"{owner}-0123456789-abcdefghij-{provider.PadRight(4, '0')[..4]}";

    /// <summary>The forms in which the bytes of a key could be written, none of which may be anywhere.</summary>
    public static IReadOnlyList<string> FormsOf(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);

        return [key, Convert.ToBase64String(bytes), Convert.ToHexString(bytes), Convert.ToHexString(bytes).ToLowerInvariant()];
    }
}

/// <summary>What a browser does with the pages of a user's keys.</summary>
internal static class KeyBrowser
{
    /// <summary>The page that lists the keys of the signed-in user.</summary>
    public const string KeysPage = WebUiModule.KeysPath;

    /// <summary>The form that stores a key.</summary>
    public const string StoreForm = "store-key";

    /// <summary>The form that deletes one.</summary>
    public const string DeleteForm = "delete-key";

    /// <summary>The page that stores the key for an AI provider.</summary>
    public static string AiKeyPage(string provider) => $"{KeysPage}/ai/{provider}";

    /// <summary>The page that deletes it.</summary>
    public static string DeleteAiKeyPage(string provider) => $"{AiKeyPage(provider)}/delete";

    /// <summary>The page that stores the Binance credential for an environment: <c>demo</c> or <c>live</c>.</summary>
    public static string ExchangeKeyPage(string environment) => $"{KeysPage}/binance/{environment}";

    /// <summary>The page that deletes it.</summary>
    public static string DeleteExchangeKeyPage(string environment) => $"{ExchangeKeyPage(environment)}/delete";

    /// <summary>
    /// Enters a key for an AI provider and saves it, and follows where the answer leads. A user
    /// who has not just proved who they are is not shown the page: the form is then posted all
    /// the same, as something that is not a browser can.
    /// </summary>
    public static Task<(string Path, string Page)> StoreAiKeyAsync(this HttpClient client, string provider, string key) =>
        client.SubmitAsync(AiKeyPage(provider), StoreForm, ("ApiKey", key));

    /// <summary>Enters the two keys of a Binance credential and saves them, in the same way.</summary>
    public static Task<(string Path, string Page)> StoreExchangeKeyAsync(
        this HttpClient client, string environment, string apiKey, string secretKey) =>
        client.SubmitAsync(ExchangeKeyPage(environment), StoreForm, ("ApiKey", apiKey), ("SecretKey", secretKey));

    /// <summary>Presses the button of the page that deletes the key for an AI provider.</summary>
    public static Task<(string Path, string Page)> DeleteAiKeyAsync(this HttpClient client, string provider) =>
        client.SubmitAsync(DeleteAiKeyPage(provider), DeleteForm);

    /// <summary>Presses the button of the page that deletes the Binance credential for an environment.</summary>
    public static Task<(string Path, string Page)> DeleteExchangeKeyAsync(this HttpClient client, string environment) =>
        client.SubmitAsync(DeleteExchangeKeyPage(environment), DeleteForm);

    /// <summary>A page as a person reads it: with the characters that the markup writes as numbers.</summary>
    public static string Read(string page) => WebUtility.HtmlDecode(page);

    /// <summary>A stored key as the pages show it: dots, and its last four characters.</summary>
    public static string Masked(string key) => $"••••{key[^4..]}";
}

/// <summary>Rows of keys that a test needs a user to have, without the pages that store them.</summary>
internal static class StoredKeyRows
{
    /// <summary>
    /// Gives a user a key for an AI provider and a Binance credential, stored as the application
    /// stores them: encrypted with its active master key, each bound to its row and its owner.
    /// It is for a test that is about something else, and needs a row in every table that holds
    /// rows of users. Nobody is signed in for it, and nothing is recorded.
    /// </summary>
    public static async Task GiveKeysAsync(this AccountsApplication application, Guid userId, DateTimeOffset savedAt)
    {
        var aiId = Guid.CreateVersion7();
        var aiKey = Keys.Of(userId.ToString("N"), "claude");
        var ai = application.Secrets.Encrypt(Encoding.UTF8.GetBytes(aiKey), AiCredential.BoundTo(aiId, userId));

        var exchangeId = Guid.CreateVersion7();
        var apiKey = Keys.Of(userId.ToString("N"), "demo");
        var exchange = application.Secrets.Encrypt(
            ExchangeCredential.ValueOf(apiKey, Keys.Of(userId.ToString("N"), "seal")),
            ExchangeCredential.BoundTo(exchangeId, userId));

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        context.Add(new AiCredential(aiId, userId, new AiProviderCode("claude"), ai.Value, ai.KeyId, aiKey[^4..], savedAt));
        context.Add(new ExchangeCredential(
            exchangeId, userId, TradingMode.Demo, exchange.Value, exchange.KeyId, apiKey[^4..], savedAt));

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
