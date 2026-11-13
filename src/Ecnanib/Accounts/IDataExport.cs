using System.Text.Json.Serialization;
using Ecnanib.Platform.Persistence;

namespace Ecnanib.Accounts;

/// <summary>
/// The export of a user's data: everything that is stored about them, for them to keep
/// (NFR-SEC-9). See docs/accounts.md.
/// </summary>
/// <remarks>
/// An export holds no secret (FR-KEY-8): not the hash of the password, not the key of two-factor
/// authentication, not the hash of a token or of a recovery code, and not a key of an AI provider
/// or of Binance. None of them says anything about the user, and each is of use only to someone
/// who wants in.
/// </remarks>
public interface IDataExport
{
    /// <summary>
    /// What is stored about the user who is signed in on a request, as it is at this moment. It
    /// fails for a request that is not signed in.
    /// </summary>
    /// <returns>Null when the account was deleted since the request was signed in.</returns>
    Task<ExportedData?> ExportAsync(HttpContext request);
}

/// <summary>What is stored about one user.</summary>
/// <param name="ExportedAt">When the export was made.</param>
/// <param name="Account">The account itself.</param>
/// <param name="LegalAcceptances">The legal documents the user accepted, oldest first.</param>
/// <param name="Sessions">The browsers the user is signed in on, oldest first.</param>
/// <param name="AuditEvents">The security-relevant actions of the user that the audit trail holds, oldest first.</param>
public sealed record ExportedData(
    DateTimeOffset ExportedAt,
    ExportedAccount Account,
    IReadOnlyList<ExportedAcceptance> LegalAcceptances,
    IReadOnlyList<ExportedSession> Sessions,
    IReadOnlyList<ExportedAuditEvent> AuditEvents)
{
    /// <summary>
    /// What the components above Accounts keep for the user, each under the name of its section:
    /// see <see cref="IExportedPart"/>. In the file, each is a section beside the ones above.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, object> Parts { get; init; } = new Dictionary<string, object>(StringComparer.Ordinal);
}

/// <summary>
/// A part of the export that a component above Accounts keeps: the rows it stores for a user, as
/// the user may read them. Accounts depends on no other component and cannot read their rows, so
/// it declares this interface, and the component that keeps the rows implements and registers
/// it, once for each section. See "When a lower component must trigger a higher one" in
/// docs/architecture.md.
/// </summary>
/// <remarks>
/// A part holds no secret (FR-KEY-8), and nothing of another user: it asks for its rows with
/// <see cref="Ownership.OwnedBy"/>.
/// </remarks>
public interface IExportedPart
{
    /// <summary>The name of the section of the export, as the file spells it: <c>aiKeys</c>.</summary>
    string Section { get; }

    /// <summary>The rows of a user, each as the file is to hold it.</summary>
    /// <param name="context">The context that the export is read with.</param>
    /// <param name="userId">The user whose rows are asked for.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<object>> ReadAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken);
}

/// <summary>An account, as its user may read it.</summary>
/// <param name="Id">The id of the account, which the audit trail names it by.</param>
/// <param name="Email">The email address.</param>
/// <param name="RegisteredAt">When the account was created.</param>
/// <param name="EmailVerifiedAt">When the address was verified, or null while it is not.</param>
/// <param name="TwoFactor">Where the account stands with two-factor authentication.</param>
public sealed record ExportedAccount(
    Guid Id, string Email, DateTimeOffset RegisteredAt, DateTimeOffset? EmailVerifiedAt, ExportedTwoFactor TwoFactor);

/// <summary>Two-factor authentication of an account, without its key and its recovery codes.</summary>
/// <param name="On">Whether signing in takes a code.</param>
/// <param name="Since">When it was turned on.</param>
/// <param name="RecoveryCodesLeft">How many recovery codes have not been used.</param>
public sealed record ExportedTwoFactor(bool On, DateTimeOffset? Since, int RecoveryCodesLeft);

/// <summary>One version of a legal document that the user accepted.</summary>
public sealed record ExportedAcceptance(LegalDocument Document, string Version, DateTimeOffset AcceptedAt);

/// <summary>A browser the user is signed in on, without its token.</summary>
/// <param name="CreatedAt">When the user signed in on it.</param>
/// <param name="LastActiveAt">When it was last used, to within a minute.</param>
public sealed record ExportedSession(DateTimeOffset CreatedAt, DateTimeOffset LastActiveAt);

/// <summary>An action of the user in the audit trail.</summary>
/// <param name="Action">What was done.</param>
/// <param name="Target">What it was done to, as a kind and an id.</param>
/// <param name="OccurredAt">When it was done.</param>
/// <param name="Origin">The IP address the request came from, or null when it was not known.</param>
public sealed record ExportedAuditEvent(AuditAction Action, string Target, DateTimeOffset OccurredAt, string? Origin);
