using Ecnanib.Agents.Abstractions;
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
