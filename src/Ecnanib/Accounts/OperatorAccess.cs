using System.Security.Claims;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;

namespace Ecnanib.Accounts;

/// <summary>
/// The check that an operator function carries (NFR-SEC-3). An operator is a user whose address
/// is listed in the configuration file (FR-OPS-1) and is verified. See docs/ownership.md.
/// </summary>
/// <remarks>
/// It is a check of its own, apart from ownership. Being an operator opens no row of another
/// user: <see cref="Ownership.OwnedBy{TEntity}"/> does not know operators. And owning
/// something makes nobody an operator. An operator is not an account type either: nothing in the
/// database says who is one.
/// </remarks>
public static class OperatorAccess
{
    /// <summary>
    /// The name of the authorization policy. A page of an operator function carries
    /// <c>[Authorize(Policy = OperatorAccess.Policy)]</c>, and an endpoint requires it.
    /// </summary>
    public const string Policy = "Operator";

    /// <summary>The claim of a request whose user is an operator. Only the session handler adds it.</summary>
    internal const string ClaimType = "ecnanib:operator";

    /// <summary>
    /// Whether a user is an operator, for an operator function that is not guarded by the policy
    /// of its endpoint. It is false for a user who is not signed in.
    /// </summary>
    public static bool IsOperator(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.Identity is { IsAuthenticated: true } && user.HasClaim(claim => claim.Type == ClaimType);
    }
}

/// <summary>The addresses of the operators, as the configuration file lists them (FR-OPS-1).</summary>
internal sealed class Operators(ApplicationSettings application)
{
    // As an account stores its address, so that a listed address is found however the file writes it.
    private readonly HashSet<string> _addresses =
        [.. application.Operators.Select(EmailAddress.Normalize)];

    /// <summary>
    /// Whether an account is an operator's.
    /// </summary>
    /// <param name="email">The address of the account, as it is stored.</param>
    /// <param name="verified">
    /// Whether the address is verified. One that is not may have been registered by someone it
    /// does not belong to, who can sign in with it all the same.
    /// </param>
    public bool Includes(string email, bool verified) => verified && _addresses.Contains(email);
}
