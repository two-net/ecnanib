using Ecnanib.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Catalog;

/// <summary>
/// A role's default provider, and optionally model (FR-ROLE-03). Seeded from the RoleDefaults section once per
/// role (NFR-DEP-02), and then the table is what counts, so administrators' edits survive a restart. A row with
/// no provider is an administrator's "no default", which keeps the seeder from filling the role again.
/// </summary>
internal sealed class RoleDefaultRow
{
    /// <summary>The longest <see cref="AiRole"/> name, with room to spare, as Agents stores it.</summary>
    public const int RoleMaxLength = 32;

    public required AiRole Role { get; init; }

    /// <summary>A row of <c>providers</c>, or null when an administrator left the role with no default.</summary>
    public string? Provider { get; set; }

    /// <summary>
    /// A model ID, compared exactly with <c>models.model_id</c>. It may name a model nobody's key has listed yet.
    /// Null when there's no provider.
    /// </summary>
    public string? ModelId { get; set; }
}

internal sealed class RoleDefaultRowConfiguration : IEntityTypeConfiguration<RoleDefaultRow>
{
    public void Configure(EntityTypeBuilder<RoleDefaultRow> builder)
    {
        builder.ToTable("role_defaults", table => table.HasCheckConstraint(
            "ck_role_defaults_model_needs_provider", "provider IS NOT NULL OR model_id IS NULL"));
        builder.HasKey(row => row.Role);
        builder.Property(row => row.Role).HasConversion<string>().HasMaxLength(RoleDefaultRow.RoleMaxLength);

        // Providers are seeded first and never removed. The foreign key also orders the inserts on a first
        // start, when both are added in one save.
        builder.HasOne<Provider>().WithMany().HasForeignKey(row => row.Provider).OnDelete(DeleteBehavior.Restrict);
    }
}
