using Ecnanib.Platform.Contracts;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Text;

namespace Ecnanib.Platform.Email;

/// <summary>
/// Sends each message as its own job run, so a request never waits on the SMTP server. It never logs
/// the address or the body. The job runner logs a failed run with its exception, and a server's reply
/// can repeat the address it refused, so a refusal is rethrown with its codes only. See Email in
/// docs/architecture.md.
/// </summary>
internal sealed class SmtpEmailSender(EmailSettings settings, IJobRunner jobs) : IEmailSender
{
    /// <summary>The job name each send is recorded under. No recurring job has it.</summary>
    public const string JobName = "send-email";

    /// <summary>Connecting, signing in and sending, together.</summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(1);

    /// <summary>Each SMTP operation, which MailKit enforces itself, as a backstop to the run's token.</summary>
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

    public void Send(EmailMessage message)
    {
        var server = settings.Server
            ?? throw new InvalidOperationException("Email is disabled, so nothing can be sent. Check Email:Enabled before sending.");

        // A run never throws, so the task is safe to discard. The run's token fires at its timeout or at
        // the end of the drain window, so a message that's already going gets the chance to finish.
        _ = jobs.RunAsync(JobName, RunTimeout, (_, cancellationToken) => DeliverAsync(server, message, cancellationToken));
    }

    private static async Task DeliverAsync(SmtpServer server, EmailMessage message, CancellationToken cancellationToken)
    {
        using var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(string.Empty, server.FromAddress));
        mime.To.Add(new MailboxAddress(string.Empty, message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart(TextFormat.Plain) { Text = message.TextBody };

        using var client = new SmtpClient { Timeout = (int)OperationTimeout.TotalMilliseconds };

        try
        {
            await client.ConnectAsync(server.Host, server.Port, SocketOptions(server), cancellationToken);

            if (server is { Username: { } username, Password: { } password })
            {
                await client.AuthenticateAsync(username, password, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (SmtpCommandException exception)
        {
            // Not the inner exception: its message is the server's reply, which can name the mailbox.
            throw new EmailDeliveryException(
                $"The SMTP server refused the message with status {(int)exception.StatusCode} ({exception.ErrorCode}).");
        }
    }

    /// <summary>
    /// Never Auto or StartTlsWhenAvailable, which send in clear when a server, or anything between, doesn't
    /// offer STARTTLS.
    /// </summary>
    private static SecureSocketOptions SocketOptions(SmtpServer server) =>
        !server.UseTls ? SecureSocketOptions.None
        : server.Port == EmailSettings.ImplicitTlsPort ? SecureSocketOptions.SslOnConnect
        : SecureSocketOptions.StartTls;
}

/// <summary>An SMTP server's refusal, without its reply text, which can name the recipient.</summary>
internal sealed class EmailDeliveryException(string message) : Exception(message);
