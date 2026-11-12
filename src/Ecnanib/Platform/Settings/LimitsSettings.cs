namespace Ecnanib.Platform.Settings;

/// <summary>
/// The <c>Limits</c> section: fair-use limits that apply equally to every user (FR-ACC-7), and how
/// often signing in, registering, and asking for a password reset may be tried (NFR-SEC-4).
/// </summary>
/// <param name="MaxAgentsPerUser">The most agents one user may have.</param>
/// <param name="SignIn">How often a password may be tried.</param>
/// <param name="Registration">How often the registration form may be sent.</param>
/// <param name="PasswordReset">How often a password reset link may be asked for.</param>
public sealed record LimitsSettings(
    int MaxAgentsPerUser,
    AttemptLimitSettings SignIn,
    AttemptLimitSettings Registration,
    AttemptLimitSettings PasswordReset)
{
    internal static LimitsSettings Read(SettingsReader file) =>
        new(
            file.WholeNumber("Limits:MaxAgentsPerUser"),
            AttemptLimitSettings.Read(file, "Limits:SignIn"),
            AttemptLimitSettings.Read(file, "Limits:Registration"),
            AttemptLimitSettings.Read(file, "Limits:PasswordReset"));
}

/// <summary>How often one thing may be tried. See "Limits on attempts" in docs/accounts.md.</summary>
/// <param name="PerClient">How many times from one client, which is an IP address, within the period.</param>
/// <param name="PerAddress">How many times for one email address within the period, whoever tries.</param>
/// <param name="Minutes">The period, in minutes. An attempt counts until it is this old.</param>
public sealed record AttemptLimitSettings(int PerClient, int PerAddress, int Minutes)
{
    /// <summary>The period as a length of time.</summary>
    public TimeSpan Period => TimeSpan.FromMinutes(Minutes);

    internal static AttemptLimitSettings Read(SettingsReader file, string key) =>
        new(
            file.WholeNumber(SettingsReader.Join(key, "PerClient")),
            file.WholeNumber(SettingsReader.Join(key, "PerAddress")),
            file.WholeNumber(SettingsReader.Join(key, "Minutes"), minimum: 1, maximum: 1440));
}
