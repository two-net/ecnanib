using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Security;
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

        // How often signing in, registering, and asking for a reset may be tried (NFR-SEC-4).
        // Each of them asks this first, and so does whatever else takes a password.
        services.AddSingleton<AttemptLimits>();

        services.AddSingleton<Sessions>();
        services.AddSingleton<ISignIn, SignIn>();

        // What a page that follows what changes asks about the session of its connection, which
        // is not asked for again by itself, and what makes that such a page is no activity of
        // its user (FR-NTF-2, NFR-SEC-1).
        services.AddSingleton<IConnectedSession, ConnectedSession>();

        // When the session of a request ends, and how long before that its user is told so
        // and can say that they are still there (NFR-USA-1).
        services.AddSingleton<ISessionEnd, SessionEnds>();
        services.AddSingleton<IPasswordReset, PasswordReset>();

        // Two-factor authentication. Its key is encrypted by the platform's StoredSecrets, and
        // the column that holds it is declared, so that the key is encrypted again when the
        // active master key has changed (FR-KEY-7).
        services.AddSingleton<TwoFactorCodes>();
        services.AddSingleton<ITwoFactor, TwoFactor>();
        services.AddStoredSecret<User>(
            user => user.TwoFactorSecret,
            user => user.TwoFactorSecretKeyId,
            user => TwoFactorCodes.BoundTo(user.Id));

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

        // Disabling and enabling the account of a user, which an operator does (FR-OPS-4). What
        // depends on an account being enabled is for IAccountDependents to do, which Agents
        // implements.
        services.AddSingleton<IAccountDisabling, AccountDisabling>();

        // Announcing maintenance, cancelling it, and ending it, which an operator does
        // (NFR-REL-6). The platform keeps what is announced, and this records who did it.
        services.AddSingleton<IMaintenanceAnnouncements, MaintenanceAnnouncements>();

        // The theme that a user chose, which is stored with the account (FR-THM-3). A request
        // reads it with its session: see SessionAuthentication.ThemeClaimType.
        services.AddSingleton<IThemeChoice, StoredThemeChoice>();

        // How much of the pages moves for a user, which is stored with the account as the theme
        // is (FR-ANI-4). A request reads it with its session: see SessionAuthentication.MotionClaimType.
        services.AddSingleton<IMotionChoice, StoredMotionChoice>();

        // Which time zone the pages write a time in for a user, which is stored with the account
        // as the theme is (NFR-USA-3). A request reads it with its session: see
        // SessionAuthentication.TimeDisplayClaimType.
        services.AddSingleton<ITimeDisplayChoice, StoredTimeDisplayChoice>();

        // What a user said on the setup checklist of a first-time user, which is stored with the
        // account (FR-UI-4). Only the dashboard reads it, so it is read there and not with the
        // session.
        services.AddSingleton<ISetupMarks, StoredSetupMarks>();

        // Deletes the events of the audit trail that are of longer ago than they are kept, every
        // hour, in small batches (NFR-DAT-5). See docs/retention.md.
        services.AddSingleton<AuditRetention>();
        services.AddBackgroundJob<AuditRetentionJob>();

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
