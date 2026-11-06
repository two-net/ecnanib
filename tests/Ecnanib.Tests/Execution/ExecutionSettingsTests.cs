using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Execution;

/// <summary>CFG-03: order execution's settings in the Agents section stop startup when invalid, and say which.</summary>
public sealed class ExecutionSettingsTests(PostgresFixture postgres)
{
    public static TheoryData<string, string, string> InvalidSettings => new()
    {
        { "Agents:PendingEntryCheckSeconds", "0", "Agents:PendingEntryCheckSeconds" },
        { "Agents:PendingEntryCheckSeconds", "6", "Agents:PendingEntryCheckSeconds" },
        { "Agents:ProtectiveStopSeconds", "4", "Agents:ProtectiveStopSeconds" },
        { "Agents:ProtectiveStopSeconds", "11", "Agents:ProtectiveStopSeconds" },
        { "Agents:ProtectiveStopSeconds", "ten", "Agents:ProtectiveStopSeconds" },
        { "Agents:MaxLimitTtlCandles", "0", "Agents:MaxLimitTtlCandles" },
        { "Agents:MaxLimitTtlCandles", "101", "Agents:MaxLimitTtlCandles" },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task An_invalid_setting_stops_startup_and_is_named(string key, string value, string named)
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), settings: new Dictionary<string, string?> { [key] = value });

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains(named, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stop_window_too_short_for_a_fill_seen_late_stops_startup()
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            settings: new Dictionary<string, string?> { ["Agents:PendingEntryCheckSeconds"] = "5", ["Agents:ProtectiveStopSeconds"] = "7" });

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains("Agents:ProtectiveStopSeconds must be at least 3 more than Agents:PendingEntryCheckSeconds", exception.ToString(), StringComparison.Ordinal);
    }
}
