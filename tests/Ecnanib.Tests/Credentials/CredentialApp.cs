using HttpStatusCode = System.Net.HttpStatusCode;
using System.Net.Http;
using Ecnanib.Kernel;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Tests.Ai;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Tests.Credentials;

/// <summary>
/// The application with its own database and clock, a <see cref="FakeBinance"/>, and a
/// <see cref="FakeAiProvider"/> behind an AI provider named Test. The adapters keep limits and credential
/// slots in memory, so each test gets its own. Credential observers are removed unless a test asks for them,
/// because the catalog's would list a key's models again after each change, and these tests count requests.
/// </summary>
internal sealed class CredentialApp : IAsyncDisposable
{
    public const string Provider = "Test";
    public const string EgressAddress = "203.0.113.10";
    public const string EgressAddressV6 = "2001:db8::10";

    /// <summary>Long enough for the shape check, which a real key always is.</summary>
    public const string AiKey = "sk-test-0123456789abcdefWXYZ";
    public const string OtherAiKey = "sk-test-fedcba9876543210ABCD";

    public const string ModelsPath = "/v1/models";
    public const string RestrictionsPath = "/sapi/v1/account/apiRestrictions";
    public const string SpotAccountPath = "/api/v3/account";
    public const string FuturesAccountPath = "/fapi/v3/account";

    /// <summary>The account in <see cref="BinanceSamples.SpotAccount"/>.</summary>
    public const long Uid = 354937868;

    public CredentialApp(
        PostgresFixture postgres,
        Dictionary<string, string?>? settings = null,
        bool observers = false,
        Action<IServiceCollection>? services = null)
    {
        Binance = new FakeBinance(Clock);
        App = AccountApp.Create(
            postgres,
            Settings(settings),
            collection =>
            {
                ExchangeHarness.Use(collection, Binance, Clock);
                AiHarness.Use(collection, Ai, new TestAdapter(Provider), new TestAdapter("Second"));

                if (!observers)
                {
                    collection.RemoveAll<ICredentialObserver>();
                }

                services?.Invoke(collection);
            });
        App.Start();
    }

    public TestClock Clock { get; } = new();

    public FakeBinance Binance { get; }

    public FakeAiProvider Ai { get; } = new();

    public AppFactory App { get; }

    public ICredentials Credentials => App.Services.GetRequiredService<ICredentials>();

    public ICredentialKeys Keys => App.Services.GetRequiredService<ICredentialKeys>();

    public string ConnectionString => App.ConnectionString();

    public static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public static Dictionary<string, string?> Settings(Dictionary<string, string?>? extra = null)
    {
        var settings = AiHarness.Settings();
        settings["AiProviders:Test:MaxRetries"] = "0";
        settings["App:EgressIpAddresses:0"] = EgressAddress;
        settings["App:EgressIpAddresses:1"] = EgressAddressV6;

        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        return settings;
    }

    /// <summary>The Test provider lists one model for any key.</summary>
    public void AiAccepts() => Ai.On(HttpMethod.Get, ModelsPath, _ => FakeAiProvider.Models(null, "model-a"));

    /// <summary>Binance answers a Live key with these permissions, and both accounts.</summary>
    public void BinanceAccepts(bool withdrawals = false, bool spot = true, bool futures = true, bool ipRestrict = true, long uid = Uid)
    {
        Binance.On(HttpMethod.Get, RestrictionsPath, _ => FakeBinance.Json(BinanceSamples.ApiRestrictions(ipRestrict, withdrawals, spot, futures)));
        Binance.On(HttpMethod.Get, SpotAccountPath, _ => FakeBinance.Json(BinanceSamples.SpotAccountOf(uid)));
        Binance.On(HttpMethod.Get, FuturesAccountPath, _ => FakeBinance.Json(BinanceSamples.FuturesAccount));
    }

    public static HttpResponseMessage Refusal() =>
        FakeBinance.Error(HttpStatusCode.Unauthorized, -2015, "Invalid API-key, IP, or permissions for action.");

    public async Task<SaveResult> AddAiAsync(Guid userId, string key = AiKey, string label = "Personal", string provider = Provider)
    {
        using var apiKey = SecretValue.FromString(key);

        return await Credentials.AddAiKeyAsync(userId, provider, label, apiKey, Cancellation);
    }

    public async Task<SaveResult> AddBinanceAsync(
        Guid userId,
        TradingEnvironment environment = TradingEnvironment.Live,
        string label = "Main account",
        string apiKey = ExchangeHarness.ExampleApiKey,
        string secret = ExchangeHarness.ExampleSecret,
        BinanceKeyType type = BinanceKeyType.Hmac,
        string? code = null)
    {
        using var key = SecretValue.FromString(apiKey);
        using var value = SecretValue.FromString(secret);

        return await Credentials.AddBinanceKeyAsync(userId, environment, label, type, key, value, code, Cancellation);
    }

    public async Task<SaveResult> RotateBinanceAsync(
        Guid userId,
        Guid id,
        string apiKey = ExchangeHarness.ExampleApiKey,
        string secret = ExchangeHarness.ExampleSecret,
        string? code = null)
    {
        using var key = SecretValue.FromString(apiKey);
        using var value = SecretValue.FromString(secret);

        return await Credentials.RotateBinanceKeyAsync(userId, id, BinanceKeyType.Hmac, key, value, code, Cancellation);
    }

    public async Task<SaveResult> RotateAiAsync(Guid userId, Guid id, string key = OtherAiKey)
    {
        using var apiKey = SecretValue.FromString(key);

        return await Credentials.RotateAiKeyAsync(userId, id, apiKey, Cancellation);
    }

    public Task<long> CountAsync(string table) =>
        Sql.ScalarAsync<long>(ConnectionString, $"SELECT count(*) FROM {table}", Cancellation);

    /// <summary>The IDs in <c>secrets</c> that a credential refers to.</summary>
    public async Task<(Guid ApiKey, Guid? Secret)> SecretIdsAsync(Guid id)
    {
        var apiKey = await Sql.ScalarAsync<Guid>(ConnectionString, $"SELECT api_key_id FROM credentials WHERE id = '{id}'", Cancellation);
        var secret = await Sql.ColumnAsync(ConnectionString, $"SELECT coalesce(api_secret_id::text, '') FROM credentials WHERE id = '{id}'", Cancellation);

        return (apiKey, secret.SingleOrDefault() is { Length: > 0 } text ? Guid.Parse(text) : null);
    }

    public ValueTask DisposeAsync() => App.DisposeAsync();
}
