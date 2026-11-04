using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// Hosts the real application against a test database. Use this instead of a bare
/// WebApplicationFactory, which runs as Development: it would load a developer's
/// appsettings.Development.json and migrate the database that file points to. Real jobs are removed
/// unless a test asks for them: one with no history is due at once, so it would run on every start.
/// </summary>
public class AppFactory(
    string? connectionString,
    Action<IServiceCollection>? configureServices = null,
    IEnumerable<KeyValuePair<string, string?>>? settings = null,
    Action<IConfigurationBuilder>? configure = null,
    string? environment = null,
    bool realJobs = false)
    : WebApplicationFactory<Program>
{
    /// <summary>App:PublicBaseUrl when a test names an environment, because outside Development it's required.</summary>
    public const string PublicBaseUrl = "https://ecnanib.test";

    /// <summary>
    /// The master key every application gets unless a test's settings name Security:EncryptionKeys.
    /// The ID is long enough that masking it as a configured secret would show.
    /// </summary>
    public const string TestKeyId = "test-key-2026";

    public const string TestKey = "EKpczgbAUVP8atXODJ2g3MIY4RpBE57Sye19rQ4J0tM=";

    /// <summary>Starts the application, which throws if startup fails.</summary>
    public void Start() => _ = Server;

    /// <summary>A client on the public address, over HTTPS. HSTS skips localhost, so only this one can see it.</summary>
    public HttpClient CreatePublicClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(PublicBaseUrl), AllowAutoRedirect = false });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development by default, and only other environments run the exception handler.
        if (environment is not null)
        {
            builder.UseEnvironment(environment);
        }

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.Sources.Clear();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connectionString,
                ["Logging:MinimumLevel"] = "Information",
                ["App:PublicBaseUrl"] = environment is null ? null : PublicBaseUrl,
            });

            if (settings?.Any(setting => setting.Key.StartsWith("Security:EncryptionKeys", StringComparison.OrdinalIgnoreCase)) != true)
            {
                configuration.AddInMemoryCollection(TestKeys.Settings((TestKeyId, TestKey, true)));
            }

            if (settings is not null)
            {
                configuration.AddInMemoryCollection(settings);
            }

            configure?.Invoke(configuration);
        });

        builder.ConfigureTestServices(services =>
        {
            if (!realJobs)
            {
                services.RemoveAll<IRecurringJob>();
            }

            configureServices?.Invoke(services);
        });
    }
}

/// <summary>A running application with its own database, shared by a test class.</summary>
public sealed class SharedApp(PostgresFixture postgres) : AppFactory(postgres.NewConnectionString());
