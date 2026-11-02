namespace Ecnanib.Modules.Identity.Contracts;

public enum LegalDocument
{
    TermsOfUse,
    RiskDisclosure,
}

/// <summary>
/// The versions users must have accepted (FR-AUTH-02, NFR-LEG-01). These live next to the text, in the
/// same build, so a user can't be shown one and recorded against another. Change a version only for a
/// material change: everyone has to accept again. A draft version never counts as the real document,
/// so #58 replaces both, and the first users accept again then. See docs/architecture.md.
/// </summary>
public static class LegalDocuments
{
    public const string TermsOfUseVersion = "draft-1";

    public const string RiskDisclosureVersion = "draft-1";

    /// <summary>What a session must carry in <see cref="AccountClaims.LegalDocuments"/>.</summary>
    public const string Current = $"{TermsOfUseVersion}|{RiskDisclosureVersion}";
}
