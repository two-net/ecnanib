namespace Ecnanib.Agents;

/// <summary>
/// The global halt, as an operator reads and switches it (FR-OPS-3). While it is on, no agent
/// opens a new position, and open positions are still managed and protected. It is kept in the
/// database, as a runtime flag, and is no setting of the configuration file (NFR-CFG-6). See
/// "The global halt" in docs/risk-guard.md, and "Operators" in docs/ownership.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is an operator function.</b> The page that switches the halt requires the policy of an
/// operator, and <see cref="SwitchAsync"/> asks again whether the user of the request is one
/// (NFR-SEC-3). Every switch is recorded in the audit trail, in the transaction that makes it
/// (FR-OPS-5).
/// </para>
/// <para>
/// What checks the plan of a cycle asks whether the halt is on through <c>IGlobalHalt</c> of
/// the Risk Guard, which the same component answers.
/// </para>
/// </remarks>
public interface IGlobalHaltSwitch
{
    /// <summary>Whether the halt is on now, and since when. It only reads, and says nothing of any user.</summary>
    /// <param name="cancellationToken">Ends the read.</param>
    Task<GlobalHaltState> StateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Switches the halt on or off, for an operator, and records it in the audit trail. A halt
    /// that is already as it is asked for stays as it is, and nothing is recorded: a form that
    /// is sent twice switches once.
    /// </summary>
    /// <param name="request">The request of the signed-in user, who must be an operator.</param>
    /// <param name="on">Whether the halt is to be on.</param>
    /// <returns>
    /// Whether this request switched the halt. False when it was as asked for already, and when
    /// the user of the request is no operator, for whom nothing is changed.
    /// </returns>
    Task<bool> SwitchAsync(HttpContext request, bool on);
}

/// <summary>The global halt as it is now.</summary>
/// <param name="On">Whether it is on.</param>
/// <param name="Since">When it was switched on. Null while it is off.</param>
public sealed record GlobalHaltState(bool On, DateTimeOffset? Since);
