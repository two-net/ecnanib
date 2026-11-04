namespace Ecnanib.Components.Account;

/// <summary>
/// Carries a sign-in's challenge from the password step to the code step. A cookie rather than the URL,
/// which proxies log, or a hidden field, which a phone loses when it reloads the tab after the user
/// switches to their authenticator app. Only pages under /sign-in see it, and it lasts as long as the
/// challenge. The cookie policy adds Secure outside Development. See Two-factor in docs/architecture.md.
/// </summary>
internal static class SignInChallengeCookie
{
    public const string Name = "ecnanib.sign-in";

    private const string Path = "/sign-in";

    public static void Set(HttpContext http, string challenge, DateTimeOffset expiresAt) =>
        http.Response.Cookies.Append(Name, challenge, Options(expiresAt));

    public static string? Read(HttpContext http) => http.Request.Cookies[Name];

    public static void Delete(HttpContext http) => http.Response.Cookies.Delete(Name, Options(expiresAt: null));

    private static CookieOptions Options(DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = Path,
        Expires = expiresAt,
        IsEssential = true,
    };
}
