using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Email;

/// <summary>CFG-03 and EXT-EML-01, for the Email section.</summary>
public sealed class EmailSettingsTests(PostgresFixture postgres)
{
    private const string Enabled = "Email:Enabled";
    private const string SmtpHost = "Email:SmtpHost";
    private const string SmtpPort = "Email:SmtpPort";
    private const string UseTls = "Email:UseTls";
    private const string Username = "Email:Username";
    private const string Password = "Email:Password";
    private const string FromAddress = "Email:FromAddress";

    public static TheoryData<string, Dictionary<string, string?>> InvalidSettings => new()
    {
        { SmtpHost, AccountApp.EmailOn(new() { [SmtpHost] = null }) },
        { SmtpHost, AccountApp.EmailOn(new() { [SmtpHost] = "<smtp host>" }) },
        { SmtpPort, AccountApp.EmailOn(new() { [SmtpPort] = "0" }) },
        { SmtpPort, AccountApp.EmailOn(new() { [SmtpPort] = "65536" }) },
        { SmtpPort, AccountApp.EmailOn(new() { [SmtpPort] = "submission" }) },
        { UseTls, AccountApp.EmailOn(new() { [UseTls] = "perhaps" }) },
        { Username, AccountApp.EmailOn(new() { [Username] = "mailer" }) },
        { Username, AccountApp.EmailOn(new() { [Password] = "a password with no user" }) },
        { FromAddress, AccountApp.EmailOn(new() { [FromAddress] = null }) },
        { FromAddress, AccountApp.EmailOn(new() { [FromAddress] = "not an address" }) },
        { FromAddress, AccountApp.EmailOn(new() { [FromAddress] = "Ecnanib <no-reply@example.test>" }) },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task An_invalid_setting_stops_startup_and_is_named(string named, Dictionary<string, string?> settings)
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), settings: settings);

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains(named, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invalid_setting_stops_startup_before_any_migration()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var database = await postgres.CreateDatabaseAsync(cancellation);

        await using var app = new AppFactory(database, settings: AccountApp.EmailOn(new() { [SmtpHost] = null }));

        Assert.ThrowsAny<Exception>(app.Start);
        Assert.Equal(
            0,
            await Sql.ScalarAsync<long>(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", cancellation));
    }

    /// <summary>A reset link is as good as a password, so outside Development it never crosses a network in clear.</summary>
    [Fact]
    public async Task Turning_tls_off_is_refused_outside_development()
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            settings: AccountApp.EmailOn(new() { [UseTls] = "false", ["App:PublicBaseUrl"] = AppFactory.PublicBaseUrl }),
            environment: Environments.Production);

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains(UseTls, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task Tls_can_be_off_for_a_server_on_this_machine(string host)
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            settings: AccountApp.EmailOn(new() { [UseTls] = "false", [SmtpHost] = host, ["App:PublicBaseUrl"] = AppFactory.PublicBaseUrl }),
            environment: Environments.Production);

        app.Start();
    }

    [Fact]
    public async Task Tls_can_be_off_in_development()
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), settings: AccountApp.EmailOn(new() { [UseTls] = "false" }));

        app.Start();
    }

    [Fact]
    public async Task A_server_that_needs_a_sign_in_starts()
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            settings: AccountApp.EmailOn(new() { [Username] = "mailer", [Password] = "an smtp password", [SmtpPort] = "465" }));

        app.Start();
    }

    /// <summary>A deployment without email needs none of the section, however it's filled in.</summary>
    [Fact]
    public async Task With_email_off_the_rest_is_ignored()
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            settings: new Dictionary<string, string?>
            {
                [Enabled] = "false",
                [SmtpHost] = "<smtp host>",
                [SmtpPort] = "0",
                [FromAddress] = "not an address",
                [Username] = "mailer",
            });

        app.Start();
    }
}
