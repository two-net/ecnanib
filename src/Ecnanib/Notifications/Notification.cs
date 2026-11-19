using Ecnanib.Accounts;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Notifications;

/// <summary>
/// An in-app message to a user, as a row of the <c>notifications</c> table: whose it is, what it
/// is about, what it says, and whether its user has read it (FR-NTF-1). See docs/notifications.md.
/// </summary>
/// <remarks>
/// <para>
/// It holds no secret: what raises one writes what happened in plain words, and never a key.
/// </para>
/// <para>
/// It names the agent that it is about by its id alone, and does not point at the agent's row:
/// Notifications may not use Agents. A notification belongs to the account, and stays when the
/// agent is deleted.
/// </para>
/// </remarks>
/// <param name="id">The id of the notification, which what raised it assigned.</param>
/// <param name="userId">The user whose notification it is.</param>
/// <param name="type">What it is about.</param>
/// <param name="agentId">The agent that it is about, or null when it is about none.</param>
/// <param name="content">What it says, in plain words.</param>
/// <param name="isRead">Whether its user has read it.</param>
/// <param name="createdAt">When it was raised.</param>
public sealed class Notification(
    Guid id, Guid userId, NotificationType type, Guid? agentId, string content, bool isRead, DateTimeOffset createdAt) : IUserOwned
{
    /// <summary>The most characters that a notification says.</summary>
    public const int ContentLength = 500;

    public Guid Id { get; } = id;

    public Guid UserId { get; } = userId;

    public NotificationType Type { get; } = type;

    public Guid? AgentId { get; } = agentId;

    public string Content { get; } = content;

    public bool IsRead { get; } = isRead;

    public DateTimeOffset CreatedAt { get; } = createdAt;
}

/// <summary>
/// What a notification is about. A member is stored by its name, so renaming one changes stored
/// data and needs a migration. What raises a notification adds its member when it is built.
/// </summary>
public enum NotificationType
{
    /// <summary>
    /// An order or the protection of a position failed: a stop-loss, or on the Futures market a
    /// take-profit, could not be placed, and the position was closed at market, or could not be
    /// closed either (FR-RSK-7, FR-BIN-F3).
    /// </summary>
    ProtectionFailure,

    /// <summary>
    /// An entry was skipped: the account of its user does not hold what the plan needs, which
    /// is the value of the entry on the Spot market and its margin on the Futures market
    /// (FR-BIN-S3, FR-BIN-F6), or the exchange did not set the margin of the symbol (FR-BIN-F2).
    /// </summary>
    EntrySkipped,

    /// <summary>
    /// An agent waits for its user: something that only they can change keeps it from trading
    /// (FR-NTF-1). It is raised when the account of its user at the exchange conflicts with how
    /// the agent trades (FR-BIN-F2).
    /// </summary>
    AttentionRequired,

    /// <summary>
    /// A position of the Futures market approaches liquidation: little of the way from the price
    /// that it was entered at to the price that the exchange liquidates it at is left
    /// (FR-BIN-F5). The service adds no margin to a position, so its user is told.
    /// </summary>
    LiquidationWarning,

    /// <summary>
    /// The records of an agent did not match what the exchange says, and were corrected: the
    /// account of its user no longer holds the position, or holds less or more of it, or an
    /// order that protected it no longer rested at the exchange (FR-BIN-7, FR-BIN-8).
    /// </summary>
    ReconciliationMismatch,
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(notification => notification.Id);

        // The application assigns the id, so a row can be written without asking the database for one.
        builder.Property(notification => notification.Id).ValueGeneratedNever();
        builder.Property(notification => notification.UserId);
        builder.Property(notification => notification.Type);
        builder.Property(notification => notification.AgentId);
        builder.Property(notification => notification.Content).HasMaxLength(Notification.ContentLength);
        builder.Property(notification => notification.IsRead);
        builder.Property(notification => notification.CreatedAt);

        // The notifications of a user that are about one agent, newest first, which the page of
        // the agent reads.
        builder.HasIndex(notification => new { notification.UserId, notification.AgentId, notification.CreatedAt });

        // The notifications belong to the account and go with it when it is deleted (FR-ACC-9).
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(notification => notification.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
