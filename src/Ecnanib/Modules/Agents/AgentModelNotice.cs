using Ecnanib.Modules.Catalog.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Agents;

/// <summary>
/// That an agent's owner was told one of its models is Deprecated or Retired (FR-CAT-04), so the event and
/// the hourly check that recovers it never tell them twice. It's kept apart from the role rows, which an edit
/// replaces. Rows of a model that's Active again are deleted, so a later deprecation is told again.
/// </summary>
internal sealed class AgentModelNotice
{
    public required Guid AgentId { get; init; }

    public required string Provider { get; init; }

    public required string ModelId { get; init; }

    /// <summary><see cref="ModelStatus.Deprecated"/> or <see cref="ModelStatus.Retired"/>.</summary>
    public required ModelStatus Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

internal sealed class AgentModelNoticeConfiguration : IEntityTypeConfiguration<AgentModelNotice>
{
    public void Configure(EntityTypeBuilder<AgentModelNotice> builder)
    {
        builder.ToTable("agent_model_notices");
        builder.HasKey(notice => new { notice.AgentId, notice.Provider, notice.ModelId, notice.Status });
        builder.Property(notice => notice.Provider).HasMaxLength(Agent.ProviderMaxLength);
        builder.Property(notice => notice.ModelId).HasMaxLength(Agent.ModelMaxLength);
        builder.Property(notice => notice.Status).HasConversion<string>().HasMaxLength(Agent.EnumMaxLength);
        builder.HasOne<Agent>().WithMany().HasForeignKey(notice => notice.AgentId).OnDelete(DeleteBehavior.Cascade);

        // Clearing a model that's Active again finds its rows by model.
        builder.HasIndex(notice => new { notice.Provider, notice.ModelId });
    }
}
