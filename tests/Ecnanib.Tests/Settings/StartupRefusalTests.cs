using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Settings;

/// <summary>
/// On any error in the configuration file the application refuses to start and reports which keys
/// are wrong, without printing secret values (NFR-CFG-4).
/// </summary>
public class StartupRefusalTests
{
    private const string Marker = "SECRET-MARKER";

    [Fact]
    public void An_invalid_file_stops_the_application_and_the_wrong_keys_are_reported_without_values()
    {
        using var contentRoot = new SettingsDirectory(SettingsJson.Valid()
            .With("Database:ConnectionString", $"Host=db.example;Password={Marker}")
            .With("Security:MasterKeys:k1", $"{Marker}-that-is-not-a-key")
            .With("Email:Password", $"<{Marker}>")
            .With("Email:Port", 0)
            .With("Limits:MaxAgents", 3));

        var (exitCode, errors) = Run(contentRoot);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            $"""
            Ecnanib cannot start.
            The configuration file {contentRoot.SettingsPath} is invalid:
              Security:MasterKeys:k1: must be 32 random bytes in Base64, which is a 256-bit key
              Email:Port: must be a whole number from 1 to 65535
              Email:Password: still holds the placeholder from the template
              Limits:MaxAgents: is not a known setting

            """,
            errors,
            ignoreLineEndingDifferences: true);
        Assert.DoesNotContain(Marker, errors);
    }

    [Fact]
    public void A_missing_file_stops_the_application()
    {
        using var contentRoot = new SettingsDirectory();

        var (exitCode, errors) = Run(contentRoot);

        Assert.Equal(1, exitCode);
        Assert.StartsWith("Ecnanib cannot start.", errors);
        Assert.Contains(contentRoot.SettingsPath, errors);
        Assert.Contains("does not exist", errors);
    }

    [Theory]
    [InlineData("{ \"Database\": { \"ConnectionString\": \"SECRET-MARKER\" \n \"Oops\": tru", "not valid JSON at line 2")]
    [InlineData("{ \"Limits\": { \"Key\": \"SECRET-MARKER\" }, \"limits\": { \"key\": 1 } }", "no key appears twice")]
    [InlineData("[ \"SECRET-MARKER\" ]", "one JSON object")]
    public void A_file_that_is_not_a_JSON_object_stops_the_application_without_being_quoted(string text, string reported)
    {
        // The host reads appsettings files itself while it is created, so it meets this file
        // before the application does. The application still refuses cleanly.
        using var contentRoot = new SettingsDirectory(text);

        var (exitCode, errors) = Run(contentRoot);

        Assert.Equal(1, exitCode);
        Assert.StartsWith("Ecnanib cannot start.", errors);
        Assert.Contains(contentRoot.SettingsPath, errors);
        Assert.Contains(reported, errors);
        Assert.DoesNotContain(Marker, errors);
    }

    [Fact]
    public void The_test_host_does_not_start_either()
    {
        using var contentRoot = new SettingsDirectory(SettingsJson.Valid().With("Email:Port", 0));
        using var application = new EcnanibApplicationFactory(contentRoot);

        Assert.ThrowsAny<Exception>(() => application.Services.GetRequiredService<IServiceProvider>());
    }

    /// <summary>Runs the application's entry point. It returns at once, because it refuses to start.</summary>
    private static (int ExitCode, string Errors) Run(SettingsDirectory contentRoot)
    {
        using var errors = new StringWriter();

        var exitCode = Program.Run([$"--contentRoot={contentRoot.Path}"], errors);

        return (exitCode, errors.ToString());
    }
}
