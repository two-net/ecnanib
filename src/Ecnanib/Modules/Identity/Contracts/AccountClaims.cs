using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Ecnanib.Modules.Identity.Contracts;

/// <summary>
/// What a session cookie carries. Administrator rights aren't here on purpose: a cookie outlives a
/// change to the configuration, so they're worked out on each check. See <see cref="IAdministrators"/>.
/// </summary>
public static class AccountClaims
{
    /// <summary>The value of <see cref="Contracts.LegalDocuments.Current"/> when the session started.</summary>
    public const string LegalDocuments = "ecnanib:legal-documents";

    /// <summary>The signed-in user, or null for a visitor.</summary>
    public static Guid? UserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), CultureInfo.InvariantCulture, out var id) ? id : null;
}

public static class AccountPolicies
{
    /// <summary>Signed in, with or without a document to accept. For what such a user still needs, such as signing out.</summary>
    public const string SignedIn = nameof(SignedIn);

    /// <summary>Use on administrator pages, and behind contracts too, because a circuit has no middleware.</summary>
    public const string Administrator = nameof(Administrator);
}

/// <summary>
/// Part of the default policy, so plain [Authorize] carries it (NFR-LEG-01). It's in Contracts because
/// the Web UI has to tell this failure apart, to send the user to accept the documents.
/// </summary>
public sealed class LegalAcceptedRequirement : IAuthorizationRequirement;
