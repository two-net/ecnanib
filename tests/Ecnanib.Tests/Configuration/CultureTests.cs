using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Ecnanib.Tests.Support;

namespace Ecnanib.Tests.Configuration;

/// <summary>
/// NFR-USE-03: the UI is English and every culture-sensitive format and parse is invariant whatever the host's locale. The
/// runtime enforces it (globalization-invariant mode in both projects, so the tests run as the application does), the
/// source never reaches for the host's clock or culture, and the chart formats its axis in English.
/// </summary>
public class CultureTests
{
    /// <summary>APIs that would let the host's locale or time zone into a page; the pages use <c>UtcStamp</c> and the invariant culture.</summary>
    private static readonly string[] ForbiddenPatterns =
    [
        "DateTime.Now", "DateTimeOffset.Now", "DateTime.Today", ".ToLocalTime(", "TimeZoneInfo.Local", "UseRequestLocalization", "AddLocalization",
        "DefaultThreadCurrentCulture", "DefaultThreadCurrentUICulture", "CultureInfo.CurrentCulture =", "IStringLocalizer",
    ];

    [Fact]
    public void BothProjectsRunInGlobalizationInvariantMode()
    {
        foreach (var path in new[] { "src/Ecnanib/Ecnanib.csproj", "tests/Ecnanib.Tests/Ecnanib.Tests.csproj" })
        {
            var project = XDocument.Load(Path.Combine(SourceTree.RepoRoot, path));
            Assert.Equal("true", project.Descendants("InvariantGlobalization").SingleOrDefault()?.Value.Trim(), ignoreCase: true);
        }
    }

    [Fact]
    public void TheBuiltApplicationAndThisProcessAreInvariant()
    {
        using var runtimeConfig = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Ecnanib.runtimeconfig.json")));
        Assert.True(runtimeConfig.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties").GetProperty("System.Globalization.Invariant").GetBoolean());

        Assert.True(AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant) && invariant);
        Assert.Equal(CultureInfo.InvariantCulture, CultureInfo.CurrentCulture);
        Assert.Equal("1234.5", 1234.5m.ToString());
        Assert.Equal("2026-01-05 00:30", new DateTime(2026, 1, 5, 0, 30, 0).ToString("yyyy-MM-dd HH:mm"));
    }

    [Fact]
    public void TheSourceNeverReachesForTheHostsClockCultureOrALocalizer()
    {
        var violations = new List<string>();
        foreach (var file in SourceTree.ApplicationSources(".cs", ".razor"))
        {
            var text = File.ReadAllText(file);
            foreach (var pattern in ForbiddenPatterns)
            {
                if (text.Contains(pattern, StringComparison.Ordinal))
                    violations.Add($"{SourceTree.RelativeToApplication(file)} mentions '{pattern}'");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void TheChartFormatsItsAxisInEnglish()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.ApplicationRoot, "Components", "Live", "PriceChart.razor.js"));

        Assert.Contains("localization: { locale: \"en-US\" }", script, StringComparison.Ordinal);
    }
}
