using System.ComponentModel.DataAnnotations;

namespace Ecnanib.Council.Configuration;

/// <summary>
/// The <c>Council</c> section of the system configuration file: how the evaluation deadline is budgeted between
/// the roles (FR-AI-13), how much market history a prompt carries, and the bounds of a role's text (Appendix C).
/// Appendix D lists no such section; FR-CFG-08 makes the appendix a minimum. Contains no credentials (FR-CFG-03).
/// </summary>
public sealed class CouncilOptions
{
    public const string SectionName = "Council";

    /// <summary>An outer cancellation this close to the deadline counts as the deadline; the role budgets end well before it, so a timed-out role is recorded rather than cancelled.</summary>
    public static TimeSpan DeadlineTolerance { get; } = TimeSpan.FromSeconds(2);

    /// <summary>Time the council leaves before the evaluation deadline for the decision's checks and placement (NFR-PERF-01); every role's budget ends at least this long before it.</summary>
    [Range(1, 120)]
    public int ExecutionReserveSeconds { get; set; } = 10;

    /// <summary>Upper bound of one role's turn, retries included; the evaluation deadline may leave it less.</summary>
    [Range(5, 600)]
    public int RoleTimeoutSeconds { get; set; } = 60;

    /// <summary>Time a non-final role must leave to the Risk Manager, which always runs (FR-AI-02, FR-AI-12).</summary>
    [Range(0, 600)]
    public int RiskManagerReserveSeconds { get; set; } = 45;

    /// <summary>A role with less than this left before the deadline is skipped rather than started.</summary>
    [Range(1, 60)]
    public int MinimumRoleBudgetSeconds { get; set; } = 5;

    /// <summary>Candles of the agent's timeframe summarised in every prompt (FR-AI-06).</summary>
    [Range(1, 500)]
    public int RecentCandles { get; set; } = 30;

    /// <summary>Candles of each higher timeframe summarised in every prompt; 0 leaves them out.</summary>
    [Range(0, 200)]
    public int HigherTimeframeCandles { get; set; } = 10;

    /// <summary>Output tokens requested per role call; may only lower <c>Providers:MaxOutputTokens</c> (NFR-COST-01). Reasoning tokens count against it on GPT, Grok and Gemini.</summary>
    [Range(1, 1_000_000)]
    public int MaxOutputTokens { get; set; } = 8192;

    /// <summary>Bound of a role's assessment text (Appendix C); longer answers are cut.</summary>
    [Range(100, 4000)]
    public int MaxAssessmentLength { get; set; } = 1200;

    /// <summary>Bound of the Risk Manager's rationale (Appendix C); the column holds 4000 including the limits prefix.</summary>
    [Range(50, 3000)]
    public int MaxRationaleLength { get; set; } = 600;

    public TimeSpan RoleTimeout => TimeSpan.FromSeconds(RoleTimeoutSeconds);

    public TimeSpan RiskManagerReserve => TimeSpan.FromSeconds(RiskManagerReserveSeconds);

    public TimeSpan MinimumRoleBudget => TimeSpan.FromSeconds(MinimumRoleBudgetSeconds);

    public TimeSpan ExecutionReserve => TimeSpan.FromSeconds(ExecutionReserveSeconds);
}
