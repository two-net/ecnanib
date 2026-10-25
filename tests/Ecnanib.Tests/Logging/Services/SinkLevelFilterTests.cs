using Ecnanib.Logging.Services;
using Serilog.Events;
using Serilog.Parsing;
using MelLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Ecnanib.Tests.Logging.Services;

/// <summary>FR-LOG-03: a sink's minimum is its default and the shared category levels replace it per prefix.</summary>
public class SinkLevelFilterTests
{
    [Fact]
    public void TheSinkMinimumIsTheDefault()
    {
        var filter = new SinkLevelFilter(MelLevel.Information, new Dictionary<string, string>());

        Assert.Equal(LogEventLevel.Information, filter.Minimum);
        Assert.Equal(LogEventLevel.Information, filter.EffectiveLevel("Ecnanib.Agents.Services.AgentService"));
        Assert.Equal(LogEventLevel.Information, filter.EffectiveLevel(null));
        Assert.False(filter.IsEnabled(Event(LogEventLevel.Debug, "Ecnanib.Agents")));
        Assert.True(filter.IsEnabled(Event(LogEventLevel.Information, "Ecnanib.Agents")));
        Assert.True(filter.IsEnabled(Event(LogEventLevel.Error, null)));
    }

    [Fact]
    public void ACategoryLevelReplacesTheDefaultEvenBelowIt()
    {
        var filter = new SinkLevelFilter(MelLevel.Warning, new Dictionary<string, string> { ["Ecnanib.Council"] = "Debug" });

        Assert.Equal(LogEventLevel.Debug, filter.EffectiveLevel("Ecnanib.Council.Services.Council"));
        Assert.Equal(LogEventLevel.Warning, filter.EffectiveLevel("Ecnanib.Agents.Services.AgentService"));
        Assert.True(filter.IsEnabled(Event(LogEventLevel.Debug, "Ecnanib.Council.Services.Council")));
        Assert.False(filter.IsEnabled(Event(LogEventLevel.Information, "Ecnanib.Agents.Services.AgentService")));
    }

    [Fact]
    public void TheLongestPrefixWins()
    {
        var filter = new SinkLevelFilter(MelLevel.Information, new Dictionary<string, string> { ["Microsoft"] = "Warning", ["Microsoft.EntityFrameworkCore"] = "Error" });

        Assert.Equal(LogEventLevel.Error, filter.EffectiveLevel("Microsoft.EntityFrameworkCore.Database.Command"));
        Assert.Equal(LogEventLevel.Warning, filter.EffectiveLevel("Microsoft.AspNetCore.Hosting"));
        Assert.Equal(LogEventLevel.Information, filter.EffectiveLevel("Npgsql"));
    }

    [Fact]
    public void APrefixMatchesOnlyAWholeSegment()
    {
        var filter = new SinkLevelFilter(MelLevel.Information, new Dictionary<string, string> { ["Binance"] = "Warning" });

        Assert.Equal(LogEventLevel.Warning, filter.EffectiveLevel("Binance"));
        Assert.Equal(LogEventLevel.Warning, filter.EffectiveLevel("Binance.RestClient"));
        Assert.Equal(LogEventLevel.Information, filter.EffectiveLevel("BinanceX.RestClient"));
        Assert.True(SinkLevelFilter.Matches("A.B", "A.B.C"));
        Assert.False(SinkLevelFilter.Matches("A.B", "A.BC"));
        Assert.False(SinkLevelFilter.Matches("A.B.C", "A.B"));
    }

    [Fact]
    public void UnparsableLevelsAndBlankKeysAreIgnored()
    {
        var filter = new SinkLevelFilter(MelLevel.Debug, new Dictionary<string, string> { ["Ecnanib"] = "loud", [" "] = "Error" });

        Assert.Equal(LogEventLevel.Debug, filter.EffectiveLevel("Ecnanib.Agents"));
    }

    internal static LogEvent Event(LogEventLevel level, string? sourceContext, string message = "hello", Exception? exception = null)
    {
        var properties = sourceContext is null ? [] : new[] { new LogEventProperty("SourceContext", new ScalarValue(sourceContext)) };
        return new LogEvent(new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero), level, exception, new MessageTemplateParser().Parse(message), properties);
    }
}
