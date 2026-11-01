using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// Hosts the real application against a test database. Use this instead of a bare
/// WebApplicationFactory, which runs as Development: it would load a developer's
/// appsettings.Development.json and migrate the database that file points to.
/// </summary>
public class AppFactory(
    string? connectionString,
    Action<IServiceCollection>? configureServices = null,
    IEnumerable<KeyValuePair<string, string?>>? settings = null,
    Action<IConfigurationBuilder>? configure = null)
    : WebApplicationFactory<Program>
{
    /// <summary>Starts the application, which throws if startup fails.</summary>
    public void Start() => _ = Server;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.Sources.Clear();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connectionString,
                ["Logging:MinimumLevel"] = "Information",
            });

            if (settings is not null)
            {
                configuration.AddInMemoryCollection(settings);
            }

            configure?.Invoke(configuration);
        });

        if (configureServices is not null)
        {
            builder.ConfigureTestServices(configureServices);
        }
    }
}

/// <summary>A running application with its own database, shared by a test class.</summary>
public sealed class SharedApp(PostgresFixture postgres) : AppFactory(postgres.NewConnectionString());
