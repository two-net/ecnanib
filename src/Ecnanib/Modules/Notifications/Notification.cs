using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Modules.Notifications.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Notifications;

/// <summary>
/// One notification for one user (SRS 6.1). There's no foreign key to users, because another module
/// owns that table: account deletion reaches this one through the port Identity declares (#51).
/// </summary>
internal sealed class Notification
{
    /// <summary>The longest <see cref="NotificationType"/> name, with room to spare.</summary>
    public const int TypeMaxLength = 32;

    public long Id { get; init; }

    public required Guid UserId { get; init; }

    public required NotificationType Type { get; init; }

    /// <summary>A <see cref="NotificationPayload"/> as JSON, so a feature can add a field without a migration.</summary>
    public required string Payload { get; init; }

    public required string Link { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ReadAt { get; init; }

    /// <summary>
    /// Whether the email job still has to look at it. Set when it's raised, from whether email is on, so
    /// turning email on later doesn't send old notifications.
    /// </summary>
    public required bool EmailDue { get; init; }

    /// <summary>When the job handed it to the sender, which isn't proof that it arrived. Null if it wasn't emailed.</summary>
    public DateTimeOffset? EmailedAt { get; init; }
}

/// <summary>What a notification says beyond its type. Add a field as a nullable property, so older rows still read.</summary>
internal sealed record NotificationPayload(string? Subject, bool Demo)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static NotificationPayload FromJson(string json) =>
        JsonSerializer.Deserialize<NotificationPayload>(json, Json) ?? new NotificationPayload(null, false);
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(notification => notification.Id);
        builder.Property(notification => notification.Type).HasConversion<string>().HasMaxLength(Notification.TypeMaxLength);
        builder.Property(notification => notification.Payload).HasColumnType("jsonb");
        builder.Property(notification => notification.Link).HasMaxLength(Notice.LinkMaxLength);

        // A user's list pages by ID. The count and the email job only look at the rows still waiting.
        // Named, because two indexes on the same columns would otherwise be one.
        builder.HasIndex(notification => new { notification.UserId, notification.Id }, "List")
            .HasDatabaseName("ix_notifications_user_id_id");
        builder.HasIndex(notification => notification.UserId, "Unread")
            .HasDatabaseName("ix_notifications_user_id_unread")
            .HasFilter("read_at IS NULL");
        builder.HasIndex(notification => new { notification.UserId, notification.Id }, "EmailDue")
            .HasDatabaseName("ix_notifications_user_id_id_email_due")
            .HasFilter("email_due");
    }
}
