using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Ecnanib.Modules.Identity.Contracts;

/// <summary>
/// The time zones a user can choose (FR-PREF-05), as IANA IDs, which is what a browser reports. They come
/// from the host, so a host without time zone data offers only UTC.
/// </summary>
public static partial class TimeZones
{
    /// <summary>Longer than any IANA ID, which tops out around 30 characters.</summary>
    public const int MaxIdLength = 64;

    private static readonly Lazy<IReadOnlyList<TimeZoneInfo>> Sorted = new(Load);

    private static readonly Lazy<Dictionary<string, TimeZoneInfo>> ById =
        new(() => Sorted.Value.ToDictionary(zone => zone.Id, StringComparer.Ordinal));

    /// <summary>What a user can save, in order of standard offset and then ID.</summary>
    public static IReadOnlyList<TimeZoneInfo> Offered => Sorted.Value;

    public static bool IsOffered(string id) => ById.Value.ContainsKey(id);

    /// <summary>
    /// Any zone the host knows, including aliases a browser may report that aren't offered, such as
    /// Asia/Calcutta. The ID can come from a cookie, and on Unix a lookup reads a file named after it, so
    /// anything that isn't shaped like an IANA ID is refused before it gets that far.
    /// </summary>
    public static bool TryFind(string? id, [NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        zone = null;

        if (id is null || id.Length > MaxIdLength || !IanaId().IsMatch(id))
        {
            return false;
        }

        return ById.Value.TryGetValue(id, out zone) || TimeZoneInfo.TryFindSystemTimeZoneById(id, out zone);
    }

    private static List<TimeZoneInfo> Load()
    {
        var zones = new Dictionary<string, TimeZoneInfo>(StringComparer.Ordinal) { [TimeZoneInfo.Utc.Id] = TimeZoneInfo.Utc };

        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            // Windows names its own zones, and finds the IANA one for each through ICU.
            if (zone.HasIanaId)
            {
                zones.TryAdd(zone.Id, zone);
            }
            else if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var ianaId)
                && TimeZoneInfo.TryFindSystemTimeZoneById(ianaId, out var ianaZone))
            {
                zones.TryAdd(ianaZone.Id, ianaZone);
            }
        }

        return [.. zones.Values.OrderBy(zone => zone.BaseUtcOffset).ThenBy(zone => zone.Id, StringComparer.Ordinal)];
    }

    /// <summary>Letters, digits, underscores, plus and minus, in up to three segments, and no dots.</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_+\-]*(?:/[A-Za-z0-9_+\-]+){0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex IanaId();
}
