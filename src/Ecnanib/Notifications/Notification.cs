using Ecnanib.Accounts;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
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
/// <para>
/// <b>It says the mode that it is about,</b> as whoever raised it said it then: the mode of the
/// agent, of the position, or of the Binance key. It keeps that mode when the agent is switched,
/// and when the agent is deleted, so what happened in Demo is told apart from what happened in
/// Live for good (FR-BIN-11). One that is about no mode, such as a key of an AI provider, says
/// none.
/// </para>
/// </remarks>
/// <param name="id">The id of the notification, which what raised it assigned.</param>
/// <param name="userId">The user whose notification it is.</param>
/// <param name="type">What it is about.</param>
/// <param name="agentId">The agent that it is about, or null when it is about none.</param>
/// <param name="mode">The mode that it is about, or null when it is about none.</param>
/// <param name="content">What it says, in plain words.</param>
/// <param name="isRead">Whether its user has read it.</param>
/// <param name="createdAt">When it was raised.</param>
/// <param name="emailPending">
/// Whether an email is still to be sent for it: it is about a critical event, and its user had
/// opted in to email when it was raised (FR-NTF-3). See <see cref="NotificationEmails"/>.
/// </param>
/// <param name="emailedAt">When the email for it was handed to the email delivery service, or null when none was.</param>
public sealed class Notification(
    Guid id,
    Guid userId,
    NotificationType type,
    Guid? agentId,
    TradingMode? mode,
    string content,
    bool isRead,
    DateTimeOffset createdAt,
    bool emailPending,
    DateTimeOffset? emailedAt) : IUserOwned
{
    /// <summary>The most characters that a notification says.</summary>
    public const int ContentLength = 500;

    public Guid Id { get; } = id;

    public Guid UserId { get; } = userId;

    public NotificationType Type { get; } = type;

    public Guid? AgentId { get; } = agentId;

    public TradingMode? Mode { get; } = mode;

    public string Content { get; } = content;

    public bool IsRead { get; } = isRead;

    public DateTimeOffset CreatedAt { get; } = createdAt;

    public bool EmailPending { get; } = emailPending;

    public DateTimeOffset? EmailedAt { get; } = emailedAt;
}

/// <summary>
/// What a notification is about. A member is stored by its name, so renaming one changes stored
/// data and needs a migration. What raises a notification adds its member when it is built. What
/// a member is called for its user, and whether it is a critical event, is in
/// <see cref="NotificationTypes"/>.
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

    /// <summary>
    /// An agent was halted: its loss in the UTC day reached its daily loss limit, or its equity
    /// fell from its peak by its drawdown limit, and the service closes its position (FR-RSK-8,
    /// FR-NTF-1).
    /// </summary>
    AgentHalted,

    /// <summary>
    /// A role of an agent was given another model by strength, by the service: its user turned
    /// automatic reassignment on for the agent, and is told of each change (FR-TEAM-10).
    /// </summary>
    RolesReassigned,

    /// <summary>
    /// An agent opened a position: an entry of it was executed (FR-NTF-1). It says what the
    /// agent holds, at which price, and where its stop-loss and its take-profit are.
    /// </summary>
    PositionOpened,

    /// <summary>
    /// A position of an agent was closed, however it was: by its stop-loss or its take-profit,
    /// by the team, by its user, by the service, or at the exchange (FR-NTF-1). It is raised
    /// when the result of the position is recorded, and says what the position made.
    /// </summary>
    PositionClosed,

    /// <summary>
    /// A key of the user is invalid: the provider of an AI key, or the exchange, refused it when
    /// it was checked again, or the service deleted a key of the exchange that can withdraw
    /// (FR-NTF-1, FR-KEY-3, FR-KEY-4). It is about no agent, and names no part of the key.
    /// </summary>
    KeyInvalid,

    /// <summary>
    /// A model of the team of an agent is unavailable: its provider no longer offers it, and the
    /// agent waits for its user, who gives the role another model (FR-NTF-1, FR-CAT-8).
    /// </summary>
    ModelUnavailable,

    /// <summary>
    /// An agent that its daily loss limit halted runs again: the UTC day in which it was halted
    /// is over, and the service resumed it (FR-RSK-8, FR-NTF-1).
    /// </summary>
    AgentResumed,
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
        builder.Property(notification => notification.Mode);
        builder.Property(notification => notification.Content).HasMaxLength(Notification.ContentLength);
        builder.Property(notification => notification.IsRead);
        builder.Property(notification => notification.CreatedAt);

        builder.Property(notification => notification.EmailPending);
        builder.Property(notification => notification.EmailedAt);

        // The notifications of a user that are about one agent, newest first, which the page of
        // the agent reads.
        builder.HasIndex(notification => new { notification.UserId, notification.AgentId, notification.CreatedAt });

        // All notifications of a user, newest first, which the page of the notifications reads.
        builder.HasIndex(notification => new { notification.UserId, notification.CreatedAt });

        // The ones that a user has not read, which every page counts for the link in its bar:
        // few rows, however many the user has read.
        builder.HasIndex(notification => notification.UserId).HasFilter("NOT is_read");

        // The ones that an email is still to be sent for, oldest first, which the job that sends
        // them reads: few rows, and most runs find none.
        builder.HasIndex(notification => notification.CreatedAt).HasFilter("email_pending");

        // The notifications belong to the account and go with it when it is deleted (FR-ACC-9).
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(notification => notification.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
