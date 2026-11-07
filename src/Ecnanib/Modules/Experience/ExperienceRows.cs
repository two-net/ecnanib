using Ecnanib.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Experience;

/// <summary>
/// SRS 6.1's PerformanceSnapshot: one model's calls and decisions in one role, for one agent, on one UTC day, by the
/// day each cycle started. The dimensions are the cycle's own, never the agent's, so Demo and Live stay apart
/// (FR-EXP-08). Every counter adds up, so a period's metrics are the sum of its days. They outlive the traces they
/// were counted from, which retention purges (#50). See Trading experience in docs/architecture.md.
/// </summary>
internal sealed class PerformanceSnapshotRow
{
    /// <summary>The longest enum name stored, with room to spare.</summary>
    public const int EnumMaxLength = 32;

    public const int SymbolMaxLength = 20;

    public const int ProviderMaxLength = 32;

    public const int ModelMaxLength = 256;

    public long Id { get; init; }

    public required Guid UserId { get; init; }

    public required Guid AgentId { get; init; }

    public required TradingEnvironment Environment { get; init; }

    public required Market Market { get; init; }

    public required string Symbol { get; init; }

    public required Timeframe Timeframe { get; init; }

    public required AiRole Role { get; init; }

    public required string Provider { get; init; }

    public required string ModelId { get; init; }

    /// <summary>UTC midnight at the start of the day.</summary>
    public required DateTimeOffset PeriodStart { get; init; }

    public long Cycles { get; set; }

    public long AnsweredCycles { get; set; }

    public long FallbackCycles { get; set; }

    public long Sent { get; set; }

    public long NotSent { get; set; }

    public long Answered { get; set; }

    public long ContractViolations { get; set; }

    public long Timeouts { get; set; }

    public long Refusals { get; set; }

    public long Errors { get; set; }

    public long Truncated { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    /// <summary>The priced calls' estimated cost, in US dollars, as <c>ai_calls.cost</c> is.</summary>
    public decimal Cost { get; set; }

    public long UnpricedTokens { get; set; }

    /// <summary>How many calls sent took each band of <see cref="LatencyBands"/>.</summary>
    public required long[] LatencyBuckets { get; init; }

    public long Holds { get; set; }

    public long Entries { get; set; }

    public long Exits { get; set; }

    public long Adjustments { get; set; }

    public long Approvals { get; set; }

    public long Modifications { get; set; }

    public long Rejections { get; set; }

    public long BullishCalls { get; set; }

    public long BearishCalls { get; set; }

    public long Hits { get; set; }

    public long Misses { get; set; }

    public required DateTimeOffset ComputedAt { get; init; }
}

/// <summary>
/// Which model filled each role in a cycle whose Strategist proposed an entry (FR-EXP-01). A trade's entry order cites
/// its cycle, so this is what attributes a trade to its models, after retention has purged the cycle's trace (#50).
/// </summary>
internal sealed class CycleAttributionRow
{
    public required Guid CycleId { get; init; }

    public required AiRole Role { get; init; }

    public required Guid UserId { get; init; }

    public required Guid AgentId { get; init; }

    public required TradingEnvironment Environment { get; init; }

    public required Market Market { get; init; }

    public required string Symbol { get; init; }

    public required Timeframe Timeframe { get; init; }

    public required string Provider { get; init; }

    public required string ModelId { get; init; }
}

/// <summary>
/// How far a user's snapshots in one timeframe are built for good: every day before <see cref="BuiltUntil"/> was
/// built after it became final, so it's never built again.
/// </summary>
internal sealed class ExperienceProgressRow
{
    public required Guid UserId { get; init; }

    public required Timeframe Timeframe { get; init; }

    /// <summary>UTC midnight at the end of the last day built for good.</summary>
    public required DateTimeOffset BuiltUntil { get; set; }

    public required DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class PerformanceSnapshotRowConfiguration : IEntityTypeConfiguration<PerformanceSnapshotRow>
{
    public void Configure(EntityTypeBuilder<PerformanceSnapshotRow> builder)
    {
        builder.ToTable("performance_snapshots");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Environment).HasConversion<string>().HasMaxLength(PerformanceSnapshotRow.EnumMaxLength);
        builder.Property(row => row.Market).HasConversion<string>().HasMaxLength(PerformanceSnapshotRow.EnumMaxLength);
        builder.Property(row => row.Timeframe).HasConversion<string>().HasMaxLength(PerformanceSnapshotRow.EnumMaxLength);
        builder.Property(row => row.Role).HasConversion<string>().HasMaxLength(PerformanceSnapshotRow.EnumMaxLength);
        builder.Property(row => row.Symbol).HasMaxLength(PerformanceSnapshotRow.SymbolMaxLength);
        builder.Property(row => row.Provider).HasMaxLength(PerformanceSnapshotRow.ProviderMaxLength);
        builder.Property(row => row.ModelId).HasMaxLength(PerformanceSnapshotRow.ModelMaxLength);
        builder.HasIndex(row => new
            {
                row.UserId,
                row.AgentId,
                row.Environment,
                row.Market,
                row.Symbol,
                row.Timeframe,
                row.Role,
                row.Provider,
                row.ModelId,
                row.PeriodStart,
            })
            .IsUnique()
            .HasDatabaseName("ix_performance_snapshots_key");

        // What a read filters on: the user, one environment and a range of days.
        builder.HasIndex(row => new { row.UserId, row.Environment, row.PeriodStart });
    }
}

internal sealed class CycleAttributionRowConfiguration : IEntityTypeConfiguration<CycleAttributionRow>
{
    public void Configure(EntityTypeBuilder<CycleAttributionRow> builder)
    {
        builder.ToTable("cycle_attributions");
        builder.HasKey(row => new { row.CycleId, row.Role });
        builder.Property(row => row.Role).HasConversion<string>().HasMaxLength(PerformanceSnapshotRow.EnumMaxLength);
        builder.Property(row => row.Environment).HasConversion<string>().HasMaxLength(PerformanceSnapshotRow.EnumMaxLength);
        builder.Property(row => row.Market).HasConversion<string>().HasMaxLength(PerformanceSnapshotRow.EnumMaxLength);
        builder.Property(row => row.Timeframe).HasConversion<string>().HasMaxLength(PerformanceSnapshotRow.EnumMaxLength);
        builder.Property(row => row.Symbol).HasMaxLength(PerformanceSnapshotRow.SymbolMaxLength);
        builder.Property(row => row.Provider).HasMaxLength(PerformanceSnapshotRow.ProviderMaxLength);
        builder.Property(row => row.ModelId).HasMaxLength(PerformanceSnapshotRow.ModelMaxLength);

        // Deleting an agent's, and account deletion's.
        builder.HasIndex(row => new { row.UserId, row.AgentId });
    }
}

internal sealed class ExperienceProgressRowConfiguration : IEntityTypeConfiguration<ExperienceProgressRow>
{
    public void Configure(EntityTypeBuilder<ExperienceProgressRow> builder)
    {
        builder.ToTable("experience_progress");
        builder.HasKey(row => new { row.UserId, row.Timeframe });
        builder.Property(row => row.Timeframe).HasConversion<string>().HasMaxLength(PerformanceSnapshotRow.EnumMaxLength);
    }
}
