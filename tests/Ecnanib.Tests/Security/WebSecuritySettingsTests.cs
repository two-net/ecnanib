using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Security;

/// <summary>CFG-03, for the settings the web security baseline reads.</summary>
public sealed class WebSecuritySettingsTests(PostgresFixture postgres)
{
    private const string BaseUrl = "App:PublicBaseUrl";
    private const string Proxy = "Security:TrustedProxies:0";
    private const string RequestsPerMinute = "Security:RateLimits:RequestsPerMinute";
    private const string AuthenticationRequestsPerMinute = "Security:RateLimits:AuthenticationRequestsPerMinute";

    public static TheoryData<string, Dictionary<string, string?>> InvalidSettings => new()
    {
        { BaseUrl, new() { [BaseUrl] = null } },
        { BaseUrl, new() { [BaseUrl] = "" } },
        { BaseUrl, new() { [BaseUrl] = "ecnanib.test" } },
        { BaseUrl, new() { [BaseUrl] = "ftp://ecnanib.test" } },
        { BaseUrl, new() { [BaseUrl] = "http://ecnanib.test" } },
        { Proxy, new() { [Proxy] = "proxy.ecnanib.test" } },
        { Proxy, new() { [Proxy] = "10.0.0.0/99" } },
        { RequestsPerMinute, new() { [RequestsPerMinute] = "0" } },
        { RequestsPerMinute, new() { [RequestsPerMinute] = "many" } },
        { AuthenticationRequestsPerMinute, new() { [AuthenticationRequestsPerMinute] = "0" } },
        { "RequestPerMinute", new() { ["Security:RateLimits:RequestPerMinute"] = "60" } },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task An_invalid_setting_stops_startup_and_is_named(string named, Dictionary<string, string?> settings)
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), settings: settings, environment: Environments.Production);

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains(named, exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Middleware is only built after migrations have run, so without this a typo would change the schema first.</summary>
    [Fact]
    public async Task An_invalid_setting_stops_startup_before_any_migration()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var database = await postgres.CreateDatabaseAsync(cancellation);
        await using var app = new AppFactory(
            database,
            settings: new Dictionary<string, string?> { [RequestsPerMinute] = "0" },
            environment: Environments.Production);

        Assert.ThrowsAny<Exception>(app.Start);

        Assert.Equal(
            0,
            await Sql.ScalarAsync<long>(
                database,
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'",
                cancellation));
    }

    /// <summary>Local setup needs no new setting.</summary>
    [Fact]
    public async Task Development_doesnt_need_a_public_address()
    {
        await using var app = new AppFactory(postgres.NewConnectionString());

        app.Start();
    }
}
