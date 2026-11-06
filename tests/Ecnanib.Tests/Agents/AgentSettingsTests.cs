using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Agents;

/// <summary>CFG-03: the settings agents and credential revalidation read stop startup with a message that names what's wrong.</summary>
public sealed class AgentSettingsTests(PostgresFixture postgres)
{
    private const string MaxAgents = "Agents:MaxAgentsPerUser";
    private const string Revalidation = "Security:CredentialRevalidationHours";
    private const string DefaultEnvironment = "Agents:DefaultEnvironment";
    private const string TwoFactorForLive = "Security:RequireTwoFactorForLive";

    [Theory]
    [InlineData(MaxAgents, "0")]
    [InlineData(MaxAgents, "1001")]
    [InlineData(MaxAgents, "ten")]
    [InlineData(Revalidation, "0")]
    [InlineData(Revalidation, "169")]
    [InlineData(Revalidation, "daily")]
    [InlineData(DefaultEnvironment, "Live")]
    [InlineData(DefaultEnvironment, "Paper")]
    [InlineData(TwoFactorForLive, "yes")]
    public async Task An_invalid_setting_stops_startup_and_is_named(string key, string value)
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), settings: new Dictionary<string, string?> { [key] = value });

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains(key, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_shipped_settings_and_none_at_all_both_start()
    {
        await using var shipped = new AppFactory(
            postgres.NewConnectionString(),
            settings: new Dictionary<string, string?>
            {
                [MaxAgents] = "10", [Revalidation] = "24", [DefaultEnvironment] = "Demo", [TwoFactorForLive] = "false",
            });
        await using var none = new AppFactory(postgres.NewConnectionString());

        shipped.Start();
        none.Start();
    }
}
