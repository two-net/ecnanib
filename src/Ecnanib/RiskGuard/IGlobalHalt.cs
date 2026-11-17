namespace Ecnanib.RiskGuard;

/// <summary>
/// Whether the global halt is on: while it is, no agent opens a new position, and open positions
/// are still managed and protected (FR-OPS-3). Whatever keeps the halt implements this and
/// registers it, and what gathers the facts of a check asks it. See "The global halt" in
/// docs/risk-guard.md.
/// </summary>
/// <remarks>
/// <para>
/// Nothing implements it yet: the operator functions are not built, and nothing stores a halt.
/// Until then no halt is on. A test fails as soon as the operator functions are built and
/// nothing answers here.
/// </para>
/// <para>
/// The halt is a runtime flag of the database, and no setting of the configuration file
/// (NFR-CFG-6). Where more than one implementation is registered, the halt is on when any of
/// them says so.
/// </para>
/// </remarks>
public interface IGlobalHalt
{
    /// <summary>Whether the halt is on now.</summary>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<bool> IsOnAsync(CancellationToken cancellationToken);
}
