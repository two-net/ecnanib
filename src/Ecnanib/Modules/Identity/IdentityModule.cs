using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.SignalR;

namespace Ecnanib.Modules.Identity;

public static class IdentityModule
{
    private const string SessionCookie = "ecnanib.session";

    public static IServiceCollection AddIdentity(this IServiceCollection services)
    {
        services.AddSingleton(provider => IdentitySettings.Read(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddSingleton<IAccounts, Accounts>();
        services.AddSingleton<SessionConnections>();
        services.AddSingleton<Sessions>();
        services.AddSingleton<ISessions>(provider => provider.GetRequiredService<Sessions>());
        services.AddSingleton<IRecurringJob, SessionExpiryJob>();
        services.AddSingleton<IAdministrators, Administrators>();
        services.AddSingleton<IAccountActivity, AccountActivity>();
        services.AddSingleton<IAuthorizationHandler, LegalAcceptedHandler>();
        services.AddSingleton<IAuthorizationHandler, AdministratorHandler>();

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<IdentitySettings, Sessions>((options, settings, sessions) =>
            {
                options.Cookie.Name = SessionCookie;
                options.LoginPath = "/sign-in";
                options.ReturnUrlParameter = "returnUrl";

                // A backstop. The row in sessions decides, so the idle timeout needs no sliding cookie,
                // and a circuit can count as activity even though it can't reissue one.
                options.ExpireTimeSpan = settings.SessionAbsoluteTimeout;
                options.SlidingExpiration = false;

                options.Events.OnValidatePrincipal = context => ValidateSessionAsync(context, sessions);

                // The default redirects to a page that doesn't exist. The status page answers this instead.
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        services.AddSingleton<SessionHubFilter>();
        services.Configure<HubOptions>(options => options.AddFilter<SessionHubFilter>());

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

    /// <summary>
    /// Runs on every request with a cookie, so that revoking or expiring a session applies to the next
    /// one. A hub request doesn't count as activity, because long polling makes one per poll, which
    /// would keep an idle tab alive.
    /// </summary>
    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext context, Sessions sessions)
    {
        var http = context.HttpContext;

        // The exception handler runs this again to render the error page, and a lookup that failed
        // would fail again, leaving an empty 500. The cookie stays, because the session may be fine.
        if (http.Features.Get<IExceptionHandlerFeature>() is not null)
        {
            context.RejectPrincipal();
            return;
        }

        var activity = http.GetEndpoint()?.Metadata.GetMetadata<HubMetadata>() is null;

        if (context.Principal is { } user
            && AccountClaims.SessionId(user) is { } sessionId
            && await sessions.SeenAsync(sessionId, activity, http.RequestAborted))
        {
            return;
        }

        context.RejectPrincipal();
        await http.SignOutAsync();
    }
}
