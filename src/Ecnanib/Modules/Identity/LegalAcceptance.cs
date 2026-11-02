using Ecnanib.Modules.Identity.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// One document version one user accepted (FR-AUTH-02, NFR-LEG-01). Rows are only added, so the table
/// is the history of what each user agreed to and when.
/// </summary>
internal sealed class LegalAcceptance
{
    public required Guid UserId { get; init; }

    public required LegalDocument Document { get; init; }

    public required string Version { get; init; }

    public required DateTimeOffset AcceptedAt { get; init; }
}

internal sealed class LegalAcceptanceConfiguration : IEntityTypeConfiguration<LegalAcceptance>
{
    public void Configure(EntityTypeBuilder<LegalAcceptance> builder)
    {
        builder.ToTable("legal_acceptances");
        builder.HasKey(acceptance => new { acceptance.UserId, acceptance.Document, acceptance.Version });
        builder.Property(acceptance => acceptance.Document).HasConversion<string>();
        builder.HasOne<User>().WithMany().HasForeignKey(acceptance => acceptance.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
