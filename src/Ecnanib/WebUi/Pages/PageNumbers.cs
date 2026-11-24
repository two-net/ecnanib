using System.Globalization;

namespace Ecnanib.WebUi.Pages;

/// <summary>
/// Which page of a list an address asks for, in one place: for the pages that show a list a
/// page at a time, each under a name of its own in the address.
/// </summary>
public static class PageNumbers
{
    /// <summary>
    /// The page that an address names under a key. Null for an address that names a page which
    /// there is not: no whole number from 1, or a page twice. A page that is absent or empty is
    /// the first. What the address names besides is not gone by.
    /// </summary>
    /// <param name="query">What the address names.</param>
    /// <param name="key">The name of the choice of a page in the address.</param>
    public static int? Of(IQueryCollection query, string key)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrEmpty(key);

        var number = query[key];

        // A choice that is made twice is none that there is.
        if (number.Count > 1)
        {
            return null;
        }

        // A choice that is absent or empty is not made.
        if (number is not [{ Length: > 0 } written])
        {
            return 1;
        }

        // As the page itself writes a number, and in no other form of one.
        return int.TryParse(written, NumberStyles.None, CultureInfo.InvariantCulture, out var page)
            && page >= 1
            && page.ToString(CultureInfo.InvariantCulture) == written
                ? page
                : null;
    }
}
