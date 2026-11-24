using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Accounts;

/// <summary>
/// The theme of the signed-in user, which is the user's own choice: dark, light, or as their
/// device is set (FR-THM-1). It is saved to the account, so it holds on every device that the
/// user signs in on (FR-THM-3). See docs/themes.md.
/// </summary>
/// <remarks>
/// <para>
/// The user is the one of the session, and the choice of anyone else is not changed (NFR-SEC-3).
/// The choice exposes no key and moves no money, so it asks for no proof of identity, and is in
/// no event of the audit trail.
/// </para>
/// <para>
/// There is nothing to read here: a request is told the theme of its user as it is signed in,
/// with <see cref="SignedInUser.ThemeOf"/>.
/// </para>
/// </remarks>
public interface IThemeChoice
{
    /// <summary>
    /// Saves the theme that the signed-in user chose. It holds for every page that is asked for
    /// from then on, on every device. A choice that is made twice changes nothing the second
    /// time. It fails for a request that is not signed in, and for a theme that there is not.
    /// </summary>
    /// <param name="request">The request, which says who is signed in.</param>
    /// <param name="theme">The theme that the user chose.</param>
    Task SetAsync(HttpContext request, Theme theme);
}

/// <summary>Stores the theme of a user in the row of their account.</summary>
internal sealed class StoredThemeChoice(IDbContextFactory<EcnanibDbContext> contexts, ILogger<StoredThemeChoice> logger)
    : IThemeChoice
{
    public async Task SetAsync(HttpContext request, Theme theme)
    {
        // A number that names no theme would be stored as that number, and read by nothing.
        if (!Enum.IsDefined(theme))
        {
            throw new ArgumentOutOfRangeException(nameof(theme), theme, "There is no such theme.");
        }

        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The account of the session, and no other: no form names a user. Only where it says
        // something else, so that a choice that is made again writes nothing.
        var changed = await context.Set<User>()
            .Where(user => user.Id == userId && user.Theme != theme)
            .ExecuteUpdateAsync(row => row.SetProperty(user => user.Theme, theme), cancellationToken);

        if (changed > 0)
        {
            logger.LogInformation("User {UserId} chose the theme {Theme}", userId, theme);
        }
    }
}
