namespace Ecnanib.RiskGuard;

/// <summary>
/// Risk Guard: non-AI limit checks and position sizing (SRS 3.1). The checks are built: see
/// <see cref="GuardCheck"/> and docs/risk-guard.md. The sizing is not.
/// </summary>
public static class RiskGuardModule
{
    /// <summary>
    /// Registers the component, which has no service: the Risk Guard is rules only, and
    /// <see cref="GuardCheck"/> is asked with the facts of a plan. What keeps the global halt
    /// registers an <see cref="IGlobalHalt"/>, when it is built: until then no halt is on.
    /// </summary>
    public static IServiceCollection AddRiskGuard(this IServiceCollection services) => services;
}
