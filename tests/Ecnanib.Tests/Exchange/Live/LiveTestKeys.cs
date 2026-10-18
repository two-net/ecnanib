using System.Text.Json;
using Ecnanib.Exchange;
using Ecnanib.Exchange.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Exchange.Live;

/// <summary>
/// Opt-in live tests read <c>tests/.exchange-keys.json</c> (git-ignored):
/// <code>{ "publicProduction": true, "spotTestnet": { "apiKey": "...", "apiSecret": "..." }, "futuresTestnet": { "apiKey": "...", "apiSecret": "..." } }</code>
/// Every test skips when its section is absent, so the default <c>dotnet test</c> never touches the network.
/// </summary>
public sealed class LiveTestKeys
{
    public const string FileName = ".exchange-keys.json";

    public bool PublicProduction { get; init; }

    public LiveKeyPair? SpotTestnet { get; init; }

    public LiveKeyPair? FuturesTestnet { get; init; }

    public sealed record LiveKeyPair(string ApiKey, string ApiSecret);

    public static LiveTestKeys? Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", FileName);
            if (File.Exists(candidate))
                return JsonSerializer.Deserialize<LiveTestKeys>(File.ReadAllText(candidate), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            directory = directory.Parent;
        }

        return null;
    }

    public static void RequirePublicProduction()
    {
        if (Load() is not { PublicProduction: true })
            Assert.Skip($"Live test: set \"publicProduction\": true in tests/{FileName} to run against api.binance.com without keys.");
    }

    public static ExchangeKeyHandle RequireSpotTestnet() => Require(k => k.SpotTestnet, "spotTestnet", "spot-testnet");

    public static ExchangeKeyHandle RequireFuturesTestnet() => Require(k => k.FuturesTestnet, "futuresTestnet", "futures-testnet");

    private static ExchangeKeyHandle Require(Func<LiveTestKeys, LiveKeyPair?> select, string section, string keyId)
    {
        var pair = Load() is { } keys ? select(keys) : null;
        if (pair is null || string.IsNullOrWhiteSpace(pair.ApiKey) || string.IsNullOrWhiteSpace(pair.ApiSecret))
            Assert.Skip($"Live test: add \"{section}\" to tests/{FileName} to run.");
        return new ExchangeKeyHandle(keyId, ExchangeEnvironment.Testnet, new ExchangeCredentials(pair!.ApiKey, pair.ApiSecret));
    }

    /// <summary>The module as the application registers it, with default configuration and console logging.</summary>
    public static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddExchange(new ConfigurationBuilder().Build());
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        return services.BuildServiceProvider();
    }
}
