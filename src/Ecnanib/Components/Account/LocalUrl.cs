using Microsoft.AspNetCore.Http.HttpResults;

namespace Ecnanib.Components.Account;

internal static class LocalUrl
{
    /// <summary>A return URL is a query parameter that anyone can write in a link, so only a local one is followed.</summary>
    public static string Or(string? url, string fallback) => RedirectHttpResult.IsLocalUrl(url) ? url! : fallback;
}
