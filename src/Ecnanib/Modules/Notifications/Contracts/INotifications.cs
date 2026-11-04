using System.Text;
using System.Text.RegularExpressions;
using Ecnanib.Kernel;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Notifications.Contracts;

/// <summary>
/// In-app notifications (FR-NTF-01), and the email that follows them for the events a user chose
/// (FR-NTF-02). Each feature raises its own. In-app is always on, so a notification is a row whatever
/// the email settings, and email goes out in the background a minute or so later. See Notifications in
/// docs/architecture.md.
/// </summary>
public interface INotifications
{
    /// <summary>How many records <see cref="ListAsync"/> returns at most. Fewer means there are no older ones.</summary>
    int PageSize { get; }

    /// <summary>
    /// Adds a notification to <paramref name="context"/> without saving, so the caller's save commits it
    /// with the change it's about, or neither commits. Across several statements, put both in one
    /// transaction. This is the way to raise one alongside a change, such as a trade.
    /// </summary>
    void Add(EcnanibDbContext context, Notice notice);

    /// <summary>Saves a notification on its own, for a raiser with nothing else to commit.</summary>
    Task RaiseAsync(Notice notice, CancellationToken cancellationToken);

    /// <summary>One notification for each account that's an administrator now (FR-NTF-01).</summary>
    Task RaiseForAdministratorsAsync(AdministratorNotice notice, CancellationToken cancellationToken);

    Task<int> CountUnreadAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// The user's notifications, newest first. Pass the last ID of one page as <paramref name="before"/>
    /// to get the next.
    /// </summary>
    Task<IReadOnlyList<NotificationRecord>> ListAsync(Guid userId, long? before, CancellationToken cancellationToken);

    /// <summary>Does nothing for another user's notification, or one that's already read.</summary>
    Task MarkReadAsync(Guid userId, long id, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the user's notifications up to <paramref name="throughId"/> as read: the newest one they've
    /// seen, so one that arrived since stays unread.
    /// </summary>
    Task MarkAllReadAsync(Guid userId, long throughId, CancellationToken cancellationToken);
}

/// <summary>
/// One notification for one user. It's checked as it's built, so a bad value throws here rather than
/// failing the caller's save. A class, not a record, so that its ToString can't put the subject in a log.
/// </summary>
public sealed partial class Notice
{
    public const int LinkMaxLength = 256;

    public const int SubjectMaxLength = 120;

    /// <param name="type">Not one of <see cref="EmailNotificationChoices.AdministratorOnly"/>: see <see cref="AdministratorNotice"/>.</param>
    /// <param name="userId">The account it's for, which only that account can see.</param>
    /// <param name="link">
    /// The page it's about, as a path on this site such as <c>/agents/42</c> (FR-NTF-03): segments of
    /// letters, digits, hyphens and underscores, with no query, so that it can never carry a token.
    /// </param>
    /// <param name="subject">
    /// What it's about, as the user would recognise it, such as an agent's name or a key's label. Shown
    /// only as text. Never a secret: control characters are removed, it's cut to
    /// <see cref="SubjectMaxLength"/>, and anything that looks like a secret is masked, but that can't
    /// recognise a secret with no marker.
    /// </param>
    /// <param name="demo">Whether it's about a Demo agent, which is labelled wherever it appears (FR-UI-07).</param>
    /// <exception cref="ArgumentException">The type is for administrators, or the link isn't a path on this site.</exception>
    public Notice(NotificationType type, Guid userId, string link, string? subject = null, bool demo = false)
    {
        if (EmailNotificationChoices.AdministratorOnly.Contains(type))
        {
            throw new ArgumentException($"{type} is for administrators. Raise it with RaiseForAdministratorsAsync.", nameof(type));
        }

        Type = type;
        UserId = userId;
        Link = CheckLink(link);
        Subject = Clean(subject);
        Demo = demo;
    }

    public NotificationType Type { get; }

    public Guid UserId { get; }

    public string Link { get; }

    public string? Subject { get; }

    public bool Demo { get; }

    public override string ToString() => $"{nameof(Notice)} {Type}";

    /// <exception cref="ArgumentException">Not a path of plain segments on this site.</exception>
    internal static string CheckLink(string link)
    {
        if (link is null || link.Length > LinkMaxLength || !LocalPath().IsMatch(link))
        {
            throw new ArgumentException(
                $"A notification links to a path on this site, such as /agents/42, of at most {LinkMaxLength} characters, "
                + "with only letters, digits, hyphens and underscores between slashes, and no query.",
                nameof(link));
        }

        return link;
    }

    /// <summary>
    /// What PostgreSQL can store and a page can show: no control characters, which include a NUL, and no
    /// half of a surrogate pair. Null when nothing is left.
    /// </summary>
    internal static string? Clean(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var kept = new StringBuilder(Math.Min(text.Length, SubjectMaxLength));

        for (var index = 0; index < text.Length && kept.Length < SubjectMaxLength; index++)
        {
            var character = text[index];

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) && kept.Length + 2 <= SubjectMaxLength)
                {
                    kept.Append(character).Append(text[++index]);
                }
            }
            else if (!char.IsLowSurrogate(character) && !char.IsControl(character))
            {
                kept.Append(character);
            }
        }

        var cleaned = kept.ToString().Trim();

        return cleaned.Length == 0 ? null : cleaned;
    }

    [GeneratedRegex("^(/[A-Za-z0-9_-]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalPath();
}

/// <summary>A notification for every administrator, with the same rules as a <see cref="Notice"/>.</summary>
public sealed class AdministratorNotice
{
    /// <param name="type">One of <see cref="EmailNotificationChoices.AdministratorOnly"/>.</param>
    /// <exception cref="ArgumentException">The type isn't for administrators, or the link isn't a path on this site.</exception>
    public AdministratorNotice(NotificationType type, string link, string? subject = null)
    {
        if (!EmailNotificationChoices.AdministratorOnly.Contains(type))
        {
            throw new ArgumentException($"{type} is raised for a user. Raise it with a Notice.", nameof(type));
        }

        Type = type;
        Link = Notice.CheckLink(link);
        Subject = Notice.Clean(subject);
    }

    public NotificationType Type { get; }

    public string Link { get; }

    public string? Subject { get; }

    public override string ToString() => $"{nameof(AdministratorNotice)} {Type}";
}

/// <param name="Subject">What it's about, or null. Anyone with an account can choose this text, so only show it as text.</param>
/// <param name="Link">A path on this site.</param>
/// <param name="ReadAt">Null while it's unread.</param>
public sealed record NotificationRecord(
    long Id,
    DateTimeOffset CreatedAt,
    NotificationType Type,
    string? Subject,
    bool Demo,
    string Link,
    DateTimeOffset? ReadAt);
