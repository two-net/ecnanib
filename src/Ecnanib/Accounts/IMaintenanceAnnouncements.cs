using Ecnanib.Platform.Availability;

namespace Ecnanib.Accounts;

/// <summary>
/// Announcing maintenance, which an operator does: from when to when the service may not answer
/// (NFR-REL-6). A banner on every page says so until it is over, and what the service is down
/// for during it is left out of its availability when it was announced in advance (NFR-REL-1).
/// See "Announced maintenance" in docs/reliability.md.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is an operator function. Each member asks again whether the user of its
/// request is an operator, whatever calls it (NFR-SEC-3), and each is recorded in the audit
/// trail, in the transaction that does it (FR-OPS-5). See "Operators" in docs/ownership.md.
/// </para>
/// <para>
/// None of it acts on an agent: maintenance pauses nothing, takes no order off the exchange,
/// and closes no position (NFR-REL-6). What is announced is read through
/// <see cref="IAnnouncedMaintenance"/>, by the pages alone.
/// </para>
/// </remarks>
public interface IMaintenanceAnnouncements
{
    /// <summary>
    /// Announces maintenance. It starts after now, ends after it starts, and lasts no longer
    /// than <c>Availability:LongestMaintenanceHours</c>.
    /// </summary>
    /// <param name="request">The request of the operator.</param>
    /// <param name="startsAt">From when.</param>
    /// <param name="endsAt">Until when.</param>
    Task<MaintenanceAnnouncement> AnnounceAsync(HttpContext request, DateTimeOffset startsAt, DateTimeOffset endsAt);

    /// <summary>Cancels maintenance that has not started: no page tells of it any more.</summary>
    /// <param name="request">The request of the operator.</param>
    /// <param name="id">The id of what was announced.</param>
    /// <returns>
    /// Whether this request cancelled it. False when it has started, is over, or was cancelled
    /// already, and when the user of the request is no operator: nothing was done then, and
    /// nothing is recorded.
    /// </returns>
    Task<bool> CancelAsync(HttpContext request, Guid id);

    /// <summary>Ends maintenance that is under way: it is over from now, and not from the end that was announced.</summary>
    /// <param name="request">The request of the operator.</param>
    /// <param name="id">The id of what was announced.</param>
    /// <returns>
    /// Whether this request ended it. False when it has not started or is over already, and
    /// when the user of the request is no operator: nothing was done then, and nothing is
    /// recorded.
    /// </returns>
    Task<bool> EndAsync(HttpContext request, Guid id);
}

/// <summary>How announcing maintenance ended.</summary>
/// <param name="Announced">What was announced, which is recorded in the audit trail. Null when nothing was.</param>
/// <param name="Problem">Why nothing was announced, when it was for what was asked for.</param>
public sealed record MaintenanceAnnouncement(AnnouncedMaintenance? Announced, MaintenanceProblem? Problem)
{
    /// <summary>
    /// Nothing was announced, and not for what was asked for: the user of the request is no
    /// operator, is not signed in, or has no account any more.
    /// </summary>
    public static MaintenanceAnnouncement Refused { get; } = new(null, null);
}
