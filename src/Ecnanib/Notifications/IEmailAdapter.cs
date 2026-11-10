namespace Ecnanib.Notifications;

/// <summary>
/// The common interface the email delivery service sits behind. Implementations live under
/// <c>Notifications/Adapters</c>.
/// </summary>
public interface IEmailAdapter
{
    /// <summary>Hands one message to the email delivery service.</summary>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>A plain-text email to one recipient.</summary>
public sealed record EmailMessage(string To, string Subject, string TextBody);
