using Ecnanib.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

namespace Ecnanib.Modules.Identity;

public static class IdentityModule
{
    private const string SessionCookie = "ecnanib.session";

    public static IServiceCollection AddIdentity(this IServiceCollection services)
    {
        services.AddSingleton(provider => IdentitySettings.Read(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddSingleton<IAccounts, Accounts>();
        services.AddSingleton<IAdministrators, Administrators>();
        services.AddSingleton<IAuthorizationHandler, LegalAcceptedHandler>();
        services.AddSingleton<IAuthorizationHandler, AdministratorHandler>();

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<IdentitySettings>((options, settings) =>
            {
                options.Cookie.Name = SessionCookie;
                options.LoginPath = "/sign-in";
                options.ReturnUrlParameter = "returnUrl";

                // #10 adds the absolute limit, revocation and listing on top of this.
                options.ExpireTimeSpan = settings.SessionIdleTimeout;
                options.SlidingExpiration = true;

                // The default redirects to a page that doesn't exist. The status page answers this instead.
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        // Attributes on one page combine as AND, and everything here sits behind plain [Authorize].
        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new LegalAcceptedRequirement())
                .Build())
            .AddPolicy(AccountPolicies.SignedIn, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(AccountPolicies.Administrator, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new AdministratorRequirement()));

        return services;
    }

    /// <summary>
    /// Call it in the slot Program.cs marks: the rate limiter has to see the account. It reads the
    /// settings here, so that a bad value stops startup before any migration runs.
    /// </summary>
    public static WebApplication UseAccountAuthentication(this WebApplication app)
    {
        _ = app.Services.GetRequiredService<IdentitySettings>();
        app.UseAuthentication();

        return app;
    }
}
