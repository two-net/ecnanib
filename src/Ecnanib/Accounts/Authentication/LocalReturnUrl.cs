namespace Ecnanib.Accounts.Authentication;

/// <summary>
/// Keeps a return URL on this site: only a path that starts with a single <c>/</c> (not <c>//</c> or <c>/\</c>) and
/// contains no control characters is used; anything else becomes the landing page.
/// </summary>
internal static class LocalReturnUrl
{
    public static string Sanitize(string? url) => IsLocal(url) ? url! : "/";

    public static bool IsLocal(string? url)
    {
        if (string.IsNullOrEmpty(url) || url[0] != '/')
            return false;
        if (url.Length > 1 && (url[1] == '/' || url[1] == '\\'))
            return false;
        foreach (var c in url)
        {
            if (char.IsControl(c))
                return false;
        }

        return true;
    }
}
