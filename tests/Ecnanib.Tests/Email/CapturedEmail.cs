using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Ecnanib.Platform.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Tests.Email;

/// <summary>
/// Stands in for the SMTP server: it keeps each message, synchronously, so a test can follow the link
/// in it. SmtpEmailSenderTests covers the real sender.
/// </summary>
public sealed partial class CapturedEmail : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public IReadOnlyList<EmailMessage> Sent => [.. _sent];

    public void Send(EmailMessage message) => _sent.Enqueue(message);

    /// <summary>Puts this in place of the application's sender.</summary>
    public void ReplaceSender(IServiceCollection services) => services.Replace(ServiceDescriptor.Singleton<IEmailSender>(this));

    public IReadOnlyList<EmailMessage> To(string address) => [.. Sent.Where(message => message.To == address)];

    /// <summary>The link in the newest message to <paramref name="address"/>, as an absolute URL.</summary>
    public Uri LastLinkTo(string address) => LinkIn(To(address)[^1]);

    /// <summary>
    /// The same, as a path and query that a <see cref="Identity.Visitor"/> can open. Links start with
    /// App:PublicBaseUrl, which the test client's host isn't.
    /// </summary>
    public string LastPathTo(string address) => LastLinkTo(address).PathAndQuery;

    public static Uri LinkIn(EmailMessage message) => new(Links().Matches(message.TextBody).Single().Value);

    /// <summary>The token in a link, for a test that has to look for it somewhere else.</summary>
    public static string TokenIn(string link) => Tokens().Match(link).Groups["token"].Value;

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex Links();

    [GeneratedRegex("token=(?<token>[A-Za-z0-9_-]+)")]
    private static partial Regex Tokens();
}
