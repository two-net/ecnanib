using Ecnanib.Accounts.Abstractions;
using Ecnanib.Accounts.Authentication;
using Ecnanib.Accounts.Configuration;
using Ecnanib.Accounts.Services;
using Ecnanib.Configuration;
using Ecnanib.Database.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Accounts;

public static class AccountsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the accounts module: validated <see cref="AccountsOptions"/> and <see cref="AgentLimitsOptions"/>,
    /// Argon2id password hashing, the sign-in throttle, <see cref="IAccountService"/>, <see cref="IFairUseLimits"/>,
    /// cookie authentication with security-stamp revalidation, authorization, the Blazor authentication state and the
    /// per-request <see cref="UserPreferencesResolver"/>. Requires <c>AddDatabase</c>, <c>AddServer</c> and <c>AddUi</c>
    /// (new accounts take its defaults). Adds no hosted service and connects to nothing.
    /// </summary>
    public static IServiceCollection AddAccounts(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AccountsOptions>()
            .Bind(configuration.GetSection(AccountsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AccountsOptions>, AccountsOptionsValidator>());

        services.AddOptions<AgentLimitsOptions>()
            .Bind(configuration.GetSection(AgentLimitsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AgentLimitsOptions>, AgentLimitsOptionsValidator>());

        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);

        // NFR-SEC-01: Argon2id behind the framework interface; swapping the scheme is this one registration.
        services.TryAddSingleton<IPasswordHasher<User>, Argon2idPasswordHasher>();
        services.TryAddSingleton<SignInThrottle>();
        services.TryAddSingleton<SessionRevocations>();
        services.TryAddSingleton<ISecurityStampSource, DatabaseSecurityStampSource>();
        services.TryAddSingleton<SessionValidator>();
        services.TryAddSingleton<IAccountService, AccountService>();
        services.TryAddSingleton<IFairUseLimits, FairUseLimits>();
        services.TryAddScoped<AccountCookieEvents>();
        // FR-UI-02: the root component reads the signed-in user's theme once per request; visitors get the Ui defaults.
        services.TryAddScoped<UserPreferencesResolver>();

        // The key ring lives in the database (FR-CFG-05); a fixed application name keeps every cookie valid when the
        // deployment moves, instead of binding it to the content-root path.
        services.AddDataProtection().SetApplicationName("Ecnanib");

        // FR-ACC-02: one persistent, sliding cookie session. EventsType resolves from the request scope.
        services.AddAuthentication(AccountSession.Scheme).AddCookie();
        services.AddOptions<CookieAuthenticationOptions>(AccountSession.Scheme)
            .Configure<IOptions<AccountsOptions>, IOptions<ServerOptions>>((cookie, accounts, server) =>
            {
                cookie.Cookie.Name = AccountSession.CookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.IsEssential = true;
                // Secure whenever users reach the application over https (Server:PublicUrl, NFR-SEC-02); a plain-http
                // public URL is a local deployment, where the flag would make sign-in impossible.
                cookie.Cookie.SecurePolicy = server.Value.IsHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                cookie.ExpireTimeSpan = accounts.Value.SessionLifetime;
                cookie.SlidingExpiration = true;
                cookie.LoginPath = "/sign-in";
                cookie.LogoutPath = "/sign-out";
                cookie.ReturnUrlParameter = "ReturnUrl";
                cookie.EventsType = typeof(AccountCookieEvents);
            });

        // AddRazorComponents registers no authorization services; UseAuthorization needs these.
        services.AddAuthorization();
        services.AddCascadingAuthenticationState();
        // Not TryAdd: AddInteractiveServerComponents already TryAdds its own provider and may run first.
        services.AddScoped<AuthenticationStateProvider, AccountAuthenticationStateProvider>();

        return services;
    }
}
