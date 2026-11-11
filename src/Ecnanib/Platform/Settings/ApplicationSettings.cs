namespace Ecnanib.Platform.Settings;

/// <summary>The <c>Application</c> section: what the service is called, where it is reached, and who operates it.</summary>
/// <param name="Name">The product name shown to users.</param>
/// <param name="PublicUrl">
/// The address users reach the service at, used in links sent by email. It is an <c>https</c>
/// address, and its port is where a request over plain HTTP is sent on to (NFR-SEC-1).
/// </param>
/// <param name="RegistrationOpen">Whether new users may register (FR-ACC-7).</param>
/// <param name="Operators">The email addresses of the users who are Operators (FR-OPS-1).</param>
public sealed record ApplicationSettings(
    string Name,
    Uri PublicUrl,
    bool RegistrationOpen,
    IReadOnlyList<string> Operators)
{
    internal static ApplicationSettings Read(SettingsReader file)
    {
        const string operatorsKey = "Application:Operators";

        var name = file.Text("Application:Name");
        var publicUrl = file.HttpsUrl("Application:PublicUrl");
        var registrationOpen = file.Flag("Application:RegistrationOpen");
        var operators = file.List(operatorsKey).Select(file.EmailAddress).ToList();
        file.Distinct(operatorsKey, operators);

        return new ApplicationSettings(name, publicUrl, registrationOpen, operators);
    }
}
