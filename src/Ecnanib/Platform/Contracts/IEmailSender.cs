namespace Ecnanib.Platform.Contracts;

/// <summary>
/// Sends email through the SMTP server in the configuration file (EXT-EML-01). Only call it when
/// email is enabled: with it off, nothing is configured to send through, so this throws. See Email
/// in docs/architecture.md.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends <paramref name="message"/> in the background, as one recorded job run, and returns at once.
    /// The caller isn't told whether it arrived: a failure is logged, and the run is recorded as failed.
    /// So the time a request takes can't show whether it sent anything. Nothing stores the message, so
    /// one that a crash cuts off is lost, and the user asks again.
    /// </summary>
    /// <exception cref="InvalidOperationException">Email is disabled.</exception>
    void Send(EmailMessage message);
}

/// <summary>
/// A plain-text message to one address. A class, not a record, because a record's ToString would print
/// the body, which can hold a link with a token in it. Never log the body or the address.
/// </summary>
public sealed class EmailMessage
{
    /// <summary>A bare address, already validated.</summary>
    public required string To { get; init; }

    public required string Subject { get; init; }

    /// <summary>Plain text, so nothing in it is ever read as markup.</summary>
    public required string TextBody { get; init; }

    public override string ToString() => nameof(EmailMessage);
}
