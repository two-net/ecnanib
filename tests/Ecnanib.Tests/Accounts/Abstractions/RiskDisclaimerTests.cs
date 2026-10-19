using System.Text.RegularExpressions;
using Ecnanib.Accounts.Abstractions;

namespace Ecnanib.Tests.Accounts.Abstractions;

/// <summary>NFR-LEGAL-01/02: the disclaimer says what the law section requires, under a version that fits the column.</summary>
public class RiskDisclaimerTests
{
    [Theory]
    [InlineData("not financial advice")]
    [InlineData("entire balance")]
    [InlineData("futures")]
    [InlineData("jurisdiction")]
    [InlineData("tax")]
    [InlineData("free")]
    [InlineData("without warranty")]
    public void StatesWhatTheLegalRequirementsAsk(string phrase)
    {
        var text = string.Join(" ", RiskDisclaimer.Paragraphs);

        Assert.Contains(phrase, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VersionIsADateStampThatFitsTheColumn()
    {
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}$"), RiskDisclaimer.Version);
        Assert.True(RiskDisclaimer.Version.Length <= 50);
        Assert.Equal(4, RiskDisclaimer.Paragraphs.Count);
    }
}
