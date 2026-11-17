using Ecnanib.Platform.Persistence;
using Ecnanib.RiskGuard;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// One check of the Risk Guard, as a row of the <c>risk_checks</c> table: which cycle it was
/// made in, what the plan that was checked does, when, the verdict, and the quantity and the
/// leverage that the check computed for the order (FR-RSK-5). The outcome of every rule hangs on
/// it: see <see cref="RiskCheckRule"/> (FR-RSK-9). See "The record of a check" in
/// docs/risk-guard.md.
/// </summary>
/// <remarks>
/// <para>
/// A cycle has no check when nothing of its plan goes on: a plan that holds, a plan that the
/// Risk Manager vetoed, and a cycle with no decision. It has two when the Risk Guard rejects a
/// plan that reverses: the reverse, and then its close alone, which goes on when it is accepted.
/// </para>
/// <para>
/// It carries its owner along to the cycle that it hangs on, as the call of a role does:
/// <c>(cycle_id, user_id)</c> points at <c>decision_cycles (id, user_id)</c>. The cycle says the
/// mode that the agent was in (FR-BIN-11). See docs/ownership.md.
/// </para>
/// </remarks>
/// <param name="id">Assigned by the application.</param>
/// <param name="cycleId">The cycle that the check was made in.</param>
/// <param name="userId">The user whose agent the cycle was of.</param>
/// <param name="sequence">Which check of its cycle: 1, or 2 for the close of a reverse that was rejected.</param>
/// <param name="action">What the plan that was checked does.</param>
/// <param name="checkedAt">When it was checked: when the facts had been read.</param>
/// <param name="verdict">Whether the plan goes on to the orders.</param>
/// <param name="quantity">
/// The quantity of the order that the plan opens, which the check computed. Zero when the facts
/// gave no order. Null for a plan that opens nothing, and when no size was computed.
/// </param>
/// <param name="leverage">The leverage of that order: 1 on Spot. Null where the quantity is.</param>
public sealed class RiskCheck(
    Guid id,
    Guid cycleId,
    Guid userId,
    int sequence,
    PlanAction action,
    DateTimeOffset checkedAt,
    GuardVerdict verdict,
    decimal? quantity,
    int? leverage) : IUserOwned
{
    public Guid Id { get; } = id;

    public Guid CycleId { get; } = cycleId;

    public Guid UserId { get; } = userId;

    public int Sequence { get; } = sequence;

    public PlanAction Action { get; } = action;

    public DateTimeOffset CheckedAt { get; } = checkedAt;

    public GuardVerdict Verdict { get; } = verdict;

    public decimal? Quantity { get; } = quantity;

    public int? Leverage { get; } = leverage;
}

/// <summary>
/// The outcome of one rule in one check of the Risk Guard, as a row of the
/// <c>risk_check_rules</c> table (FR-RSK-9). A check has a row for every rule, also for one that
/// its plan is not held to, so a user sees exactly why a plan was accepted or rejected.
/// </summary>
/// <remarks>
/// It holds the names of a rule and of an outcome, and two numbers: no text, and nothing that a
/// model wrote. It carries its owner along to its check: <c>(check_id, user_id)</c> points at
/// <c>risk_checks (id, user_id)</c>.
/// </remarks>
/// <param name="checkId">The check.</param>
/// <param name="userId">The user whose agent the check was for.</param>
/// <param name="rule">The rule.</param>
/// <param name="outcome">What the check made of it.</param>
/// <param name="measured">What the rule measured, or null where it measures nothing or could not.</param>
/// <param name="threshold">What the measure was held to, or null where there is none.</param>
public sealed class RiskCheckRule(
    Guid checkId,
    Guid userId,
    GuardRule rule,
    RuleOutcome outcome,
    decimal? measured,
    decimal? threshold) : IUserOwned
{
    public Guid CheckId { get; } = checkId;

    public Guid UserId { get; } = userId;

    public GuardRule Rule { get; } = rule;

    public RuleOutcome Outcome { get; } = outcome;

    public decimal? Measured { get; } = measured;

    public decimal? Threshold { get; } = threshold;
}

/// <summary>One check of the Risk Guard with the outcome of every rule, as it is stored with its cycle.</summary>
/// <param name="Check">The check.</param>
/// <param name="Rules">The outcome of every rule, in the order of <see cref="GuardRule"/>.</param>
public sealed record RiskCheckRecord(RiskCheck Check, IReadOnlyList<RiskCheckRule> Rules)
{
    /// <summary>The record of what the Risk Guard made of a plan in a cycle.</summary>
    /// <param name="cycleId">The cycle.</param>
    /// <param name="userId">The user whose agent the cycle is of.</param>
    /// <param name="sequence">Which check of the cycle: 1, or 2.</param>
    /// <param name="action">What the plan that was checked does.</param>
    /// <param name="checkedAt">When it was checked.</param>
    /// <param name="result">What the Risk Guard made of it.</param>
    public static RiskCheckRecord Of(
        Guid cycleId, Guid userId, int sequence, PlanAction action, DateTimeOffset checkedAt, GuardResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // The size that the check computed is stored with it, as numbers: also of a plan that
        // was rejected, so that its user sees what the rules were asked of (FR-RSK-5).
        var check = new RiskCheck(
            Guid.CreateVersion7(), cycleId, userId, sequence, action, checkedAt, result.Verdict, result.Size?.Quantity, result.Size?.Leverage);

        return new RiskCheckRecord(
            check,
            [.. result.Rules.Select(rule => new RiskCheckRule(check.Id, userId, rule.Rule, rule.Outcome, rule.Measured, rule.Threshold))]);
    }
}

internal sealed class RiskCheckConfiguration : IEntityTypeConfiguration<RiskCheck>
{
    public void Configure(EntityTypeBuilder<RiskCheck> builder)
    {
        builder.ToTable("risk_checks");
        builder.HasKey(check => check.Id);

        // The application assigns the id, so a row can be written without asking the database for one.
        builder.Property(check => check.Id).ValueGeneratedNever();
        builder.Property(check => check.CycleId);
        builder.Property(check => check.UserId);
        builder.Property(check => check.Sequence);
        builder.Property(check => check.Action);
        builder.Property(check => check.CheckedAt);
        builder.Property(check => check.Verdict);
        builder.Property(check => check.Quantity);
        builder.Property(check => check.Leverage);

        // The checks of a cycle, in their order. It is also the index of the key below.
        builder.HasIndex(check => new { check.CycleId, check.UserId, check.Sequence }).IsUnique();

        // The checks of a cycle go with it, and so with its agent and with the account. The key
        // carries the owner along, so the check of a cycle is its user's (NFR-DAT-9).
        builder.HasOne<DecisionCycle>()
            .WithMany()
            .HasForeignKey(check => new { check.CycleId, check.UserId })
            .HasPrincipalKey(cycle => new { cycle.Id, cycle.UserId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RiskCheckRuleConfiguration : IEntityTypeConfiguration<RiskCheckRule>
{
    public void Configure(EntityTypeBuilder<RiskCheckRule> builder)
    {
        builder.ToTable("risk_check_rules");

        // A check says one outcome for each rule. The owner stands in the key, so the key is
        // the index of the key below as well.
        builder.HasKey(rule => new { rule.CheckId, rule.UserId, rule.Rule });
        builder.Property(rule => rule.CheckId);
        builder.Property(rule => rule.UserId);
        builder.Property(rule => rule.Rule);
        builder.Property(rule => rule.Outcome);
        builder.Property(rule => rule.Measured);
        builder.Property(rule => rule.Threshold);

        // The outcomes of a check go with it, and so with its cycle, its agent, and the account.
        builder.HasOne<RiskCheck>()
            .WithMany()
            .HasForeignKey(rule => new { rule.CheckId, rule.UserId })
            .HasPrincipalKey(check => new { check.Id, check.UserId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
