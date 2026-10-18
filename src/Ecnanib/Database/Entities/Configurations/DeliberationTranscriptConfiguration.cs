using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Database.Entities.Configurations;

internal sealed class DeliberationTranscriptConfiguration : IEntityTypeConfiguration<DeliberationTranscript>
{
    public void Configure(EntityTypeBuilder<DeliberationTranscript> builder)
    {
        builder.HasKey(t => new { t.DecisionId, t.Role });
        builder.Property(t => t.Output).HasColumnType("jsonb");
        builder.HasOne(t => t.Decision).WithMany(d => d.Transcripts).HasForeignKey(t => t.DecisionId).OnDelete(DeleteBehavior.Cascade);
        // The retention sweep deletes by creation time.
        builder.HasIndex(t => t.CreatedAt);
    }
}
