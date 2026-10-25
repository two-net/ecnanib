using Ecnanib.Components.Logging;
using Ecnanib.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging.Pages;

public class LogFiltersTests
{
    private static readonly Guid AgentId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    [Theory]
    [InlineData("info", LogLevel.Information)]
    [InlineData("WARNING", LogLevel.Warning)]
    [InlineData(" error ", LogLevel.Error)]
    [InlineData("Information", LogLevel.Information)]
    public void LevelsParseBySlugOrName(string value, LogLevel expected) => Assert.Equal(expected, LogFilters.ParseLevel(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("loud")]
    [InlineData("none")]
    [InlineData("3")]
    public void OtherLevelTextsMeanAll(string? value) => Assert.Null(LogFilters.ParseLevel(value));

    [Fact]
    public void TimesAreReadAsUtcAndRoundTripThroughTheInputFormat()
    {
        var at = new DateTimeOffset(2026, 1, 5, 10, 30, 0, TimeSpan.Zero);

        Assert.Equal(at, LogFilters.ParseTime("2026-01-05T10:30"));
        Assert.Equal(at, LogFilters.ParseTime("2026-01-05T10:30:00"));
        Assert.Equal(at.Date, LogFilters.ParseTime("2026-01-05")!.Value.UtcDateTime);
        Assert.Equal(at, LogFilters.ParseTime("2026-01-05T12:30:00+02:00"));
        Assert.Null(LogFilters.ParseTime("yesterday"));
        Assert.Null(LogFilters.ParseTime(null));
        Assert.Equal("2026-01-05T10:30", LogFilters.ForInput(at));
        Assert.Equal("", LogFilters.ForInput(null));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("0", 1)]
    [InlineData("-3", 1)]
    [InlineData("x", 1)]
    [InlineData("4", 4)]
    public void PagesStartAtOne(string? value, int expected) => Assert.Equal(expected, LogFilters.ParsePage(value));

    [Fact]
    public void TheQueryStringCarriesOnlyWhatIsSetAndRoundTrips()
    {
        var query = LogFilters.Parse(AgentId, "warning", " Ecnanib.Council.Services.Council ", "100% of", "2026-01-05T10:30", null, "2");

        Assert.Equal(new LogQuery(AgentId, LogLevel.Warning, "Ecnanib.Council.Services.Council", "100% of", new DateTimeOffset(2026, 1, 5, 10, 30, 0, TimeSpan.Zero), null, 2), query);
        Assert.True(LogFilters.IsFiltered(query));
        Assert.Equal("level=warning&component=Ecnanib.Council.Services.Council&q=100%25%20of&from=2026-01-05T10:30&page=3", LogFilters.Query(query, 3));
        Assert.Equal("level=warning&component=Ecnanib.Council.Services.Council&q=100%25%20of&from=2026-01-05T10:30", LogFilters.Query(query, 1));

        var none = LogFilters.Parse(AgentId, null, "", "  ", null, null, null);
        Assert.False(LogFilters.IsFiltered(none));
        Assert.Equal("", LogFilters.Query(none, 1));
        Assert.Equal("page=2", LogFilters.Query(none, 2));
    }
}
