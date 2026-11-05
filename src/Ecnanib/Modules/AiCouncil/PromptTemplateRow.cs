using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.AiCouncil;

/// <summary>
/// One version of a prompt template (SRS 6.1 PromptTemplate). A row is never changed: an edit adds the next version
/// (FR-PRM-03), so an AI call that recorded a row's ID can always show what it was sent. The highest version of a
/// key is the one in use. Status, author and publishing are #48's.
/// </summary>
internal sealed class PromptTemplateRow
{
    /// <summary>The longest name among the layers, roles, markets and timeframes, with room to spare.</summary>
    public const int EnumMaxLength = 32;

    public required Guid Id { get; init; }

    public required PromptLayer Layer { get; init; }

    public AiRole? Role { get; init; }

    public Market? Market { get; init; }

    public Timeframe? Timeframe { get; init; }

    public required int Version { get; init; }

    public required string Content { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public PromptTemplateKey Key => new(Layer, Role, Market, Timeframe);

    public PromptTemplate ToTemplate() => new(Id, Key, Version, Content);
}

internal sealed class PromptTemplateRowConfiguration : IEntityTypeConfiguration<PromptTemplateRow>
{
    public void Configure(EntityTypeBuilder<PromptTemplateRow> builder)
    {
        builder.ToTable("prompt_templates", table =>
        {
            table.HasCheckConstraint("ck_prompt_templates_version", "version >= 1");

            // PromptTemplateKey's rule: a layer's own dimension is required.
            table.HasCheckConstraint(
                "ck_prompt_templates_layer_dimension",
                "(layer NOT IN ('Role', 'OutputContract') OR role IS NOT NULL) "
                + "AND (layer <> 'Market' OR market IS NOT NULL) "
                + "AND (layer <> 'Timeframe' OR timeframe IS NOT NULL)");
        });

        builder.HasKey(row => row.Id);
        builder.Ignore(row => row.Key);
        builder.Property(row => row.Layer).HasConversion<string>().HasMaxLength(PromptTemplateRow.EnumMaxLength);
        builder.Property(row => row.Role).HasConversion<string>().HasMaxLength(PromptTemplateRow.EnumMaxLength);
        builder.Property(row => row.Market).HasConversion<string>().HasMaxLength(PromptTemplateRow.EnumMaxLength);
        builder.Property(row => row.Timeframe).HasConversion<string>().HasMaxLength(PromptTemplateRow.EnumMaxLength);
        builder.Property(row => row.Content).HasMaxLength(PromptLimits.TemplateMaxLength);

        // One row per version of a key. A missing dimension is part of the key, so nulls compare equal.
        builder.HasIndex(row => new { row.Layer, row.Role, row.Market, row.Timeframe, row.Version })
            .IsUnique()
            .AreNullsDistinct(false);
    }
}
