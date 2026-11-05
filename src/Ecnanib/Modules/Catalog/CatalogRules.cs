using System.Globalization;

namespace Ecnanib.Modules.Catalog;

/// <summary>
/// What a value in the catalog's metadata may be, so the configuration file and administrators' forms
/// agree. Each caller adds its own: the Overrides section trims and refuses a wildcard, for instance.
/// </summary>
internal static class CatalogRules
{
    /// <summary>A display name or family.</summary>
    public const int TextMaxLength = 100;

    public const int ModelMaxLength = 256;

    public const decimal MaximumPrice = 1_000_000m;

    /// <summary>No control characters, which also keeps a NUL out of PostgreSQL.</summary>
    public static bool IsText(string value) => value.Length <= TextMaxLength && !value.Any(char.IsControl);

    /// <summary>
    /// One model's exact ID. It's compared exactly with the catalog's, so a space is refused rather than trimmed.
    /// </summary>
    public static bool IsModelId(string value) =>
        value.Length is > 0 and <= ModelMaxLength && !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));

    /// <summary>US dollars per million tokens, from 0 (DATA-R-02).</summary>
    public static bool IsPrice(decimal value) => value is >= 0 and <= MaximumPrice;

    /// <summary>
    /// A price as the configuration and the forms write it: digits and a decimal point, with no sign or
    /// thousands separator, in the invariant culture, such as <c>2.5</c>.
    /// </summary>
    public static bool TryParsePrice(string value, out decimal price) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out price) && IsPrice(price);
}
