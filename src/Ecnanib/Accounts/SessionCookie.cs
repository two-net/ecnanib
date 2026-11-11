namespace Ecnanib.Accounts;

/// <summary>
/// The cookie that carries the token of a session: sent only over TLS, hidden from scripts, and
/// gone when the browser closes (NFR-SEC-1). This is the only place that reads or writes it.
/// </summary>
internal static class SessionCookie
{
    /// <summary>
    /// The <c>__Host-</c> prefix makes a browser refuse the cookie unless this host set it over
    /// TLS for the whole host, so another site under the same domain cannot plant one.
    /// </summary>
    public const string Name = "__Host-ecnanib-session";

    /// <summary>The token a request carries, or null when it carries none.</summary>
    public static string? TokenOf(HttpRequest request) =>
        request.Cookies.TryGetValue(Name, out var token) && !string.IsNullOrEmpty(token) ? token : null;

    public static void Set(HttpResponse response, string token) => response.Cookies.Append(Name, token, Options());

    /// <summary>Takes the cookie from the browser.</summary>
    public static void Clear(HttpResponse response) => response.Cookies.Delete(Name, Options());

    // The same for setting and for clearing: a browser ignores the removal of a cookie with this
    // prefix unless the removal is marked Secure as well.
    private static CookieOptions Options() => new()
    {
        Secure = true,
        HttpOnly = true,

        // Sent when a link in an email opens a page, and not with a form that another site posts.
        SameSite = SameSiteMode.Lax,
        Path = "/",
        IsEssential = true,

        // No expiry: the cookie ends with the browser, and the server ends the session.
    };
}
