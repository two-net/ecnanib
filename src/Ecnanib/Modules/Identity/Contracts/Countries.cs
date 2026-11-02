using System.Collections;
using System.Globalization;
using System.Resources;

namespace Ecnanib.Modules.Identity.Contracts;

public sealed record Country(string Code, string Name);

/// <summary>
/// What a user can declare at registration, and what Legal:BlockedCountries may name (NFR-LEG-04).
/// Countries.resx is the one list, so a code that a form can't offer can't be blocked by mistake.
/// </summary>
public static class Countries
{
    private static readonly ResourceManager Resources = new("Ecnanib.Modules.Identity.Countries", typeof(Countries).Assembly);

    private static readonly Lazy<IReadOnlyList<Country>> Sorted = new(Load);

    private static readonly Lazy<HashSet<string>> Codes = new(() => [.. Sorted.Value.Select(country => country.Code)]);

    /// <summary>In alphabetical order of name.</summary>
    public static IReadOnlyList<Country> All => Sorted.Value;

    public static bool IsKnown(string code) => Codes.Value.Contains(code);

    private static List<Country> Load()
    {
        var names = Resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true)
            ?? throw new InvalidOperationException("The list of countries is missing.");

        return
        [
            .. names.Cast<DictionaryEntry>()
                .Select(entry => new Country((string)entry.Key, (string)entry.Value!))
                .OrderBy(country => country.Name, StringComparer.InvariantCulture),
        ];
    }
}
