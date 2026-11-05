using Ecnanib.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Catalog;

/// <summary>
/// A role's default provider, and optionally model (FR-ROLE-03). Seeded from the RoleDefaults section once per
/// role (NFR-DEP-02), and then the table is what counts, so administrators' edits (#26) survive a restart.
/// </summary>
internal sealed class RoleDefaultRow
{
    /// <summary>The longest <see cref="AiRole"/> name, with room to spare, as Agents stores it.</summary>
    public const int RoleMaxLength = 32;

    public required AiRole Role { get; init; }

    /// <summary>A row of <c>providers</c>.</summary>
    public required string Provider { get; set; }

    /// <summary>A model ID, compared exactly with <c>models.model_id</c>. It may name a model nobody's key has listed yet.</summary>
    public string? ModelId { get; set; }
}

internal sealed class RoleDefaultRowConfiguration : IEntityTypeConfiguration<RoleDefaultRow>
{
    public void Configure(EntityTypeBuilder<RoleDefaultRow> builder)
    {
        builder.ToTable("role_defaults");
        builder.HasKey(row => row.Role);
        builder.Property(row => row.Role).HasConversion<string>().HasMaxLength(RoleDefaultRow.RoleMaxLength);

        // Providers are seeded first and never removed. The foreign key also orders the inserts on a first
        // start, when both are added in one save.
        builder.HasOne<Provider>().WithMany().HasForeignKey(row => row.Provider).OnDelete(DeleteBehavior.Restrict);
    }
}
