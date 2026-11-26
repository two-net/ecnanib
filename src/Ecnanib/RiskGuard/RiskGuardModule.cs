namespace Ecnanib.RiskGuard;

/// <summary>
/// Risk Guard: non-AI limit checks and position sizing (SRS 3.1). A check asks every rule, and
/// computes the size of the order that its plan opens: see <see cref="GuardCheck"/>,
/// <see cref="OrderSizing"/>, and docs/risk-guard.md.
/// </summary>
public static class RiskGuardModule
{
    /// <summary>
    /// Registers the component, which has no service: the Risk Guard is rules only, and
    /// <see cref="GuardCheck"/> is asked with the facts of a plan. What keeps the global halt
    /// registers an <see cref="IGlobalHalt"/>: Agents does, for the halt that an operator
    /// switches (FR-OPS-3).
    /// </summary>
    public static IServiceCollection AddRiskGuard(this IServiceCollection services) => services;
}
