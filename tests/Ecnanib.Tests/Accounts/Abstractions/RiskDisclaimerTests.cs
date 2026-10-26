using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ecnanib.Accounts.Abstractions;

namespace Ecnanib.Tests.Accounts.Abstractions;

/// <summary>
/// NFR-LEGAL-01/02: the disclaimer says what the law section requires, its footer summary states the two prominent clauses,
/// and its version fits the column and changes with the text (FR-ACC-08: a changed text asks every user again).
/// </summary>
public class RiskDisclaimerTests
{
    /// <summary>The paragraphs as last reviewed. A text change without a version bump fails <see cref="ChangingTheTextMeansBumpingTheVersion"/>.</summary>
    private const string PinnedVersion = "2026-10-19";
    private const string PinnedHash = "a81d608c349400567320e2d5e6e066e6ee9150cd7f537acc4179b6f3f8f02f3e";

    [Theory]
    [InlineData("software, not financial advice")]
    [InlineData("cryptocurrencies")]
    [InlineData("entire balance")]
    [InlineData("leveraged futures")]
    [InlineData("responsible")]
    [InlineData("jurisdiction")]
    [InlineData("taxes")]
    [InlineData("varies by country")]
    [InlineData("free of charge")]
    [InlineData("as is")]
    [InlineData("without warranty")]
    public void StatesWhatTheLegalRequirementsAsk(string phrase)
    {
        var text = string.Join(" ", RiskDisclaimer.Paragraphs);

        Assert.Contains(phrase, text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The sentence every page's footer carries (NFR-LEGAL-01 "prominently"): no advice, and the balance at stake.</summary>
    [Theory]
    [InlineData("software, not financial advice")]
    [InlineData("cryptocurrencies")]
    [InlineData("leveraged futures in particular")]
    [InlineData("entire balance")]
    public void TheSummaryStatesTheProminentClauses(string phrase) => Assert.Contains(phrase, RiskDisclaimer.Summary, StringComparison.Ordinal);

    /// <summary>FR-ACC-08: the texts around the acceptance say what is accepted, the checkbox names the version, and the notice confirms it.</summary>
    [Fact]
    public void TheAcceptanceTextsNameTheDisclaimerAndItsVersion()
    {
        Assert.Equal("You must read and accept the risk disclaimer before a live-mode agent can start.", RiskDisclaimer.AcceptancePrompt);
        Assert.Equal($"I have read and accept the risk disclaimer (version {RiskDisclaimer.Version}).", RiskDisclaimer.AcceptanceLabel);
        Assert.Equal("Thank you. The risk disclaimer is accepted.", RiskDisclaimer.AcceptedNotice);
    }

    [Fact]
    public void VersionIsADateStampThatFitsTheColumn()
    {
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}$"), RiskDisclaimer.Version);
        Assert.True(RiskDisclaimer.Version.Length <= 50);
        Assert.Equal(4, RiskDisclaimer.Paragraphs.Count);
    }

    /// <summary>
    /// The accounts README's rule as a test: every acceptance is kept per version, so a changed paragraph under the same
    /// version would let an acceptance of the old text stand for the new one.
    /// </summary>
    [Fact]
    public void ChangingTheTextMeansBumpingTheVersion()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", RiskDisclaimer.Paragraphs))));

        Assert.True(RiskDisclaimer.Version == PinnedVersion && hash == PinnedHash,
            "RiskDisclaimer changed: when a paragraph changes, bump RiskDisclaimer.Version (every user is asked to accept again) and re-pin "
            + $"PinnedVersion and PinnedHash in this test. Version is {RiskDisclaimer.Version}, the hash is {hash}.");
    }
}
