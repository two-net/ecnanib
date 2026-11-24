using Ecnanib.Accounts;

namespace Ecnanib.WebUi.Themes;

/// <summary>
/// The cookie that carries the theme a visitor chose: for a visitor the choice is saved on the
/// device (FR-THM-3). It says <c>dark</c> or <c>light</c>, and there is none for a visitor whose
/// pages look as their device is set. This is the only place that reads or writes it. See
/// docs/themes.md.
/// </summary>
/// <remarks>
/// It is read by the server alone, which writes the theme into the page that it answers with
/// (FR-THM-4). So no script reads it, and it is hidden from scripts as every cookie is. It says
/// nothing of who the visitor is, and it is neither set nor taken back by signing in or out.
/// </remarks>
internal static class DeviceTheme
{
    /// <summary>
    /// The <c>__Host-</c> prefix makes a browser refuse the cookie unless this host set it over
    /// TLS for the whole host, so another site under the same domain cannot plant one.
    /// </summary>
    public const string Name = "__Host-ecnanib-theme";

    /// <summary>
    /// How long a browser keeps the choice. It is kept when the browser closes, as a choice is,
    /// and is a year old at the most: saving it again starts the year anew.
    /// </summary>
    public static readonly TimeSpan Kept = TimeSpan.FromDays(365);

    /// <summary>
    /// The theme that the device of a request has saved. A device that saved none, or whose
    /// cookie says anything but a theme, looks as it is set.
    /// </summary>
    public static Theme Of(HttpRequest request) =>
        request.Cookies.TryGetValue(Name, out var word) && PageTheme.OfWord(word) is { } theme ? theme : Theme.System;

    /// <summary>
    /// Saves the theme that a visitor chose on their device. A device that is to look as it is
    /// set needs no cookie for that, and its cookie is taken back.
    /// </summary>
    public static void Set(HttpResponse response, Theme theme)
    {
        if (theme == Theme.System)
        {
            response.Cookies.Delete(Name, Options());

            return;
        }

        var options = Options();
        options.MaxAge = Kept;

        response.Cookies.Append(Name, PageTheme.WordOf(theme), options);
    }

    // The same for setting and for clearing: a browser ignores the removal of a cookie with this
    // prefix unless the removal is marked Secure as well.
    private static CookieOptions Options() => new()
    {
        Secure = true,
        HttpOnly = true,

        // Sent when a link from another site opens a page, so that the page is in its theme
        // from the first, and not with a form that another site posts.
        SameSite = SameSiteMode.Lax,
        Path = "/",

        // It is what the visitor asked for, and nothing follows them by it.
        IsEssential = true,
    };
}
