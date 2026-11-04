using Ecnanib.Tests.Email;
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
    /// <summary>
    /// What links start with when email is on. Plain HTTP, which Development allows, so the redirect to
    /// HTTPS stays off, and not the test client's host, so a test can tell where a link came from.
    /// </summary>
    public const string PublicBaseUrl = "http://ecnanib.test";

    /// <summary>
    /// Email on, with a server nothing connects to: <see cref="CreateWithEmail"/> captures the mail.
    /// <paramref name="extra"/> goes on top, so a test can take one of these away.
    /// </summary>
    public static Dictionary<string, string?> EmailOn(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Email:Enabled"] = "true",
            ["Email:SmtpHost"] = "smtp.example.test",
            ["Email:FromAddress"] = "no-reply@example.test",
            ["App:PublicBaseUrl"] = PublicBaseUrl,
        };

        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        return settings;
    }

    /// <summary>The application with email on, and every message it sends kept in <paramref name="email"/>.</summary>
    public static AppFactory CreateWithEmail(
        PostgresFixture postgres,
        CapturedEmail email,
        Dictionary<string, string?>? settings = null,
        string? connectionString = null) =>
        Create(postgres, EmailOn(settings), email.ReplaceSender, connectionString: connectionString);

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

/// <summary>A running application with email on and its own database, shared by a test class.</summary>
public sealed class SharedEmailApp : AppFactory
{
    public SharedEmailApp(PostgresFixture postgres)
        : this(new CapturedEmail(), postgres.NewConnectionString())
    {
    }

    private SharedEmailApp(CapturedEmail email, string connectionString)
        : base(connectionString, email.ReplaceSender, AccountApp.Settings(AccountApp.EmailOn())) =>
        Email = email;

    /// <summary>Every message the application has sent. Tests share it, so look for your own address.</summary>
    public CapturedEmail Email { get; }

    public string ConnectionString => this.ConnectionString();
}
