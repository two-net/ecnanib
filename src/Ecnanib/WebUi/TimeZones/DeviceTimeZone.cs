using System.Text.RegularExpressions;

namespace Ecnanib.WebUi.TimeZones;

/// <summary>
/// The cookie that carries the time zone of a device: the pages write a time in the time zone
/// of the user, and which one that is, is of the device that they sit at (NFR-USA-3). The
/// device says it when its user signs in, in a field of the form that a script fills, and the
/// server keeps it here. This is the only place that reads or writes it. See docs/times.md.
/// </summary>
/// <remarks>
/// <para>
/// It is read by the server alone, which writes every time of a page in that time zone. So no
/// script reads it, and it is hidden from scripts as every cookie is. It says nothing of who
/// the user is, and it is neither set nor taken back by signing out: it is of the device.
/// </para>
/// <para>
/// <b>What a device says is checked before it is looked up, and again when it is read.</b> A
/// time zone is named as the time zone database names it, such as <c>Europe/Berlin</c>: letters,
/// digits, and the few signs of such a name, in three parts at the most. Only a name of that
/// form is looked up at all, because the lookup reads a file of that name. What names no time
/// zone that the machine knows is no time zone, and the pages are in UTC.
/// </para>
/// <para>
/// <b>A browser may say an older name of a time zone.</b> The time zone database has renamed
/// some time zones, and a browser goes by a list of its own that still has the older names:
/// Chromium says <c>Asia/Calcutta</c> for a device in India, where the database says
/// <c>Asia/Kolkata</c>. A machine may have the older names or not. So a name that the machine
/// does not know is looked up once more by its newer name: see <see cref="Renamed"/>.
/// </para>
/// </remarks>
internal static partial class DeviceTimeZone
{
    /// <summary>
    /// The <c>__Host-</c> prefix makes a browser refuse the cookie unless this host set it over
    /// TLS for the whole host, so another site under the same domain cannot plant one.
    /// </summary>
    public const string Name = "__Host-ecnanib-time-zone";

    /// <summary>The name of the field of a form that carries what the device says its time zone is.</summary>
    public const string Field = "TimeZone";

    /// <summary>The longest name of a time zone that is looked at.</summary>
    public const int LongestName = 64;

    /// <summary>
    /// How long a browser keeps what the device said. It is kept when the browser closes, and
    /// is a year old at the most: each sign-in on the device starts the year anew.
    /// </summary>
    public static readonly TimeSpan Kept = TimeSpan.FromDays(365);

    /// <summary>
    /// The time zone that the device of a request has said, or null for a device that has said
    /// none, or whose cookie names no time zone that the machine knows.
    /// </summary>
    public static TimeZoneInfo? Of(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Cookies.TryGetValue(Name, out var said) ? Known(said) : null;
    }

    /// <summary>
    /// The name that the device of a request has said for its time zone, as it said it, or null
    /// where <see cref="Of"/> is.
    /// </summary>
    public static string? NameOf(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Cookies.TryGetValue(Name, out var said) && Known(said) is not null ? said : null;
    }

    /// <summary>
    /// Keeps what a device said its time zone is, on the device, and says whether it did. What
    /// names no time zone is not kept, and nothing at all is written for it: what the device
    /// said before stays as it is.
    /// </summary>
    /// <param name="response">The answer that carries the cookie.</param>
    /// <param name="said">What the device said: the name of a time zone, or anything else.</param>
    public static bool Keep(HttpResponse response, string? said)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (said is null || Known(said) is null)
        {
            return false;
        }

        response.Cookies.Append(Name, said, new CookieOptions
        {
            Secure = true,
            HttpOnly = true,

            // Sent when a link from another site opens a page, so that the page is in its time
            // zone from the first, and not with a form that another site posts.
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = Kept,

            // It is what the pages need to say a time as their user reads the clock, and
            // nothing follows them by it.
            IsEssential = true,
        });

        return true;
    }

    /// <summary>
    /// The time zone that a name names, or null for what is no name of one: anything that is
    /// not written as the time zone database writes a name, and a name that the machine does
    /// not know.
    /// </summary>
    public static TimeZoneInfo? Known(string? name)
    {
        if (name is not { Length: > 0 and <= LongestName } || !WrittenAsAName().IsMatch(name))
        {
            return null;
        }

        if (TimeZoneInfo.TryFindSystemTimeZoneById(name, out var zone))
        {
            return zone;
        }

        // An older name that the machine does not have: the same time zone, under its newer name.
        return Renamed.TryGetValue(name, out var newer) && TimeZoneInfo.TryFindSystemTimeZoneById(newer, out var renamed)
            ? renamed
            : null;
    }

    /// <summary>
    /// The older names that browsers still say for a time zone, each with the name that the time
    /// zone database has for it now. A machine whose database has both finds the older one
    /// itself. One that has dropped the older names would answer such a device in UTC without
    /// this list.
    /// </summary>
    private static readonly Dictionary<string, string> Renamed = new(StringComparer.Ordinal)
    {
        ["Africa/Asmera"] = "Africa/Asmara",
        ["America/Buenos_Aires"] = "America/Argentina/Buenos_Aires",
        ["America/Catamarca"] = "America/Argentina/Catamarca",
        ["America/Cordoba"] = "America/Argentina/Cordoba",
        ["America/Jujuy"] = "America/Argentina/Jujuy",
        ["America/Mendoza"] = "America/Argentina/Mendoza",
        ["America/Coral_Harbour"] = "America/Atikokan",
        ["America/Godthab"] = "America/Nuuk",
        ["America/Indianapolis"] = "America/Indiana/Indianapolis",
        ["America/Louisville"] = "America/Kentucky/Louisville",
        ["Asia/Calcutta"] = "Asia/Kolkata",
        ["Asia/Katmandu"] = "Asia/Kathmandu",
        ["Asia/Rangoon"] = "Asia/Yangon",
        ["Asia/Saigon"] = "Asia/Ho_Chi_Minh",
        ["Atlantic/Faeroe"] = "Atlantic/Faroe",
        ["Europe/Kiev"] = "Europe/Kyiv",
        ["Pacific/Enderbury"] = "Pacific/Kanton",
        ["Pacific/Ponape"] = "Pacific/Pohnpei",
        ["Pacific/Truk"] = "Pacific/Chuuk",
    };

    // A name of the time zone database: "UTC", "Europe/Berlin", "America/Argentina/Buenos_Aires",
    // "Etc/GMT+5". No dot, no blank, and no slash at its start, so it names no file but one of
    // that database.
    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_+\-]*(/[A-Za-z0-9_+\-]+){0,2}\z")]
    private static partial Regex WrittenAsAName();
}
