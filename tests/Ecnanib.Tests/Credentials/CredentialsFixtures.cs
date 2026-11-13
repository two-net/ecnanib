using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Ecnanib.AiProviderGateway;
using Ecnanib.Credentials;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Persistence;
using Ecnanib.WebUi;
using Npgsql;

namespace Ecnanib.Tests.Credentials;

/// <summary>A row of the <c>ai_credentials</c> table, read by a test directly.</summary>
internal sealed record StoredAiCredential(
    Guid Id,
    Guid UserId,
    string Provider,
    byte[] EncryptedValue,
    string MasterKeyId,
    string MaskedSuffix,
    DateTimeOffset SavedAt,
    string Status,
    DateTimeOffset? ValidatedAt)
{
    /// <summary>Every key of an AI provider, by user and provider.</summary>
    public static IReadOnlyList<StoredAiCredential> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            """
            SELECT id, user_id, provider, encrypted_value, master_key_id, masked_suffix, saved_at, status, validated_at
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
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8)));
        }

        return credentials;
    }

    /// <summary>The key of a user for a provider, which must be stored.</summary>
    public static StoredAiCredential Of(TestDatabase database, Guid userId, string provider) =>
        All(database).Single(credential => credential.UserId == userId && credential.Provider == provider);
}

/// <summary>A row of the <c>exchange_credentials</c> table, read by a test directly.</summary>
internal sealed record StoredExchangeCredential(
    Guid Id,
    Guid UserId,
    string Environment,
    byte[] EncryptedValue,
    string MasterKeyId,
    string MaskedSuffix,
    DateTimeOffset SavedAt,
    string Status,
    DateTimeOffset? ValidatedAt,
    bool SpotTrading,
    bool FuturesTrading,
    bool? IpRestricted)
{
    /// <summary>Every Binance credential, by user and environment.</summary>
    public static IReadOnlyList<StoredExchangeCredential> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            """
            SELECT id, user_id, environment, encrypted_value, master_key_id, masked_suffix, saved_at,
                   status, validated_at, spot_trading, futures_trading, ip_restricted
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
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetBoolean(9),
                reader.GetBoolean(10),
                reader.IsDBNull(11) ? null : reader.GetBoolean(11)));
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

    /// <summary>
    /// Presses the button of the list that has the provider asked about the stored key again.
    /// The answer is the list itself, with what the provider said.
    /// </summary>
    public static Task<(string Path, string Page)> CheckAiKeyAsync(this HttpClient client, string provider) =>
        client.SubmitAsync(KeysPage, $"check-ai-{provider}");

    /// <summary>Presses the button of the list that has Binance asked about the stored credential of an environment again.</summary>
    public static Task<(string Path, string Page)> CheckExchangeKeyAsync(this HttpClient client, string environment) =>
        client.SubmitAsync(KeysPage, $"check-binance-{environment}");

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
    /// stores them: encrypted with its active master key, each bound to its row and its owner,
    /// and valid as of the moment it was stored, as a key is that its provider took. It is for a
    /// test that is about something else, and needs a row in every table that holds rows of
    /// users. Nobody is signed in for it, and nothing is recorded.
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

        context.Add(new AiCredential(
            aiId, userId, new AiProviderCode("claude"), ai.Value, ai.KeyId, aiKey[^4..], savedAt, KeyStatus.Valid, savedAt));
        context.Add(new ExchangeCredential(
            exchangeId,
            userId,
            TradingMode.Demo,
            exchange.Value,
            exchange.KeyId,
            apiKey[^4..],
            savedAt,
            KeyStatus.Valid,
            savedAt,
            spotTrading: true,
            futuresTrading: true,
            ipRestricted: null));

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}

/// <summary>One time that <see cref="StandInProviders"/> was asked about a key.</summary>
/// <param name="About">Whose key: the code of the AI provider, or <c>Demo</c> or <c>Live</c> for Binance.</param>
/// <param name="Key">The key as it was handed over. For Binance, the API key.</param>
/// <param name="SecretKey">The secret key of a Binance credential, and null for an AI provider.</param>
internal sealed record AskedKey(string About, string Key, string? SecretKey = null);

/// <summary>
/// Stands for the AI providers and for Binance, where the application asks them about a key
/// (FR-KEY-3): it keeps what it was asked, and answers as the test says. Unless a test says
/// otherwise, every provider takes every key, and Binance takes every credential as one that
/// trades on both markets and cannot withdraw.
/// </summary>
internal sealed class StandInProviders : IAiKeyChecks, IExchangeAdapter
{
    /// <summary>What Binance says about a credential of its test environment: it reports no permissions of a key there.</summary>
    public static readonly ExchangeKeyCheck TakenInDemo = new(KeyVerdict.Accepted, SpotTrading: true, FuturesTrading: true);

    /// <summary>What Binance says about a credential of the real exchange that a careful user made.</summary>
    public static readonly ExchangeKeyCheck TakenInLive =
        new(KeyVerdict.Accepted, SpotTrading: true, FuturesTrading: true, CanWithdraw: false, IpRestricted: true);

    private readonly ConcurrentQueue<AskedKey> _asked = new();

    /// <summary>Every time it was asked, in order.</summary>
    public IReadOnlyList<AskedKey> Asked => [.. _asked];

    /// <summary>What an AI provider, named by its code, says about a key.</summary>
    public Func<string, string, KeyVerdict> AiAnswer { get; set; } = (_, _) => KeyVerdict.Accepted;

    /// <summary>What Binance says about a credential of an environment, by its API key.</summary>
    public Func<TradingMode, string, ExchangeKeyCheck> BinanceAnswer { get; set; } =
        (environment, _) => environment == TradingMode.Live ? TakenInLive : TakenInDemo;

    /// <summary>
    /// When set, it runs once while a provider is asked, before the answer: for what happens to
    /// a key in the time that its provider takes to answer.
    /// </summary>
    public Func<Task>? WhileAsked { get; set; }

    public async Task<KeyVerdict> CheckAsync(AiProviderCode provider, string apiKey, CancellationToken cancellationToken)
    {
        _asked.Enqueue(new AskedKey(provider.Value, apiKey));
        await MeanwhileAsync();

        return AiAnswer(provider.Value, apiKey);
    }

    public async Task<ExchangeKeyCheck> CheckKeyAsync(TradingMode mode, string apiKey, string secretKey, CancellationToken cancellationToken)
    {
        _asked.Enqueue(new AskedKey(mode.ToString(), apiKey, secretKey));
        await MeanwhileAsync();

        return BinanceAnswer(mode, apiKey);
    }

    public Task<DateTimeOffset> GetServerTimeAsync(Market market, TradingMode mode, CancellationToken cancellationToken) =>
        Task.FromResult(TimeProvider.System.GetUtcNow());

    private async Task MeanwhileAsync()
    {
        if (WhileAsked is { } meanwhile)
        {
            WhileAsked = null;
            await meanwhile();
        }
    }
}
