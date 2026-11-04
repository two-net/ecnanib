using Microsoft.AspNetCore.Http.HttpResults;

namespace Ecnanib.Components.Account;

internal static class LocalUrl
{
    /// <summary>A return URL is a query parameter that anyone can write in a link, so only a local one is followed.</summary>
    public static string Or(string? url, string fallback) => RedirectHttpResult.IsLocalUrl(url) ? url! : fallback;

    /// <summary>
    /// <paramref name="path"/>, carrying <paramref name="returnUrl"/> on when it's local, so a sign-in in
    /// two steps still ends where it was going. <paramref name="query"/> goes first, if there is one.
    /// </summary>
    public static string WithReturn(string path, string? returnUrl, string? query = null)
    {
        var parameters = new List<string>();

        if (query is not null)
        {
            parameters.Add(query);
        }

        if (RedirectHttpResult.IsLocalUrl(returnUrl))
        {
            parameters.Add($"returnUrl={Uri.EscapeDataString(returnUrl!)}");
        }

        return parameters.Count == 0 ? path : $"{path}?{string.Join('&', parameters)}";
    }
}
