using Ecnanib.Kernel;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Platform.Audit;

/// <summary>
/// One audit record (SRS 6.1). The table refuses UPDATE and TRUNCATE, through a trigger the
/// AddAuditEvents migration installs. DELETE stays allowed for retention (#50). There's no foreign key
/// to users, because records outlive accounts (FR-AUTH-09).
/// </summary>
internal sealed class AuditEvent
{
    public const int TargetTypeMaxLength = 64;

    public const int TargetIdMaxLength = 128;

    public long Id { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The account the record is about. See <see cref="AuditEntry.UserId"/>.</summary>
    public Guid? UserId { get; init; }

    public required AuditActorKind ActorKind { get; init; }

    public Guid? ActorId { get; init; }

    public required AuditAction Action { get; init; }

    public string? TargetType { get; init; }

    public string? TargetId { get; init; }

    /// <summary>The masked before/after summary as a JSON array, or null when there's none.</summary>
    public string? Summary { get; init; }

    public string? IpAddress { get; init; }

    public string? UserAgent { get; init; }

    public string? CorrelationId { get; init; }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events");
        builder.HasKey(record => record.Id);
        builder.Property(record => record.ActorKind).HasConversion<string>();
        builder.Property(record => record.Action).HasConversion<string>();
        builder.Property(record => record.TargetType).HasMaxLength(AuditEvent.TargetTypeMaxLength);
        builder.Property(record => record.TargetId).HasMaxLength(AuditEvent.TargetIdMaxLength);
        builder.Property(record => record.Summary).HasColumnType("jsonb");
        builder.Property(record => record.IpAddress).HasMaxLength(ClientText.AddressMaxLength);
        builder.Property(record => record.UserAgent).HasMaxLength(ClientText.UserAgentMaxLength);

        // A user's history pages by ID. Retention (#50) and the administrators' search use the time.
        builder.HasIndex(record => new { record.UserId, record.Id });
        builder.HasIndex(record => record.Timestamp);
        builder.HasIndex(record => record.CorrelationId);
    }
}
