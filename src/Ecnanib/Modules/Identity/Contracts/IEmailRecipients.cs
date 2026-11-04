using Ecnanib.Kernel;

namespace Ecnanib.Modules.Identity.Contracts;

/// <summary>
/// Who gets notification email, and for what (FR-NTF-02, FR-PREF-06). It hands out other users'
/// addresses, so only Notifications may call it, which BannedApis enforces. See Notifications in
/// docs/architecture.md.
/// </summary>
public interface IEmailRecipients
{
    /// <summary>
    /// The users among <paramref name="userIds"/> whose address is verified, each with the types they
    /// take by email: what they chose, or <see cref="EmailNotificationChoices.Defaults"/>, and the
    /// administrator-only types only for administrators. An unverified address could be a stranger's,
    /// so it's left out, and so is an account that's gone. One query, whatever the count.
    /// </summary>
    Task<IReadOnlyList<EmailRecipient>> FindAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}

/// <summary>A class, not a record, so that its ToString can't put an address in a log.</summary>
public sealed class EmailRecipient(Guid userId, string address, IReadOnlySet<NotificationType> types)
{
    public Guid UserId { get; } = userId;

    /// <summary>Verified. Never log it.</summary>
    public string Address { get; } = address;

    public IReadOnlySet<NotificationType> Types { get; } = types;

    public override string ToString() => nameof(EmailRecipient);
}
