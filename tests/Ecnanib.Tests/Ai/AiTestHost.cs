using System.Net;
using System.Text.Json;
using Ecnanib.Ai;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Ai.Configuration;
using Ecnanib.Ai.Providers;
using Ecnanib.Ai.Resilience;
using Ecnanib.Tests.Ai.Fixtures;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Ai;

/// <summary>Builds the AI module against a <see cref="FakeHttpMessageHandler"/>, either through the real container or directly.</summary>
internal static class AiTestHost
{
    public const string Key = "sk-test-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd";

    /// <summary>How a provider might echo the key back in an error message (prefix and suffix around asterisks).</summary>
    public const string ProviderMaskedKey = "sk-test-AB************89abcd";

    public const string Masked = "…abcd";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static AiKeyHandle Handle(AiProvider provider, string? key = null) => new("key-1", provider, new AiCredentials(key ?? Key));

    public static JsonElement Schema() =>
        JsonDocument.Parse("""{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}""").RootElement.Clone();

    public static HttpResponseMessage Json(string fixture, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers) =>
        FakeHttpMessageHandler.Json(status, Fixture.Read(fixture, Key, ProviderMaskedKey), headers);

    /// <summary>The module as the application registers it, with the fake handler as the primary HTTP handler.</summary>
    public static ServiceProvider BuildServices(FakeHttpMessageHandler handler, TestLoggerProvider? logs = null, Dictionary<string, string?>? config = null, TimeProvider? time = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config ?? new Dictionary<string, string?>()).Build();
        var services = new ServiceCollection();
        services.AddAiProviders(configuration);
        services.AddHttpClient(AiProviderClientBase.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        if (time is not null)
            services.AddSingleton(time);
        if (logs is not null)
            services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        return services.BuildServiceProvider();
    }

    public static IAiProviderClient Client(this ServiceProvider services, AiProvider provider) =>
        services.GetRequiredService<IAiClientFactory>().GetClient(provider);

    /// <summary>A client built by hand, for tests that need a short real HTTP timeout or a fixed jitter.</summary>
    public static IAiProviderClient Direct(AiProvider provider, FakeHttpMessageHandler handler, AiOptions? options = null, TimeProvider? time = null, TestLoggerProvider? logs = null, TimeSpan? timeout = null, double jitter = 1.0)
    {
        options ??= new AiOptions();
        time ??= TimeProvider.System;
        var opts = Options.Create(options);
        var loggers = logs is null ? NullLoggerFactory.Instance : TestLogging.CreateFactory(logs);
        var http = new StubHttpClientFactory(handler, timeout ?? options.CallTimeout);
        var retry = new AiRetryPolicy(opts, time, loggers.CreateLogger<AiRetryPolicy>(), () => jitter);
        return provider switch
        {
            AiProvider.Claude => new ClaudeClient(http, opts, retry, time, loggers.CreateLogger<ClaudeClient>()),
            AiProvider.Gemini => new GeminiClient(http, opts, retry, time, loggers.CreateLogger<GeminiClient>()),
            AiProvider.Gpt => new GptClient(http, opts, retry, time, loggers.CreateLogger<GptClient>()),
            AiProvider.Grok => new GrokClient(http, opts, retry, time, loggers.CreateLogger<GrokClient>()),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
    }

    public static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met.");
            await Task.Delay(1, Ct);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler, TimeSpan timeout) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = timeout };
    }
}
