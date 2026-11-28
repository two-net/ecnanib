using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.WebUi.Pages.Legal;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// The risk disclosure states that automated trading can lose all allocated capital, that the
/// aggressive profile raises that risk, that leverage can cause rapid losses, that nothing in
/// the service is investment advice, and that past results do not predict future results
/// (NFR-LEG-1). It carries a version, which a registration records (FR-ACC-8). See "Legal
/// documents" in docs/accounts.md.
/// </summary>
public partial class RiskDisclosureTests
{
    private const string Ada = "ada@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 28, 9, 0, 0, TimeSpan.Zero);

    // The version of the text, and what its words come to. The two are pinned together: see
    // The_text_of_a_version_does_not_change.
    private const string Version = "1.0";
    private const string Fingerprint = "b3fd50da0972a2fb81de9ea4cd922d4afe1a5029d4e62c43ecd2a287af3a2fa9";

    [Theory]
    [InlineData("Automated trading can lose all of the capital that you allocate to an agent.")]
    [InlineData("Ecnanib has one trading profile, and it is aggressive. That raises the risk of losing your capital.")]
    [InlineData("On Futures an agent trades with leverage, and leverage can cause rapid losses.")]
    [InlineData("Nothing in this service is investment advice.")]
    [InlineData("Past results do not predict future results.")]
    public void The_disclosure_states_each_risk_that_it_must(string statement)
    {
        // Each opens a section of its own, so it is the first thing read under a heading.
        Assert.Contains(
            RiskDisclosurePages.Sections,
            section => section.Paragraphs[0].StartsWith(statement, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_page_shows_the_whole_text_under_its_version()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        // A page writes an apostrophe encoded: the text is read as a browser shows it.
        var page = WebUtility.HtmlDecode(await client.PageAsync(LegalPages.RiskDisclosurePath));

        Assert.Contains("<h1>Risk disclosure</h1>", page);
        Assert.Contains($"<p>Version {Version}</p>", page);

        // The sections, by their headings and in their order, under the one first heading.
        Assert.Equal(
            [
                "You can lose all of the capital you allocate",
                "The aggressive profile raises that risk",
                "Leverage can cause rapid losses",
                "The limits reduce the risk and do not remove it",
                "No AI model is a proven trader",
                "Nothing in this service is investment advice",
                "Past results do not predict future results",
            ],
            SecondHeading().Matches(page).Select(heading => heading.Groups[1].Value));

        // Every paragraph of the text, in the order of the text, after the version.
        var paragraphs = Paragraph().Matches(MainOf(page)).Select(paragraph => paragraph.Groups[1].Value).ToList();

        Assert.Equal(
            [
                $"Version {Version}",
                .. RiskDisclosurePages.Opening,
                .. RiskDisclosurePages.Sections.SelectMany(section => section.Paragraphs),
            ],
            paragraphs);

        // What the specification requires, as the page says it.
        Assert.Contains("<p>Automated trading can lose all of the capital that you allocate to an agent. ", page);
        Assert.Contains("<p>Ecnanib has one trading profile, and it is aggressive. That raises the risk of losing your capital.</p>", page);
        Assert.Contains("<p>On Futures an agent trades with leverage, and leverage can cause rapid losses.</p>", page);
        Assert.Contains("<p>Nothing in this service is investment advice. ", page);
        Assert.Contains("<p>Past results do not predict future results. ", page);

        // The text took the place of the notice that it was being prepared.
        Assert.DoesNotContain(LegalPages.Draft, page);
    }

    [Fact]
    public async Task A_registration_records_the_version_of_the_disclosure_that_the_form_showed()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        using var client = application.CreateClient();

        // The box of the form carries the version of the text that its link opens.
        var form = await client.PageAsync("/register");
        Assert.Contains($"name=\"RiskDisclosure\" type=\"checkbox\" required value=\"{Version}\"", form);

        var (path, _) = await client.SubmitAsync(
            "/register",
            "register",
            ("Email", Ada),
            ("Password", AccountsApplication.Password),
            ("TermsOfService", LegalDocuments.CurrentVersionOf(LegalDocument.TermsOfService)),
            ("PrivacyPolicy", LegalDocuments.CurrentVersionOf(LegalDocument.PrivacyPolicy)),
            ("RiskDisclosure", Version));

        Assert.Equal("/register/sent", path);

        var user = StoredUser.Of(application.Database, Ada)!;
        Assert.Contains(
            new StoredAcceptance(user.Id, "RiskDisclosure", Version, Morning),
            StoredAcceptance.All(application.Database));
    }

    [Fact]
    public async Task The_draft_of_the_disclosure_is_no_longer_accepted()
    {
        using var application = new AccountsApplication();

        // What a form posts that was opened while the disclosure was a draft, at version 0.1.
        var result = await application.RegisterAsync(
            Ada,
            accepted:
            [
                .. AccountsApplication.EveryDocument.Where(accepted => accepted.Document != LegalDocument.RiskDisclosure),
                new AcceptedDocument(LegalDocument.RiskDisclosure, "0.1"),
            ]);

        Assert.Equal(RegistrationOutcome.Invalid, result.Outcome);
        Assert.Equal([RegistrationProblem.RiskDisclosureNotAccepted], result.Problems);
        Assert.Empty(StoredUser.All(application.Database));
        Assert.Empty(StoredAcceptance.All(application.Database));
    }

    [Fact]
    public void The_text_of_a_version_does_not_change()
    {
        // An acceptance names a version, and the version names the text that the user saw. So a
        // text that changes takes a new version: the two are pinned here together.
        var fingerprint = FingerprintOf(
        [
            .. RiskDisclosurePages.Opening,
            .. RiskDisclosurePages.Sections.SelectMany(section => (IEnumerable<string>)[section.Heading, .. section.Paragraphs]),
        ]);

        Assert.True(
            (Version, Fingerprint) == (LegalDocuments.CurrentVersionOf(LegalDocument.RiskDisclosure), fingerprint),
            $"""
            The risk disclosure is pinned at version {Version} with the fingerprint {Fingerprint}.
            It is at version {LegalDocuments.CurrentVersionOf(LegalDocument.RiskDisclosure)} with the fingerprint {fingerprint}.
            A text that changed takes a new version in LegalDocuments, in the same commit: users are on record as having
            accepted the old text under the old version. Then pin the new version and the new fingerprint here, together.
            """);
    }

    // What the words of a text come to: its lines, one below the other, hashed.
    private static string FingerprintOf(IEnumerable<string> lines) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));

    // What a page holds between the tags of its content.
    private static string MainOf(string page)
    {
        var from = Content().Match(page);
        Assert.True(from.Success, "The page has no main element.");

        return page[(from.Index + from.Length)..page.IndexOf("</main>", StringComparison.Ordinal)];
    }

    [GeneratedRegex("<main[^>]*>")]
    private static partial Regex Content();

    [GeneratedRegex("<h2>([^<]*)</h2>")]
    private static partial Regex SecondHeading();

    [GeneratedRegex("<p>([^<]*)</p>")]
    private static partial Regex Paragraph();
}
