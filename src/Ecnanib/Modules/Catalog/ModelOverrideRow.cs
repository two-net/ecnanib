using Ecnanib.Kernel;
using Ecnanib.Modules.Catalog.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Catalog;

/// <summary>
/// Administrators' overrides for one model (FR-CAT-05): the third layer, above the configuration's and what
/// providers report. A sync writes only <c>models</c>, so these persist across syncs. A null field inherits.
/// A model with no overrides has no row.
/// </summary>
internal sealed class ModelOverrideRow
{
    public required string Provider { get; init; }

    public required string ModelId { get; init; }

    public string? DisplayName { get; set; }

    /// <summary>Withdrawn from offer. The model's status doesn't change, so it still ages (FR-CAT-04).</summary>
    public bool Hidden { get; set; }

    public bool? ImageInput { get; set; }

    public bool? StructuredOutput { get; set; }

    public bool? ToolUse { get; set; }

    public bool? LiveSearch { get; set; }

    public bool? ReasoningEffort { get; set; }

    /// <summary>US dollars per million tokens (DATA-R-02). Set with <see cref="OutputPrice"/> or not at all.</summary>
    public decimal? InputPrice { get; set; }

    public decimal? OutputPrice { get; set; }

    /// <summary>
    /// <see cref="AiRole"/> names, in role order. Names rather than an enum conversion, so a name this build
    /// doesn't know is skipped rather than failing the read.
    /// </summary>
    public string[] RecommendedRoles { get; set; } = [];

    public ModelEdit ToEdit() => new(
        DisplayName,
        Hidden,
        new ModelCapabilities(ImageInput, StructuredOutput, ToolUse, LiveSearch, ReasoningEffort),
        InputPrice is { } input && OutputPrice is { } output ? new ModelPrice(input, output) : null,
        Roles(RecommendedRoles));

    public void Set(ModelEdit edit)
    {
        DisplayName = edit.DisplayName;
        Hidden = edit.Hidden;
        ImageInput = edit.Capabilities.ImageInput;
        StructuredOutput = edit.Capabilities.StructuredOutput;
        ToolUse = edit.Capabilities.ToolUse;
        LiveSearch = edit.Capabilities.LiveSearch;
        ReasoningEffort = edit.Capabilities.ReasoningEffort;
        InputPrice = edit.Price?.InputPerMillion;
        OutputPrice = edit.Price?.OutputPerMillion;
        RecommendedRoles = Names(edit.RecommendedRoles);
    }

    public static string[] Names(IEnumerable<AiRole> roles) => [.. roles.Distinct().Order().Select(role => role.ToString())];

    /// <summary>The roles a row names, in role order, skipping any this build doesn't know.</summary>
    public static AiRole[] Roles(IReadOnlyCollection<string> names) =>
        [.. Enum.GetValues<AiRole>().Where(role => names.Contains(role.ToString(), StringComparer.Ordinal))];
}

internal sealed class ModelOverrideRowConfiguration : IEntityTypeConfiguration<ModelOverrideRow>
{
    public void Configure(EntityTypeBuilder<ModelOverrideRow> builder)
    {
        // A cost needs both prices, as in Catalog:Overrides.
        builder.ToTable("model_overrides", table => table.HasCheckConstraint(
            "ck_model_overrides_prices", "(input_price IS NULL) = (output_price IS NULL)"));

        builder.HasKey(row => new { row.Provider, row.ModelId });
        builder.Property(row => row.DisplayName).HasMaxLength(CatalogRules.TextMaxLength);
        builder.Property(row => row.RecommendedRoles).HasColumnType("text[]");

        // Models are never deleted, but if one were, its overrides would mean nothing.
        builder.HasOne<Model>()
            .WithOne()
            .HasForeignKey<ModelOverrideRow>(row => new { row.Provider, row.ModelId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
