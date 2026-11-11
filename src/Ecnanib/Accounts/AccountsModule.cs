using Microsoft.AspNetCore.Authentication;

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

        services.AddSingleton<Sessions>();
        services.AddSingleton<ISignIn, SignIn>();
        services.AddSingleton<IPasswordReset, PasswordReset>();

        // The one way a request is known to be signed in, for the Web UI and the API alike.
        services.AddAuthentication(SessionAuthentication.Scheme)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthentication.Scheme, configureOptions: null);

        return services;
    }
}
