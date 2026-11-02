using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Identity;

/// <summary>
/// The real application with its limits out of the way. A test host has no peer address, so every
/// request in a class would share one bucket. RateLimitTests covers the limits themselves.
/// </summary>
internal static class AccountApp
{
    public static Dictionary<string, string?> Settings(Dictionary<string, string?>? extra = null) =>
        new(extra ?? [])
        {
            ["Security:RateLimits:RequestsPerMinute"] = "100000",
            ["Security:RateLimits:AuthenticationRequestsPerMinute"] = "100000",
        };

    public static AppFactory Create(
        PostgresFixture postgres,
        Dictionary<string, string?>? settings = null,
        Action<IServiceCollection>? services = null,
        string? environment = null,
        string? connectionString = null,
        bool realJobs = false) =>
        new(connectionString ?? postgres.NewConnectionString(), services, Settings(settings), environment: environment, realJobs: realJobs);

    public static string ConnectionString(this AppFactory app) =>
        app.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Default"]!;
}

/// <summary>A running application with its own database, shared by a test class.</summary>
public sealed class SharedAccountApp(PostgresFixture postgres)
    : AppFactory(postgres.NewConnectionString(), settings: AccountApp.Settings())
{
    public string ConnectionString => this.ConnectionString();
}
