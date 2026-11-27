namespace Ecnanib.Platform.Settings;

/// <summary>The <c>Application</c> section: what the service is called, where it is reached, and who operates it.</summary>
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
public sealed record ApplicationSettings(
    string Name,
    Uri PublicUrl,
    bool RegistrationOpen,
    IReadOnlyList<string> Operators,
    int ShutdownSeconds)
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

        var name = file.Text("Application:Name");
        var publicUrl = file.HttpsUrl("Application:PublicUrl");
        var registrationOpen = file.Flag("Application:RegistrationOpen");
        var operators = file.List(operatorsKey).Select(file.EmailAddress).ToList();
        file.Distinct(operatorsKey, operators);
        var shutdownSeconds = file.WholeNumber(
            "Application:ShutdownSeconds", minimum: LeastShutdownSeconds, maximum: MostShutdownSeconds);

        return new ApplicationSettings(name, publicUrl, registrationOpen, operators, shutdownSeconds);
    }
}
