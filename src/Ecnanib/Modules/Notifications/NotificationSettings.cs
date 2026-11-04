namespace Ecnanib.Modules.Notifications;

/// <summary>
/// The one setting notifications read: where emailed links point. Identity checks it, and refuses to
/// start when email is on without it, so this only normalises it the same way, to an origin with no
/// path, so that a trailing slash can't double the one each link starts with.
/// </summary>
internal sealed class NotificationSettings
{
    private const string PublicBaseUrlKey = "App:PublicBaseUrl";

    private NotificationSettings()
    {
    }

    /// <summary>Such as https://ecnanib.example.com. Null only when email is off, when nothing needs it.</summary>
    public string? PublicOrigin { get; private init; }

    public static NotificationSettings Read(IConfiguration configuration) => new()
    {
        PublicOrigin = Uri.TryCreate(configuration[PublicBaseUrlKey], UriKind.Absolute, out var url)
            && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
                ? url.GetLeftPart(UriPartial.Authority)
                : null,
    };
}
