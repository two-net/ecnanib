using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>
/// A decision cycle (SRS 6.1's DecisionCycle): what it ran on, how it ended, and its totals. Its roles, AI calls and
/// Risk Engine evaluation are rows of their own, which go with it. There's no foreign key to <c>agents</c> or
/// <c>users</c>, because other modules own them: deleting an agent reaches this table through
/// <see cref="Agents.Contracts.IAgentHistory"/>. See Decision cycle in docs/architecture.md.
/// </summary>
internal sealed class DecisionCycleRow
{
    /// <summary>The longest enum name stored in the trace, with room to spare.</summary>
    public const int EnumMaxLength = 32;

    public const int SymbolMaxLength = 20;

    public const int ProviderMaxLength = 32;

    public const int ModelMaxLength = 256;

    /// <summary>One cycle per agent and candle, whatever restarts or catch-ups happen.</summary>
    public const string CandleIndex = "ix_decision_cycles_agent_id_candle_close";

    public required Guid Id { get; init; }

    public required Guid UserId { get; init; }

    public required Guid AgentId { get; init; }

    public required Market Market { get; init; }

    public required TradingEnvironment Environment { get; init; }

    public required string Symbol { get; init; }

    public required Timeframe Timeframe { get; init; }

    /// <summary>When the candle the cycle decided on closed: SRS 6.1's "scheduled at", without the delay.</summary>
    public required DateTimeOffset CandleClose { get; init; }

    public required bool CatchUp { get; init; }

    /// <summary>After this, the cycle executes no new entry (FR-CYC-06).</summary>
    public required DateTimeOffset Deadline { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Null while it runs.</summary>
    public CycleOutcome? Outcome { get; set; }

    public CycleFailure? Failure { get; set; }

    /// <summary>A summary of what the cycle saw, as JSON (FR-UI-04). The candles each role saw are in its prompt.</summary>
    public string? InputSnapshot { get; set; }

    /// <summary>The loss limits' rule results at the start of the cycle, as JSON (FR-RISK-08, FR-RISK-10).</summary>
    public string? LossLimits { get; set; }

    /// <summary>
    /// The agent's AI budget against its spend at the start of the cycle, as JSON, when it has a limit (FR-CYC-07).
    /// </summary>
    public string? Budget { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    /// <summary>In US dollars, every priced call's, or null when none had a price.</summary>
    public decimal? Cost { get; set; }

    /// <summary>The cycle's run's, which links it to its logs and its <c>job_runs</c> row (NFR-OBS-02).</summary>
    public string? CorrelationId { get; init; }

    public List<CycleRoleRow> Roles { get; init; } = [];
}

/// <summary>A role in a cycle, for the council view (FR-ROLE-08).</summary>
internal sealed class CycleRoleRow
{
    public required Guid CycleId { get; init; }

    public required AiRole Role { get; init; }

    public required RoleState State { get; set; }

    public string? Provider { get; set; }

    public string? ModelId { get; set; }

    public bool UsedFallback { get; set; }

    /// <summary>What a model concluded, so it's only ever shown as text.</summary>
    public string? Summary { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>
/// One call to a model, or one that couldn't be sent (SRS 6.1's AiCall, FR-UI-04, FR-PRM-03, FR-EXP-01). It holds
/// the user's prompt and a model's answer, so only its user sees it, and administrators never do (FR-ADM-01).
/// </summary>
internal sealed class AiCallRow
{
    public long Id { get; init; }

    public required Guid CycleId { get; init; }

    public required AiRole Role { get; init; }

    public required string Provider { get; init; }

    public required string ModelId { get; init; }

    public required bool Fallback { get; init; }

    /// <summary>1, or 2 for the retry an answer that broke its contract gets (FR-CYC-05).</summary>
    public required int Attempt { get; init; }

    public required CouncilCallStatus Status { get; init; }

    public CouncilFailure? Failure { get; init; }

    /// <summary>The provider's own message, which can quote the prompt, so it's here and never in the log.</summary>
    public string? ProviderMessage { get; init; }

    public string? Instructions { get; init; }

    public string? Message { get; init; }

    public string? Answer { get; init; }

    /// <summary>The checked output, as JSON in its contract's names, when the answer conforms.</summary>
    public string? Output { get; init; }

    /// <summary>What broke the contract, as JSON: each a path and a problem.</summary>
    public string? Violations { get; init; }

    /// <summary>The prompt template versions used, as JSON (FR-PRM-03).</summary>
    public required string Templates { get; init; }

    public required long InputTokens { get; init; }

    public required long OutputTokens { get; init; }

    public decimal? Cost { get; init; }

    public required TimeSpan Latency { get; init; }

    public required int ProviderAttempts { get; init; }

    public required bool Truncated { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
}

/// <summary>
/// The Risk Engine's evaluation of a cycle's order intent (SRS 6.1's RiskEvaluation, FR-RISK-10): every rule's
/// inputs, outcome and reason, and what passed. Each part is JSON with its enums by name.
/// </summary>
internal sealed class RiskEvaluationRow
{
    public long Id { get; init; }

    public required Guid CycleId { get; init; }

    public required RiskVerdict Verdict { get; init; }

    public required string Intent { get; init; }

    public required string Caps { get; init; }

    public required string Effective { get; init; }

    public required string Rules { get; init; }

    /// <summary>What to send, when it passed, for execution (#34) or approval (#38).</summary>
    public string? SizedOrder { get; init; }

    public string? Breach { get; init; }

    public required DateTimeOffset EvaluatedAt { get; init; }
}

internal sealed class DecisionCycleRowConfiguration : IEntityTypeConfiguration<DecisionCycleRow>
{
    public void Configure(EntityTypeBuilder<DecisionCycleRow> builder)
    {
        builder.ToTable("decision_cycles");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Market).HasConversion<string>().HasMaxLength(DecisionCycleRow.EnumMaxLength);
        builder.Property(row => row.Environment).HasConversion<string>().HasMaxLength(DecisionCycleRow.EnumMaxLength);
        builder.Property(row => row.Timeframe).HasConversion<string>().HasMaxLength(DecisionCycleRow.EnumMaxLength);
        builder.Property(row => row.Outcome).HasConversion<string>().HasMaxLength(DecisionCycleRow.EnumMaxLength);
        builder.Property(row => row.Failure).HasConversion<string>().HasMaxLength(DecisionCycleRow.EnumMaxLength);
        builder.Property(row => row.Symbol).HasMaxLength(DecisionCycleRow.SymbolMaxLength);
        builder.Property(row => row.InputSnapshot).HasColumnType("jsonb");
        builder.Property(row => row.LossLimits).HasColumnType("jsonb");
        builder.Property(row => row.Budget).HasColumnType("jsonb");
        builder.HasIndex(row => new { row.AgentId, row.CandleClose }).IsUnique().HasDatabaseName(DecisionCycleRow.CandleIndex);
        builder.HasIndex(row => new { row.UserId, row.AgentId, row.StartedAt });

        // Retention (#50) purges by start.
        builder.HasIndex(row => row.StartedAt);

        builder.HasMany(row => row.Roles).WithOne().HasForeignKey(role => role.CycleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany<AiCallRow>().WithOne().HasForeignKey(call => call.CycleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany<RiskEvaluationRow>().WithOne().HasForeignKey(evaluation => evaluation.CycleId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CycleRoleRowConfiguration : IEntityTypeConfiguration<CycleRoleRow>
{
    public void Configure(EntityTypeBuilder<CycleRoleRow> builder)
    {
        builder.ToTable("cycle_roles");
        builder.HasKey(row => new { row.CycleId, row.Role });
        builder.Property(row => row.Role).HasConversion<string>().HasMaxLength(DecisionCycleRow.EnumMaxLength);
        builder.Property(row => row.State).HasConversion<string>().HasMaxLength(DecisionCycleRow.EnumMaxLength);
        builder.Property(row => row.Provider).HasMaxLength(DecisionCycleRow.ProviderMaxLength);
        builder.Property(row => row.ModelId).HasMaxLength(DecisionCycleRow.ModelMaxLength);
    }
}

internal sealed class AiCallRowConfiguration : IEntityTypeConfiguration<AiCallRow>
{
    public void Configure(EntityTypeBuilder<AiCallRow> builder)
    {
        builder.ToTable("ai_calls");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Role).HasConversion<string>().HasMaxLength(DecisionCycleRow.EnumMaxLength);
        builder.Property(row => row.Status).HasConversion<string>().HasMaxLength(DecisionCycleRow.EnumMaxLength);
        builder.Property(row => row.Failure).HasConversion<string>().HasMaxLength(DecisionCycleRow.EnumMaxLength);
        builder.Property(row => row.Provider).HasMaxLength(DecisionCycleRow.ProviderMaxLength);
        builder.Property(row => row.ModelId).HasMaxLength(DecisionCycleRow.ModelMaxLength);
        builder.Property(row => row.Output).HasColumnType("jsonb");
        builder.Property(row => row.Violations).HasColumnType("jsonb");
        builder.Property(row => row.Templates).HasColumnType("jsonb");
        builder.HasIndex(row => row.CycleId);
    }
}

internal sealed class RiskEvaluationRowConfiguration : IEntityTypeConfiguration<RiskEvaluationRow>
{
    public void Configure(EntityTypeBuilder<RiskEvaluationRow> builder)
    {
        builder.ToTable("risk_evaluations");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Verdict).HasConversion<string>().HasMaxLength(DecisionCycleRow.EnumMaxLength);
        builder.Property(row => row.Intent).HasColumnType("jsonb");
        builder.Property(row => row.Caps).HasColumnType("jsonb");
        builder.Property(row => row.Effective).HasColumnType("jsonb");
        builder.Property(row => row.Rules).HasColumnType("jsonb");
        builder.Property(row => row.SizedOrder).HasColumnType("jsonb");
        builder.Property(row => row.Breach).HasColumnType("jsonb");

        // One evaluation per cycle: a cycle evaluates at most one intent.
        builder.HasIndex(row => row.CycleId).IsUnique();
    }
}
