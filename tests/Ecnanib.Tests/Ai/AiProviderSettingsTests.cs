using System.Text.Json;
using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Tests.Ai;

/// <summary>CFG-03, EXT-AI-03, NFR-SEC-10.</summary>
public sealed class AiProviderSettingsTests(PostgresFixture postgres)
{
    private const string Enabled = "AiProviders:Test:Enabled";
    private const string BaseUrl = "AiProviders:Test:BaseUrl";
    private const string Timeout = "AiProviders:Test:TimeoutSeconds";
    private const string Retries = "AiProviders:Test:MaxRetries";
    private const string Backoff = "AiProviders:Test:RetryBackoffMs";
    private const string Concurrency = "AiProviders:Test:MaxConcurrentPerCredential";

    public static TheoryData<string, string?> InvalidSettings => new()
    {
        { BaseUrl, null },
        { BaseUrl, "" },
        { BaseUrl, "test.ai.test" },
        { BaseUrl, "ftp://test.ai.test" },
        { BaseUrl, "https://test.ai.test/v1" },
        { BaseUrl, "https://test.ai.test/?x=1" },
        { BaseUrl, "https://test.ai.test/#x" },
        { BaseUrl, "https://someone:secret@test.ai.test" },
        { Enabled, "maybe" },
        { Timeout, "0" },
        { Timeout, "601" },
        { Timeout, "two minutes" },
        { Retries, "-1" },
        { Retries, "6" },
        { Backoff, "-1" },
        { Backoff, "60001" },
        { Concurrency, "0" },
        { Concurrency, "33" },
    };

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task An_invalid_setting_stops_startup_and_is_named(string key, string? value)
    {
        await using var app = App(postgres.NewConnectionString(), AiHarness.Settings(new Dictionary<string, string?> { [key] = value }));

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains(key, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("someone:secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invalid_setting_stops_startup_before_any_migration()
    {
        var database = await postgres.CreateDatabaseAsync(Cancellation);
        await using var app = App(database, AiHarness.Settings(new Dictionary<string, string?> { [BaseUrl] = null }));

        Assert.ThrowsAny<Exception>(app.Start);

        Assert.Equal(0, await Sql.ScalarAsync<long>(
            database,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'",
            Cancellation));
    }

    [Fact]
    public async Task An_adapter_without_its_section_stops_startup()
    {
        await using var app = App(postgres.NewConnectionString(), new Dictionary<string, string?>());

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains("AiProviders:Test:BaseUrl is required, because the Test adapter is registered", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outside_Development_a_provider_must_use_https()
    {
        await using var app = App(
            postgres.NewConnectionString(),
            AiHarness.Settings(new Dictionary<string, string?> { [BaseUrl] = "http://test.ai.test" }),
            environment: "Production");

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains($"{BaseUrl} must use https outside Development", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_adapters_with_one_name_stop_startup()
    {
        await using var app = App(postgres.NewConnectionString(), AiHarness.Settings(), new TestAdapter("Test"), new TestAdapter("test"));

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains("Two AI provider adapters are named Test", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_adapter_name_is_letters_and_digits()
    {
        await using var app = App(postgres.NewConnectionString(), AiHarness.Settings(), new TestAdapter("Te:st"));

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains("but Te:st isn't", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disabled_provider_needs_nothing_else_and_fails_without_sending()
    {
        await using var ai = new AiHarness(postgres, new Dictionary<string, string?> { [Enabled] = "false", [BaseUrl] = "not a URL", [Timeout] = "0" });

        var exception = await Assert.ThrowsAsync<AiException>(() => ai.CompleteAsync());

        Assert.Equal((AiFailure.Other, 0), (exception.Failure, exception.Attempts));
        Assert.Contains("disabled", exception.Message, StringComparison.Ordinal);
        Assert.Empty(ai.Provider.Requests);
        Assert.Equal(new[] { new AiProviderInfo("Test", false), new AiProviderInfo("Second", true) }, ai.Providers.All.Skip(4));
    }

    [Fact]
    public async Task Without_adapters_no_section_is_needed()
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            services => services.RemoveAll<IAiProviderAdapter>(),
            configure: configuration =>
            {
                // AppFactory gives every application the four providers' sections, which this one goes without.
                foreach (var source in configuration.Sources.OfType<MemoryConfigurationSource>()
                    .Where(source => source.InitialData?.Any(setting => setting.Key.StartsWith("AiProviders:", StringComparison.Ordinal)) == true)
                    .ToList())
                {
                    configuration.Sources.Remove(source);
                }
            });
        app.Start();

        Assert.Empty(app.Services.GetRequiredService<IAiProviders>().All);
    }

    /// <summary>A misspelt section can't hide: the real adapter's own section would be missing, which stops startup.</summary>
    [Fact]
    public async Task A_section_without_an_adapter_is_only_warned_about()
    {
        var settings = AiHarness.Settings(new Dictionary<string, string?> { ["AiProviders:Tset:BaseUrl"] = "https://test.ai.test" });
        await using var app = new LoggedApp(
            postgres.NewConnectionString(),
            settings,
            configureServices: services => AiHarness.Use(services, new FakeAiProvider(), new TestAdapter("Test"), new TestAdapter("Second")));
        await app.StopAsync();

        var warning = Assert.Single(app.FileEvents(), entry => entry.GetProperty("Properties").TryGetProperty("Section", out _));
        Assert.Equal("Warning", warning.GetProperty("Level").GetString());
        Assert.Equal("Tset", warning.GetProperty("Properties").GetProperty("Provider").GetString());
    }

    private static AppFactory App(
        string connectionString,
        Dictionary<string, string?> settings,
        params IAiProviderAdapter[] adapters) =>
        App(connectionString, settings, environment: null, adapters);

    private static AppFactory App(
        string connectionString,
        Dictionary<string, string?> settings,
        string? environment,
        params IAiProviderAdapter[] adapters) =>
        new(
            connectionString,
            services => AiHarness.Use(services, new FakeAiProvider(), adapters is [] ? [new TestAdapter("Test"), new TestAdapter("Second")] : adapters),
            settings,
            environment: environment);
}
