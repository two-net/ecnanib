namespace Ecnanib.Platform.Persistence;

/// <summary>
/// A row that belongs to one user, and carries the id of that user (FR-ACC-5, NFR-DAT-9). Every
/// table holds rows of this kind or rows of the service itself, and never both. See
/// docs/ownership.md.
/// </summary>
/// <remarks>
/// It lives in the platform so that a component which may not use Accounts can still say whose
/// its rows are: the owner is an id, and tells nothing about the person.
/// </remarks>
public interface IUserOwned
{
    /// <summary>The user the row belongs to: <c>users.id</c>.</summary>
    Guid UserId { get; }
}

/// <summary>How the rows of one user are asked for. See docs/ownership.md.</summary>
public static class Ownership
{
    /// <summary>
    /// The rows that belong to one user, and no others. A query for the data of a user starts
    /// here, with the user who is signed in on the request, so a row of anyone else is not found:
    /// to whoever asks, it is the same as a row that does not exist (NFR-SEC-3).
    /// </summary>
    /// <param name="rows">The rows of a table whose rows belong to users.</param>
    /// <param name="userId">The user whose rows are asked for.</param>
    public static IQueryable<TEntity> OwnedBy<TEntity>(this IQueryable<TEntity> rows, Guid userId)
        where TEntity : class, IUserOwned =>
        rows.Where(row => row.UserId == userId);
}
