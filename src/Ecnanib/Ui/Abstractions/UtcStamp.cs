using System.Globalization;

namespace Ecnanib.Ui.Abstractions;

/// <summary>
/// The one way a page writes an instant (NFR-USE-03, SRS "all times UTC"): the UTC value, in the invariant culture, so a
/// stamp labelled UTC is UTC whatever offset the value carries and reads the same on every host. <see cref="Minute"/> is
/// the pages' usual precision, <see cref="Second"/> the health and log pages'; a missing value reads "never".
/// </summary>
public static class UtcStamp
{
    public const string Never = "never";

    public static string Minute(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    public static string Minute(DateTimeOffset? at) => at is { } value ? Minute(value) : Never;

    public static string Second(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    public static string Second(DateTimeOffset? at) => at is { } value ? Second(value) : Never;
}
