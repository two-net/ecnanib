using Ecnanib.Accounts.Abstractions;
using Ecnanib.Accounts.Authentication;
using Ecnanib.Accounts.Configuration;
using Ecnanib.Accounts.Services;
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
    /// cookie authentication with security-stamp revalidation, authorization and the Blazor authentication state.
    /// Requires <c>AddDatabase</c>. Adds no hosted service and connects to nothing.
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

        // The key ring lives in the database (FR-CFG-05); a fixed application name keeps every cookie valid when the
        // deployment moves, instead of binding it to the content-root path.
        services.AddDataProtection().SetApplicationName("Ecnanib");

        // FR-ACC-02: one persistent, sliding cookie session. EventsType resolves from the request scope.
        services.AddAuthentication(AccountSession.Scheme).AddCookie();
        services.AddOptions<CookieAuthenticationOptions>(AccountSession.Scheme)
            .Configure<IOptions<AccountsOptions>, IServiceProvider>((cookie, accounts, provider) =>
            {
                var development = provider.GetService<IHostEnvironment>()?.IsDevelopment() == true;
                cookie.Cookie.Name = AccountSession.CookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.IsEssential = true;
                // Secure everywhere except Development, where the http launch profile must keep working (NFR-SEC-02).
                cookie.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
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
