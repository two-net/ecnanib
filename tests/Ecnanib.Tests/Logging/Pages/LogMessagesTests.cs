using Ecnanib.Components.Logging;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging.Pages;

public class LogMessagesTests
{
    [Theory]
    [InlineData(LogLevel.Trace, "Trace", "level-trace")]
    [InlineData(LogLevel.Debug, "Debug", "level-debug")]
    [InlineData(LogLevel.Information, "Info", "level-information")]
    [InlineData(LogLevel.Warning, "Warning", "level-warning")]
    [InlineData(LogLevel.Error, "Error", "level-error")]
    [InlineData(LogLevel.Critical, "Critical", "level-critical")]
    public void LevelsHaveALabelAndAPillClass(LogLevel level, string label, string css)
    {
        Assert.Equal(label, LogMessages.Level(level));
        Assert.Equal(css, LogMessages.LevelClass(level));
    }

    [Theory]
    [InlineData("Ecnanib.Agents.Services.AgentScheduler", "AgentScheduler")]
    [InlineData("AgentScheduler", "AgentScheduler")]
    [InlineData("Trailing.", "Trailing.")]
    public void ComponentsShowTheirTypeName(string component, string expected) => Assert.Equal(expected, LogMessages.Component(component));

    [Fact]
    public void TheEmptyStatesAreExact()
    {
        Assert.Equal("No log entries for this agent yet. The database log arrives with the logging work; until then the log is on the console.", LogMessages.NoEntries);
        Assert.Equal("No log entries match the filter.", LogMessages.NoMatches);
        Assert.Equal("2026-01-05 10:02:03 UTC", LogMessages.Stamp(new DateTimeOffset(2026, 1, 5, 12, 2, 3, TimeSpan.FromHours(2))));
    }
}
