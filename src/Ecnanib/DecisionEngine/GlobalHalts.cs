using Ecnanib.RiskGuard;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Whether a global halt is on, as the Decision Engine asks it: of whatever keeps one (FR-OPS-3).
/// While it is on, no agent opens a new position, and open positions are still managed and
/// protected. See "The global halt" in docs/risk-guard.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>The halt is on when any of those that keep one says so.</b> Agents keeps the one that an
/// operator switches, and a test may add a stand-in of its own.
/// </para>
/// <para>
/// <b>It is asked in two places, and of one thing:</b> by what has the Risk Guard check the plan
/// of a cycle, whose rule a plan that opens a position must pass, and by what settles the
/// orders, which takes an entry that rests off the exchange while the halt is on. So both go by
/// the same answer.
/// </para>
/// </remarks>
internal sealed class GlobalHalts(IEnumerable<IGlobalHalt> halts)
{
    private readonly IReadOnlyList<IGlobalHalt> _halts = [.. halts];

    /// <summary>Whether a halt is on now.</summary>
    /// <param name="cancellationToken">Cancels the question.</param>
    public async Task<bool> AnyOnAsync(CancellationToken cancellationToken)
    {
        foreach (var halt in _halts)
        {
            if (await halt.IsOnAsync(cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
