using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Identity;

/// <summary>CFG-03, for the settings accounts read.</summary>
public sealed class IdentitySettingsTests(PostgresFixture postgres)
{
    private const string PasswordMinLength = "Security:PasswordMinLength";
    private const string MaxFailedSignIns = "Security:MaxFailedSignIns";
    private const string LockoutMinutes = "Security:LockoutMinutes";
    private const string SessionIdleTimeoutMinutes = "Security:SessionIdleTimeoutMinutes";
    private const string AdminEmail = "Admin:AdminEmails:0";
    private const string BootstrapSecret = "Admin:BootstrapSecret";
    private const string EmailEnabled = "Email:Enabled";
    private const string BlockedCountry = "Legal:BlockedCountries:0";

    private const string Listed = "admin@example.test";
    private const string GoodSecret = "a bootstrap secret of plenty of characters";

    public static TheoryData<string, Dictionary<string, string?>> InvalidSettings => new()
    {
        { PasswordMinLength, new() { [PasswordMinLength] = "11" } },
        { PasswordMinLength, new() { [PasswordMinLength] = "257" } },
        { PasswordMinLength, new() { [PasswordMinLength] = "long" } },
        { MaxFailedSignIns, new() { [MaxFailedSignIns] = "0" } },
        { LockoutMinutes, new() { [LockoutMinutes] = "0" } },
        { SessionIdleTimeoutMinutes, new() { [SessionIdleTimeoutMinutes] = "0" } },
        { AdminEmail, new() { [AdminEmail] = "not an address", [BootstrapSecret] = GoodSecret } },
        { AdminEmail, new() { [AdminEmail] = "Sam <sam@example.test>", [BootstrapSecret] = GoodSecret } },
        { BootstrapSecret, new() { [AdminEmail] = Listed } },
        { BootstrapSecret, new() { [AdminEmail] = Listed, [BootstrapSecret] = " " } },
        { BootstrapSecret, new() { [AdminEmail] = Listed, [BootstrapSecret] = "too short" } },
        { BootstrapSecret, new() { [AdminEmail] = Listed, [BootstrapSecret] = "<random secret of at least 32 characters>" } },
        { EmailEnabled, new() { [EmailEnabled] = "perhaps" } },
        { BlockedCountry, new() { [BlockedCountry] = "UK" } },
        { BlockedCountry, new() { [BlockedCountry] = "us" } },
        { BlockedCountry, new() { [BlockedCountry] = "Germany" } },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task An_invalid_setting_stops_startup_and_is_named(string named, Dictionary<string, string?> settings)
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), settings: settings);

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains(named, exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Middleware is only built after migrations have run, so without this a typo would change the schema first.</summary>
    [Fact]
    public async Task An_invalid_setting_stops_startup_before_any_migration()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var database = await postgres.CreateDatabaseAsync(cancellation);

        await using var app = new AppFactory(database, settings: new Dictionary<string, string?> { [MaxFailedSignIns] = "0" });

        Assert.ThrowsAny<Exception>(app.Start);
        Assert.Equal(
            0,
            await Sql.ScalarAsync<long>(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", cancellation));
    }

    [Fact]
    public async Task No_secret_is_needed_when_email_can_verify_an_address()
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            settings: new Dictionary<string, string?> { [AdminEmail] = Listed, [EmailEnabled] = "true" });

        app.Start();
    }

    [Fact]
    public async Task No_secret_is_needed_when_nobody_is_listed()
    {
        await using var app = new AppFactory(postgres.NewConnectionString());

        app.Start();
    }
}
