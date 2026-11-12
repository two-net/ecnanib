namespace Ecnanib.Accounts;

/// <summary>
/// The cookie that carries the token of a sign-in that waits for a code, from the page that took
/// the password to the page that takes the code (FR-ACC-3). It is sent only over TLS and is hidden
/// from scripts (NFR-SEC-1). This is the only place that reads or writes it.
/// </summary>
internal static class PendingSignInCookie
{
    /// <summary>
    /// The <c>__Host-</c> prefix makes a browser refuse the cookie unless this host set it over
    /// TLS for the whole host, so another site under the same domain cannot plant one.
    /// </summary>
    public const string Name = "__Host-ecnanib-sign-in";

    /// <summary>The token a request carries, or null when it carries none.</summary>
    public static string? TokenOf(HttpRequest request) =>
        request.Cookies.TryGetValue(Name, out var token) && !string.IsNullOrEmpty(token) ? token : null;

    public static void Set(HttpResponse response, string token) => response.Cookies.Append(Name, token, Options());

    /// <summary>
    /// Takes the cookie from the browser of a request that carried it. A request without one is
    /// left as it is, so that its answer does not name a cookie the browser never had.
    /// </summary>
    public static void Clear(HttpContext request)
    {
        if (TokenOf(request.Request) is not null)
        {
            request.Response.Cookies.Delete(Name, Options());
        }
    }

    // The same for setting and for clearing: a browser ignores the removal of a cookie with this
    // prefix unless the removal is marked Secure as well.
    private static CookieOptions Options() => new()
    {
        Secure = true,
        HttpOnly = true,

        // Only the two pages of signing in use it, and the one leads to the other. No link from
        // an email or from another site needs it.
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,

        // No expiry: the cookie ends with the browser, and the server ends the sign-in.
    };
}
