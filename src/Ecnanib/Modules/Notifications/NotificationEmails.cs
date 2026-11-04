using System.Globalization;
using System.Text;
using Ecnanib.Kernel;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Notifications;

/// <summary>
/// The email a user gets for the notifications the job found for them, as plain text. Links start with
/// App:PublicBaseUrl, never a request's Host, and are paths a notice has already checked, so none can
/// carry a token (FR-NTF-03). Times are UTC, as in the account emails.
/// </summary>
internal sealed class NotificationEmails(NotificationSettings settings)
{
    /// <summary>The list. Components/Pages/NotificationList.razor.</summary>
    public const string ListPath = "/notifications";

    /// <summary>Where the choices are. Components/Account/PreferenceSettings.razor.</summary>
    public const string PreferencesPath = "/account/preferences";

    /// <summary>The most one message lists. The rest are counted, with a link to the list.</summary>
    public const int MaxEntries = 20;

    /// <param name="entries">Newest first. At least one.</param>
    public EmailMessage For(string to, IReadOnlyList<NotificationEntry> entries)
    {
        var body = new StringBuilder();

        body.Append(entries.Count == 1 ? Emails.IntroOne : Format(Emails.IntroMany, entries.Count));

        foreach (var entry in entries.Take(MaxEntries))
        {
            body.Append("\n\n").Append(Format(Emails.Entry, Heading(entry), entry.CreatedAt, Link(entry.Link)));
        }

        if (entries.Count > MaxEntries)
        {
            body.Append("\n\n").Append(Format(Emails.More, entries.Count - MaxEntries, Link(ListPath)));
        }

        body.Append("\n\n").Append(Format(Emails.Footer, Link(PreferencesPath)));

        return new EmailMessage
        {
            To = to,
            Subject = entries.Count == 1 ? Format(Emails.SubjectOne, Title(entries[0].Type)) : Format(Emails.SubjectMany, entries.Count),
            TextBody = body.ToString(),
        };
    }

    /// <summary>Throws for a type without its text, which the tests find by emailing one of each.</summary>
    public static string Title(NotificationType type) => type switch
    {
        NotificationType.TradeOpened => Emails.TitleTradeOpened,
        NotificationType.TradeClosed => Emails.TitleTradeClosed,
        NotificationType.StopMoved => Emails.TitleStopMoved,
        NotificationType.ApprovalPending => Emails.TitleApprovalPending,
        NotificationType.ApprovalExpired => Emails.TitleApprovalExpired,
        NotificationType.AgentPausedLossLimit => Emails.TitleAgentPausedLossLimit,
        NotificationType.AgentPausedBudget => Emails.TitleAgentPausedBudget,
        NotificationType.AgentPausedErrors => Emails.TitleAgentPausedErrors,
        NotificationType.CredentialInvalid => Emails.TitleCredentialInvalid,
        NotificationType.ModelDeprecated => Emails.TitleModelDeprecated,
        NotificationType.ModelRetired => Emails.TitleModelRetired,
        NotificationType.KillSwitchExecuted => Emails.TitleKillSwitchExecuted,
        NotificationType.CatalogSyncFailed => Emails.TitleCatalogSyncFailed,
        NotificationType.ExchangeRateLimitBan => Emails.TitleExchangeRateLimitBan,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Give the type its title in Modules/Notifications/Emails.resx."),
    };

    private static string Heading(NotificationEntry entry) => (entry.Subject, entry.Demo) switch
    {
        (null, false) => Format(Emails.HeadingNoSubject, Title(entry.Type)),
        (null, true) => Format(Emails.HeadingNoSubjectDemo, Title(entry.Type)),
        (_, false) => Format(Emails.Heading, Title(entry.Type), entry.Subject),
        (_, true) => Format(Emails.HeadingDemo, Title(entry.Type), entry.Subject),
    };

    private static string Format(string format, params object?[] values) => string.Format(CultureInfo.InvariantCulture, format, values);

    private string Link(string path) =>
        $"{settings.PublicOrigin ?? throw new InvalidOperationException("App:PublicBaseUrl is required to email a link.")}{path}";
}

/// <summary>One notification as an email lists it.</summary>
internal sealed record NotificationEntry(NotificationType Type, string? Subject, bool Demo, string Link, DateTimeOffset CreatedAt);
