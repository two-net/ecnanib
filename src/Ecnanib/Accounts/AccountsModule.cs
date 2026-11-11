namespace Ecnanib.Accounts;

/// <summary>Accounts: registration, sign-in, preferences, audit trail (SRS 3.1). See docs/accounts.md.</summary>
public static class AccountsModule
{
    /// <summary>
    /// Registers the component. Its emails are sent through <see cref="IAccountMailer"/>, which
    /// Notifications implements and registers.
    /// </summary>
    public static IServiceCollection AddAccounts(this IServiceCollection services)
    {
        services.AddSingleton<PasswordHasher>();
        services.AddSingleton<IRegistration, Registration>();
        services.AddSingleton<IFairUse, FairUse>();

        return services;
    }
}
