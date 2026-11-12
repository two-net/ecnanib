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

        // Two-factor authentication. Its key is encrypted by the platform's StoredSecrets.
        services.AddSingleton<TwoFactorCodes>();
        services.AddSingleton<ITwoFactor, TwoFactor>();

        // What a sensitive action asks first: whether its user proved who they are again
        // (FR-ACC-4). Also registered as itself, for the sensitive action that Accounts does
        // itself, which asks inside its own transaction.
        services.AddSingleton<Reauthentication>();
        services.AddSingleton<IReauthentication>(provider => provider.GetRequiredService<Reauthentication>());

        // Deleting the account, and the export of what is stored about its user (FR-ACC-9,
        // NFR-SEC-9). Whether agents stand in the way is for IAgentActivity to say, which Agents
        // implements.
        services.AddSingleton<IAccountDeletion, AccountDeletion>();
        services.AddSingleton<IDataExport, DataExport>();

        // The one way a request is known to be signed in, for the Web UI and the API alike.
        services.AddAuthentication(SessionAuthentication.Scheme)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthentication.Scheme, configureOptions: null);

        // What an operator function requires, which is checked apart from whose a row is
        // (NFR-SEC-3). The session handler says who is an operator, on every request.
        services.AddSingleton<Operators>();
        services.AddAuthorizationBuilder()
            .AddPolicy(OperatorAccess.Policy, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => OperatorAccess.IsOperator(context.User)));

        return services;
    }
}
