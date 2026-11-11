namespace Ecnanib.Accounts;

/// <summary>A document a user accepts when registering (FR-ACC-8).</summary>
/// <remarks>
/// The name of a member is its code in the database, so renaming one changes stored data and
/// needs a migration.
/// </remarks>
public enum LegalDocument
{
    TermsOfService,
    PrivacyPolicy,
    RiskDisclosure,
}

/// <summary>
/// The version of each document that is in force. A registration accepts exactly these, and they
/// are what is recorded with the account (FR-ACC-8).
/// </summary>
/// <remarks>
/// The texts are pages of the Web UI. Whoever changes a text changes its version here in the
/// same commit, so that an acceptance always names the text the user saw. See docs/accounts.md.
/// </remarks>
public static class LegalDocuments
{
    /// <summary>Every document a registration must accept.</summary>
    public static IReadOnlyList<LegalDocument> All { get; } = Enum.GetValues<LegalDocument>();

    /// <summary>The version in force. The texts are drafts until the legal wording is written.</summary>
    public static string CurrentVersionOf(LegalDocument document) => document switch
    {
        LegalDocument.TermsOfService => "0.1",
        LegalDocument.PrivacyPolicy => "0.1",
        LegalDocument.RiskDisclosure => "0.1",
        _ => throw new ArgumentOutOfRangeException(nameof(document), document, "The document has no version."),
    };
}

/// <summary>One document in the version a visitor accepted on the registration form.</summary>
public sealed record AcceptedDocument(LegalDocument Document, string Version);
