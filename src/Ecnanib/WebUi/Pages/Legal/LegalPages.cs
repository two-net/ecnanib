using Ecnanib.Accounts;

namespace Ecnanib.WebUi.Pages.Legal;

/// <summary>
/// Where the legal documents are, and what they are called. The version of each is kept by
/// Accounts, in <see cref="LegalDocuments"/>, because Accounts records what a user accepted.
/// </summary>
public static class LegalPages
{
    public const string TermsOfServicePath = "/legal/terms";
    public const string PrivacyPolicyPath = "/legal/privacy";
    public const string RiskDisclosurePath = "/legal/risk-disclosure";

    /// <summary>The path of the page that shows a document.</summary>
    public static string PathOf(LegalDocument document) => document switch
    {
        LegalDocument.TermsOfService => TermsOfServicePath,
        LegalDocument.PrivacyPolicy => PrivacyPolicyPath,
        LegalDocument.RiskDisclosure => RiskDisclosurePath,
        _ => throw new ArgumentOutOfRangeException(nameof(document), document, "The document has no page."),
    };

    /// <summary>The name of a document, as a title.</summary>
    public static string TitleOf(LegalDocument document) => document switch
    {
        LegalDocument.TermsOfService => "Terms of Service",
        LegalDocument.PrivacyPolicy => "Privacy Policy",
        LegalDocument.RiskDisclosure => "Risk disclosure",
        _ => throw new ArgumentOutOfRangeException(nameof(document), document, "The document has no title."),
    };

    /// <summary>The name of a document, as it is written inside a sentence.</summary>
    public static string NameOf(LegalDocument document) =>
        document == LegalDocument.RiskDisclosure ? "risk disclosure" : TitleOf(document);
}
