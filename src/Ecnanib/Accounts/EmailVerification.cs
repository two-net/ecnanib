namespace Ecnanib.Accounts;

/// <summary>The rules of verifying an email address (FR-ACC-1), and the link that does it.</summary>
public static class EmailVerification
{
    /// <summary>How long a verification link works after it was sent.</summary>
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(24);

    /// <summary>The path of the page that a verification link opens.</summary>
    public const string Path = "/verify-email";

    /// <summary>
    /// The query parameter that carries the token. It is called this so that the one place that
    /// masks secrets in logs recognises it in a logged address. See docs/logging.md.
    /// </summary>
    public const string TokenParameter = "token";

    /// <summary>The link for a token, under the address users reach the service at.</summary>
    public static Uri LinkFor(Uri publicUrl, string token)
    {
        ArgumentNullException.ThrowIfNull(publicUrl);
        ArgumentNullException.ThrowIfNull(token);

        return new Uri(publicUrl, $"{Path}?{TokenParameter}={Uri.EscapeDataString(token)}");
    }
}
