using Ecnanib.Modules.Identity.Contracts;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// The settings accounts read. Read() is the only way in, and it reads these keys and no others,
/// because the Security and Email sections also hold keys that other issues own (CFG-03). A value
/// nobody configured takes its SRS 7.3 default, and a value that's wrong stops startup.
/// </summary>
internal sealed class IdentitySettings
{
    /// <summary>NFR-SEC-02, so configuration can raise this and can't lower it.</summary>
    public const int LowestPasswordMinLength = 12;

    /// <summary>Long enough for any passphrase or password manager, and it bounds the cost of a hash.</summary>
    public const int PasswordMaxLength = 256;

    public const int BootstrapSecretMinLength = 32;

    /// <summary>The value in SRS 7.3. It's long enough to pass, so it has to be refused by name.</summary>
    public const string SampleBootstrapSecret = "<random secret of at least 32 characters>";

    private const string PasswordMinLengthKey = "Security:PasswordMinLength";
    private const string MaxFailedSignInsKey = "Security:MaxFailedSignIns";
    private const string LockoutMinutesKey = "Security:LockoutMinutes";
    private const string SessionIdleTimeoutMinutesKey = "Security:SessionIdleTimeoutMinutes";
    private const string AdminEmailsKey = "Admin:AdminEmails";
    private const string BootstrapSecretKey = "Admin:BootstrapSecret";
    private const string EmailEnabledKey = "Email:Enabled";
    private const string BlockedCountriesKey = "Legal:BlockedCountries";

    private const int DefaultMaxFailedSignIns = 5;
    private const int DefaultLockoutMinutes = 15;
    private const int DefaultSessionIdleTimeoutMinutes = 60;

    private IdentitySettings()
    {
    }

    public required int PasswordMinLength { get; init; }

    public required int MaxFailedSignIns { get; init; }

    public required TimeSpan Lockout { get; init; }

    public required TimeSpan SessionIdleTimeout { get; init; }

    /// <summary>Normalized, so compare it with <see cref="EmailAddresses.Normalize"/>.</summary>
    public required IReadOnlySet<string> AdministratorEmails { get; init; }

    /// <summary>Null unless someone needs it, which means email is off and an address is listed.</summary>
    public required string? BootstrapSecret { get; init; }

    public required bool EmailEnabled { get; init; }

    public required IReadOnlySet<string> BlockedCountries { get; init; }

    /// <exception cref="InvalidOperationException">A setting is invalid. The message names it.</exception>
    public static IdentitySettings Read(IConfiguration configuration)
    {
        var passwordMinLength = configuration.GetValue(PasswordMinLengthKey, LowestPasswordMinLength);
        var maxFailedSignIns = configuration.GetValue(MaxFailedSignInsKey, DefaultMaxFailedSignIns);
        var lockoutMinutes = configuration.GetValue(LockoutMinutesKey, DefaultLockoutMinutes);
        var sessionIdleTimeoutMinutes = configuration.GetValue(SessionIdleTimeoutMinutesKey, DefaultSessionIdleTimeoutMinutes);
        var emailEnabled = configuration.GetValue(EmailEnabledKey, false);

        Require(
            passwordMinLength is >= LowestPasswordMinLength and <= PasswordMaxLength,
            $"{PasswordMinLengthKey} must be between {LowestPasswordMinLength} and {PasswordMaxLength}.");
        Require(maxFailedSignIns >= 1, $"{MaxFailedSignInsKey} must be at least 1.");
        Require(lockoutMinutes >= 1, $"{LockoutMinutesKey} must be at least 1.");
        Require(sessionIdleTimeoutMinutes >= 1, $"{SessionIdleTimeoutMinutesKey} must be at least 1.");

        var administrators = ReadAdministrators(configuration);

        return new IdentitySettings
        {
            PasswordMinLength = passwordMinLength,
            MaxFailedSignIns = maxFailedSignIns,
            Lockout = TimeSpan.FromMinutes(lockoutMinutes),
            SessionIdleTimeout = TimeSpan.FromMinutes(sessionIdleTimeoutMinutes),
            AdministratorEmails = administrators,
            BootstrapSecret = !emailEnabled && administrators.Count > 0 ? ReadBootstrapSecret(configuration) : null,
            EmailEnabled = emailEnabled,
            BlockedCountries = ReadBlockedCountries(configuration),
        };
    }

    private static HashSet<string> ReadAdministrators(IConfiguration configuration)
    {
        var emails = configuration.GetSection(AdminEmailsKey).Get<string[]>() ?? [];

        foreach (var (index, email) in emails.Index())
        {
            Require(EmailAddresses.IsValid(email), $"{AdminEmailsKey}:{index} must be an email address.");
        }

        return [.. emails.Select(EmailAddresses.Normalize)];
    }

    private static string ReadBootstrapSecret(IConfiguration configuration)
    {
        var secret = configuration[BootstrapSecretKey];

        Require(
            !string.IsNullOrWhiteSpace(secret),
            $"{BootstrapSecretKey} is required when {AdminEmailsKey} lists an address and {EmailEnabledKey} is false, "
            + "because nothing else can verify that address.");
        Require(
            secret!.Length >= BootstrapSecretMinLength && secret != SampleBootstrapSecret,
            $"{BootstrapSecretKey} must be a random value of at least {BootstrapSecretMinLength} characters, not the sample.");

        return secret;
    }

    private static HashSet<string> ReadBlockedCountries(IConfiguration configuration)
    {
        var codes = configuration.GetSection(BlockedCountriesKey).Get<string[]>() ?? [];

        foreach (var (index, code) in codes.Index())
        {
            Require(Countries.IsKnown(code), $"{BlockedCountriesKey}:{index} must be an uppercase ISO 3166-1 alpha-2 code, such as GB.");
        }

        return [.. codes];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
