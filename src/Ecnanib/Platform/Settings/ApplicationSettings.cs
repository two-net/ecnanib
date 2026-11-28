using Ecnanib.Shared;

namespace Ecnanib.Platform.Settings;

/// <summary>
/// The <c>Application</c> section: what the service is called, where it is reached, who operates
/// it, and where it is not offered.
/// </summary>
/// <param name="Name">The product name shown to users.</param>
/// <param name="PublicUrl">
/// The address users reach the service at, used in links sent by email. It is an <c>https</c>
/// address, and its port is where a request over plain HTTP is sent on to (NFR-SEC-1).
/// </param>
/// <param name="RegistrationOpen">Whether new users may register (FR-ACC-7).</param>
/// <param name="Operators">The email addresses of the users who are Operators (FR-OPS-1).</param>
/// <param name="ShutdownSeconds">
/// How long the application has to stop once it is told to, from a minute and a half to an hour:
/// the cycles that run are stopped, and what they left is settled with the exchange, within it
/// (NFR-REL-5). See "Stopping" in docs/reliability.md.
/// </param>
/// <param name="RestrictedCountries">
/// The countries that the service is not offered in, each by its code of
/// <see cref="Countries"/> (NFR-LEG-3). Nobody who says that they live in one of them is
/// registered. The list may be empty, and then no visitor is asked where they live. See
/// "Restrictions by country" in docs/legal.md.
/// </param>
public sealed record ApplicationSettings(
    string Name,
    Uri PublicUrl,
    bool RegistrationOpen,
    IReadOnlyList<string> Operators,
    int ShutdownSeconds,
    IReadOnlyList<string> RestrictedCountries)
{
    /// <summary>
    /// The shortest time to stop in: a cycle that is being stopped takes 45 seconds at the most,
    /// and what is under way with the exchange when the time is nearly up takes as long again.
    /// </summary>
    public const int LeastShutdownSeconds = 90;

    // An hour. Whatever supervises the process waits for it for at least this long.
    private const int MostShutdownSeconds = 3600;

    internal static ApplicationSettings Read(SettingsReader file)
    {
        const string operatorsKey = "Application:Operators";
        const string restrictedCountriesKey = "Application:RestrictedCountries";

        var name = file.Text("Application:Name");
        var publicUrl = file.HttpsUrl("Application:PublicUrl");
        var registrationOpen = file.Flag("Application:RegistrationOpen");
        var operators = file.List(operatorsKey).Select(file.EmailAddress).ToList();
        file.Distinct(operatorsKey, operators);
        var shutdownSeconds = file.WholeNumber(
            "Application:ShutdownSeconds", minimum: LeastShutdownSeconds, maximum: MostShutdownSeconds);
        var restrictedCountries = file.List(restrictedCountriesKey).Select(key => Country(file, key)).ToList();
        file.Distinct(restrictedCountriesKey, restrictedCountries);

        return new ApplicationSettings(name, publicUrl, registrationOpen, operators, shutdownSeconds, restrictedCountries);
    }

    /// <summary>
    /// The code of a country, written as it is. A code that names no country restricts nobody,
    /// and nothing would say so: <c>UK</c> for <c>GB</c>, or a code in small letters. So the
    /// file is refused for one.
    /// </summary>
    private static string Country(SettingsReader file, string key)
    {
        var code = file.Text(key);
        if (code.Length > 0 && !Countries.IsCode(code))
        {
            file.Problem(key, "must be the code of a country in two capital letters, as ISO 3166-1 has it, such as GB");
            return string.Empty;
        }

        return code;
    }
}
