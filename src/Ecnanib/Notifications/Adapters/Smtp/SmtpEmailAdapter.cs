using System.Diagnostics;
using Ecnanib.Platform.Settings;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Ecnanib.Notifications.Adapters.Smtp;

/// <summary>
/// Sends email through the mail server that the <c>Email</c> section of the configuration file
/// names, over SMTP. See docs/accounts.md.
/// </summary>
/// <remarks>
/// <para>
/// Each message goes over a connection of its own. The service sends few messages, and a
/// connection that is kept would have to be watched and reopened.
/// </para>
/// <para>
/// The file has no key for encryption, so the port and the sign-in decide it: see
/// <see cref="EncryptionFor"/>. Credentials are never sent over a connection that is not encrypted.
/// </para>
/// </remarks>
internal sealed class SmtpEmailAdapter(
    EmailSettings settings,
    ApplicationSettings application,
    ILogger<SmtpEmailAdapter> logger) : IEmailAdapter
{
    // The port on which a mail server expects encryption from the first byte.
    private const int ImplicitTlsPort = 465;

    // How long the mail server may take to answer a command.
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(30);

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var mail = new MimeMessage();
        mail.From.Add(new MailboxAddress(application.Name, settings.From));
        mail.To.Add(new MailboxAddress(string.Empty, message.To));
        mail.Subject = message.Subject;
        mail.Body = new TextPart("plain") { Text = message.TextBody };

        var started = Stopwatch.GetTimestamp();

        try
        {
            using var client = new SmtpClient { Timeout = (int)AnswerTimeout.TotalMilliseconds };

            await client.ConnectAsync(settings.Host, settings.Port, EncryptionFor(settings), cancellationToken);

            if (settings.User is not null && settings.Password is not null)
            {
                await client.AuthenticateAsync(settings.User, settings.Password.Reveal(), cancellationToken);
            }

            await client.SendAsync(mail, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);

            // Without the address, which is personal data.
            logger.LogInformation("Email {Outcome} in {DurationMs} ms", "Succeeded", Milliseconds(started));
        }
        catch (Exception error)
        {
            // Logged here for its duration and outcome. What it means is the caller's to decide.
            logger.LogWarning(
                error,
                "Email {Outcome} in {DurationMs} ms",
                error is OperationCanceledException ? "Canceled" : "Failed",
                Milliseconds(started));

            throw;
        }
    }

    /// <summary>
    /// How the connection is encrypted. On port 465 it is encrypted from the start. On any other
    /// port, a server that takes a sign-in must offer encryption (STARTTLS) before the sign-in is
    /// sent, or nothing is sent. A server that takes no sign-in, such as a relay on the same
    /// machine, is used with encryption when it offers it and without when it does not.
    /// </summary>
    internal static SecureSocketOptions EncryptionFor(EmailSettings settings)
    {
        if (settings.Port == ImplicitTlsPort)
        {
            return SecureSocketOptions.SslOnConnect;
        }

        return settings.User is null ? SecureSocketOptions.StartTlsWhenAvailable : SecureSocketOptions.StartTls;
    }

    private static double Milliseconds(long started) =>
        Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1);
}
