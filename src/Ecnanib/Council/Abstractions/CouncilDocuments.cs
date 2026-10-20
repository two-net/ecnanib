using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Council.Abstractions;

/// <summary>
/// A role's validated output (Appendix C "role output"): what every role returns after schema validation. Text is
/// bounded by the configured lengths; prices are positive; <see cref="EntryType"/> is null when no entry is proposed.
/// </summary>
public record RoleOutputDocument(
    string Assessment,
    decimal Confidence,
    DecisionAction ProposedAction,
    OrderType? EntryType,
    decimal? EntryPrice,
    decimal? StopPrice,
    IReadOnlyList<decimal> Targets,
    IReadOnlyList<string> Flags);

/// <summary>The Risk Manager's validated output (Appendix C "final decision", FR-AI-10) before the hard limits are applied.</summary>
public sealed record FinalDecisionDocument(
    string Assessment,
    decimal Confidence,
    DecisionAction ProposedAction,
    OrderType? EntryType,
    decimal? EntryPrice,
    decimal? StopPrice,
    IReadOnlyList<decimal> Targets,
    IReadOnlyList<string> Flags,
    decimal? RiskPercent,
    string Rationale,
    decimal? PartialFraction)
    : RoleOutputDocument(Assessment, Confidence, ProposedAction, EntryType, EntryPrice, StopPrice, Targets, Flags);

/// <summary>Outcome of one hard limit (FR-AI-11): the council's output passed it, was clamped to it, or violated it.</summary>
public enum LimitCheckStatus
{
    Pass = 1,
    Clamped = 2,
    Fail = 3,
}

/// <summary>One entry of a decision's limits check (Appendix C); stored as <c>decisions.limits_check</c>.</summary>
public sealed record LimitCheck(string Limit, LimitCheckStatus Status, string Detail);

/// <summary>The limits the council enforces after the Risk Manager (FR-EXE-04, FR-EXE-09, FR-AI-15); execution appends its own (notional, balance).</summary>
public static class LimitNames
{
    public const string EntriesAllowed = "entriesAllowed";
    public const string NoEntryWindow = "noEntryWindow";
    public const string SetupRequired = "setupRequired";
    public const string Direction = "direction";
    public const string DailyLoss = "dailyLoss";
    public const string OpenPositions = "openPositions";
    public const string StopRequired = "stopRequired";
    public const string StopSide = "stopSide";
    public const string StopDistance = "stopDistance";
    public const string RiskPerTrade = "riskPerTrade";
    public const string Leverage = "leverage";
    public const string Targets = "targets";
    public const string PositionRequired = "positionRequired";
    public const string StopTightenOnly = "stopTightenOnly";
    public const string PartialFraction = "partialFraction";
}
