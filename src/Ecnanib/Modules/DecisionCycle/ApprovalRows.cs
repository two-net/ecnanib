using Ecnanib.Modules.DecisionCycle.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>
/// A Confirm agent's sized order, waiting for its user or decided (SRS 6.1's Approval, SRS 3.11). It hangs off the cycle
/// that proposed it, and goes with it, so deleting an agent or its history needs nothing more. Each JSON column has its
/// enums by name. See Approvals in docs/architecture.md.
/// </summary>
internal sealed class ApprovalRow
{
    /// <summary>At most one pending approval per agent: a newer one replaces it.</summary>
    public const string PendingIndex = "ix_approvals_agent_id_pending";

    /// <summary>
    /// At most one approval per agent being checked again and sent, so two approved entries can't both pass the Risk
    /// Engine before either is on record.
    /// </summary>
    public const string ApprovingIndex = "ix_approvals_agent_id_approving";

    public required Guid Id { get; init; }

    public required Guid CycleId { get; init; }

    public required Guid UserId { get; init; }

    public required Guid AgentId { get; init; }

    public required ApprovalStatus Status { get; set; }

    public ApprovalEnd? End { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The user who decided it, or null when the system did or nobody has.</summary>
    public Guid? DecidedBy { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>The <see cref="RiskEngine.Contracts.OrderIntent"/> the cycle's evaluation passed, which approving evaluates again.</summary>
    public required string Intent { get; init; }

    /// <summary>The Strategist's, for a limit entry (FR-EXE-01).</summary>
    public int? LimitTtlCandles { get; init; }

    /// <summary>The <see cref="ApprovalProposal"/>, for the page (FR-APR-01). It holds what models wrote.</summary>
    public required string Proposal { get; init; }

    /// <summary>
    /// The Risk Engine's evaluation when it was approved, as the trace keeps a cycle's: the intent, the caps, the
    /// effective settings, every rule, the sized order and any loss limit reached (FR-APR-03, FR-RISK-10).
    /// </summary>
    public string? Revalidation { get; set; }

    public DateTimeOffset? RevalidatedAt { get; set; }

    /// <summary>What execution did, as an <see cref="Execution.Contracts.ExecutionResult"/>.</summary>
    public string? Execution { get; set; }
}

internal sealed class ApprovalRowConfiguration : IEntityTypeConfiguration<ApprovalRow>
{
    public void Configure(EntityTypeBuilder<ApprovalRow> builder)
    {
        builder.ToTable("approvals");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Status).HasConversion<string>().HasMaxLength(DecisionCycleRow.EnumMaxLength);
        builder.Property(row => row.End).HasConversion<string>().HasMaxLength(DecisionCycleRow.EnumMaxLength);
        builder.Property(row => row.Intent).HasColumnType("jsonb");
        builder.Property(row => row.Proposal).HasColumnType("jsonb");
        builder.Property(row => row.Revalidation).HasColumnType("jsonb");
        builder.Property(row => row.Execution).HasColumnType("jsonb");

        // One approval per cycle, and it goes with the cycle: an agent's deletion, account deletion and retention.
        builder.HasOne<DecisionCycleRow>()
            .WithOne()
            .HasForeignKey<ApprovalRow>(row => row.CycleId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_approvals_decision_cycles_cycle_id");
        builder.HasIndex(row => row.CycleId).IsUnique();

        // The approvals page lists a user's newest first.
        builder.HasIndex(row => new { row.UserId, row.CreatedAt });

        // Two indexes on one column need a name each, or EF Core makes them one, and the database's name is set apart.
        builder.HasIndex(row => row.AgentId, ApprovalRow.PendingIndex)
            .IsUnique()
            .HasFilter("status = 'Pending'")
            .HasDatabaseName(ApprovalRow.PendingIndex);

        builder.HasIndex(row => row.AgentId, ApprovalRow.ApprovingIndex)
            .IsUnique()
            .HasFilter("status = 'Approving'")
            .HasDatabaseName(ApprovalRow.ApprovingIndex);

        // The expiry job finds pending approvals by when they expire.
        builder.HasIndex(row => row.ExpiresAt).HasFilter("status = 'Pending'").HasDatabaseName("ix_approvals_pending_expires_at");
    }
}
