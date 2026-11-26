using Ecnanib.Accounts;

namespace Ecnanib.WebUi.Pages.Legal;

/// <summary>
/// Where the legal documents are, what they are called, and the texts of their pages, in one
/// place (NFR-USA-4). The version of each is kept by Accounts, in <see cref="LegalDocuments"/>,
/// because Accounts records what a user accepted.
/// </summary>
public static class LegalPages
{
    public const string TermsOfServicePath = "/legal/terms";
    public const string PrivacyPolicyPath = "/legal/privacy";
    public const string RiskDisclosurePath = "/legal/risk-disclosure";

    /// <summary>
    /// What a link to a document says, in its own text, where the document opens beside the
    /// page: on a form, so what was typed into it stays (NFR-USA-1).
    /// </summary>
    public static string OpensInNewTab => "opens in a new tab";

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

    /// <summary>What a document says of the version that a registration accepts, under its title.</summary>
    public static string Version(LegalDocument document) => $"Version {LegalDocuments.CurrentVersionOf(document)}";

    /// <summary>What stands in the place of a document whose text is not written yet.</summary>
    public static string Draft =>
        "This document is a draft. Its text is being prepared, and replaces this page before the service opens to the public.";

    /// <summary>The name of a document, as it is written inside a sentence.</summary>
    public static string NameOf(LegalDocument document) =>
        document == LegalDocument.RiskDisclosure ? "risk disclosure" : TitleOf(document);
}
