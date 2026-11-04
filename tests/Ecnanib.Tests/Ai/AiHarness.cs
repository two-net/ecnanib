using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Ai;

/// <summary>
/// The application with its own database, two test providers named Test and Second, and a
/// <see cref="FakeAiProvider"/> behind both. The pipeline keeps its credential slots in memory, so each
/// test gets its own.
/// </summary>
internal sealed class AiHarness : IAsyncDisposable
{
    public const string TestHost = "test.ai.test";
    public const string SecondHost = "second.ai.test";
    public const string ApiKey = "test-api-key";
    public const string Model = "test-model";

    public AiHarness(PostgresFixture postgres, Dictionary<string, string?>? settings = null)
    {
        App = new AppFactory(postgres.NewConnectionString(), services => Use(services, Provider, Adapter, Second), Settings(settings));
        App.Start();
    }

    public FakeAiProvider Provider { get; } = new();

    public TestAdapter Adapter { get; } = new("Test");

    public TestAdapter Second { get; } = new("Second");

    public AppFactory App { get; }

    public IAiProviders Providers => App.Services.GetRequiredService<IAiProviders>();

    public IAiCompletions Completions => App.Services.GetRequiredService<IAiCompletions>();

    public static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Both providers' settings, with no backoff so that retries are quick, and then <paramref name="overrides"/>.</summary>
    public static Dictionary<string, string?> Settings(IEnumerable<KeyValuePair<string, string?>>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AiProviders:Test:BaseUrl"] = $"https://{TestHost}",
            ["AiProviders:Test:TimeoutSeconds"] = "10",
            ["AiProviders:Test:MaxRetries"] = "2",
            ["AiProviders:Test:RetryBackoffMs"] = "0",
            ["AiProviders:Test:MaxConcurrentPerCredential"] = "4",
            ["AiProviders:Second:BaseUrl"] = $"https://{SecondHost}",
            ["AiProviders:Second:RetryBackoffMs"] = "0",
        };

        foreach (var (key, value) in overrides ?? [])
        {
            settings[key] = value;
        }

        return settings;
    }

    /// <summary>Registers <paramref name="adapters"/>, and sends what they build to <paramref name="handler"/>.</summary>
    public static void Use(IServiceCollection services, HttpMessageHandler handler, params IAiProviderAdapter[] adapters)
    {
        foreach (var adapter in adapters)
        {
            services.AddSingleton<IAiProviderAdapter>(adapter);
        }

        services.AddHttpClient("AiProviders")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
    }

    /// <summary>A key on its own credential, unless <paramref name="credentialId"/> names one.</summary>
    public static AiKey Key(string provider = "Test", string apiKey = ApiKey, Guid? credentialId = null) =>
        new(provider, credentialId ?? Guid.NewGuid(), SecretValue.FromString(apiKey));

    public static AiRequest Request(string prompt = "Should the agent buy?", AiModelPrice? price = null) =>
        new(Model, AiRole.Strategist, "You are the Strategist.", [AiMessage.User(prompt)], 1000) { Price = price };

    public Task<AiResponse> CompleteAsync(AiKey? key = null, AiRequest? request = null, CancellationToken? cancellationToken = null) =>
        Completions.CompleteAsync(key ?? Key(), request ?? Request(), cancellationToken ?? Cancellation);

    public ValueTask DisposeAsync() => App.DisposeAsync();
}
