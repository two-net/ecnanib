namespace Ecnanib.Modules.RiskEngine.Contracts;

/// <summary>
/// The deterministic Risk Engine (SRS 3.9). It validates and sizes every order intent, and nothing reaches the
/// exchange without a passing evaluation (FR-RISK-01, AC-01). It reads nothing but its arguments and the caps in
/// configuration, so the same inputs always give the same evaluation. It has no clock, no I/O and no state, and
/// a test checks that it stays that way. See Risk Engine in docs/architecture.md.
/// </summary>
public interface IRiskEngine
{
    /// <summary>The caps it applies, from configuration (FR-RISK-11).</summary>
    RiskCaps Caps { get; }

    /// <summary>
    /// Runs every rule that applies to the intent, and records each one's inputs, outcome and reason
    /// (FR-RISK-10). A passing evaluation carries the <see cref="SizedOrder"/> to send. A failing entry because a
    /// loss limit was reached also carries the <see cref="LossLimitBreach"/> the caller must act on (FR-RISK-08).
    /// </summary>
    RiskEvaluation Evaluate(OrderIntent intent, RiskInputs inputs);

    /// <summary>
    /// The loss limits alone (FR-RISK-08). The decision cycle calls it at the start of every cycle, whatever the
    /// council decides, so a limit reached by a stop that filled between cycles pauses the agent at once.
    /// </summary>
    LossLimitCheck CheckLossLimits(RiskSettings settings, decimal capitalAllocation, TradingRecord record, DateTimeOffset now);
}
