using System.Net;
using System.Net.Mail;

namespace Ecnanib.Platform.Email;

/// <summary>
/// The SMTP settings in the Email section (EXT-EML-01). Read() is the only way in. With email off it
/// reads Enabled and nothing else, so a deployment without email needs none of the rest. With it on,
/// a missing or invalid value stops startup with a message that names the setting (CFG-03).
/// </summary>
internal sealed class EmailSettings
{
    /// <summary>STARTTLS. 465 means TLS from the first byte instead.</summary>
    public const int DefaultSmtpPort = 587;

    public const int ImplicitTlsPort = 465;

    private const string EnabledKey = "Email:Enabled";
    private const string SmtpHostKey = "Email:SmtpHost";
    private const string SmtpPortKey = "Email:SmtpPort";
    private const string UseTlsKey = "Email:UseTls";
    private const string UsernameKey = "Email:Username";
    private const string PasswordKey = "Email:Password";
    private const string FromAddressKey = "Email:FromAddress";

    private EmailSettings()
    {
    }

    /// <summary>Where mail goes. Null when email is off.</summary>
    public SmtpServer? Server { get; private init; }

    /// <exception cref="InvalidOperationException">A setting is missing or invalid. The message names it.</exception>
    public static EmailSettings Read(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue(EnabledKey, false))
        {
            return new EmailSettings();
        }

        var host = configuration[SmtpHostKey]?.Trim();
        var port = configuration.GetValue(SmtpPortKey, DefaultSmtpPort);
        var useTls = configuration.GetValue(UseTlsKey, true);
        var username = NullIfEmpty(configuration[UsernameKey]);
        var password = NullIfEmpty(configuration[PasswordKey]);
        var from = configuration[FromAddressKey]?.Trim();

        Require(
            !string.IsNullOrEmpty(host) && Uri.CheckHostName(host) != UriHostNameType.Unknown,
            $"{SmtpHostKey} must be a host name or IP address when {EnabledKey} is true.");
        Require(port is >= 1 and <= 65535, $"{SmtpPortKey} must be between 1 and 65535.");
        Require(
            useTls || environment.IsDevelopment() || IsLoopback(host!),
            $"{UseTlsKey} must be true outside Development, unless {SmtpHostKey} is on this machine, "
            + "because reset links would otherwise cross the network in clear.");
        Require(
            (username is null) == (password is null),
            $"{UsernameKey} and {PasswordKey} must be set together, or both left out for a server that needs no sign-in.");
        Require(
            !string.IsNullOrEmpty(from) && IsBareAddress(from),
            $"{FromAddressKey} must be an email address, such as no-reply@example.com, when {EnabledKey} is true.");

        return new EmailSettings
        {
            Server = new SmtpServer
            {
                Host = host!,
                Port = port,
                UseTls = useTls,
                Username = username,
                Password = password,
                FromAddress = from!,
            },
        };
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    /// <summary>MailAddress alone accepts a display name, as in "Sam &lt;a@b&gt;".</summary>
    private static bool IsBareAddress(string value) =>
        !value.Any(char.IsControl) && MailAddress.TryCreate(value, out var address) && address.Address == value;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

/// <summary>An SMTP server, as the configuration file describes it. Only there when email is on.</summary>
internal sealed class SmtpServer
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    /// <summary>
    /// TLS is required, never just used when the server offers it, so a server or network that drops
    /// STARTTLS can't make a reset link travel in clear.
    /// </summary>
    public required bool UseTls { get; init; }

    /// <summary>Null when the server takes mail without signing in, and then so is <see cref="Password"/>.</summary>
    public required string? Username { get; init; }

    public required string? Password { get; init; }

    public required string FromAddress { get; init; }

    public override string ToString() => nameof(SmtpServer);
}
