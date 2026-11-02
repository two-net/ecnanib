using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.RateLimiting;

namespace Ecnanib.Endpoints.Security;

/// <summary>
/// The HTTP edge: NFR-SEC-01, NFR-SEC-03, NFR-SEC-06 and NFR-SEC-07. It's in the Web UI unit because
/// pages need the nonce and endpoints need the policy names, and the Web UI can't use Platform.
/// </summary>
internal static class WebSecurity
{
    private static readonly TimeSpan HstsMaxAge = TimeSpan.FromDays(365);

    /// <summary>
    /// Every option reads the settings through DI, because a test's settings aren't on
    /// builder.Configuration until the host is built.
    /// </summary>
    public static IServiceCollection AddWebSecurity(this IServiceCollection services)
    {
        services.AddSingleton(provider => WebSecuritySettings.Read(
            provider.GetRequiredService<IConfiguration>(),
            provider.GetRequiredService<IHostEnvironment>()));

        services.AddOptions<ForwardedHeadersOptions>().Configure<WebSecuritySettings>((options, settings) =>
        {
            // Not the host: it would feed links and redirects from a header that proxies don't all set.
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // Walks back through every trusted proxy, and stops at the first address that isn't one.
            options.ForwardLimit = null;

            foreach (var network in settings.TrustedProxies)
            {
                options.KnownIPNetworks.Add(network);
            }
        });

        services.AddOptions<HttpsRedirectionOptions>().Configure<WebSecuritySettings>(
            (options, settings) => options.HttpsPort = settings.HttpsPort);

        // No IncludeSubDomains, which could lock an operator's unrelated sites to HTTPS for a year.
        services.AddHsts(options => options.MaxAge = HstsMaxAge);

        services.AddRateLimiter();
        services.AddOptions<RateLimiterOptions>().Configure<WebSecuritySettings>(
            (options, settings) => RateLimits.Configure(options, settings.RateLimits));

        return services;
    }

    /// <summary>
    /// Call it straight after UseRequestLogging. What it adds has to run once per request, so it goes
    /// outside the exception handler, which runs everything after it again.
    /// </summary>
    public static WebApplication UseWebSecurity(this WebApplication app)
    {
        // Resolved here so that a bad value stops startup before any migration runs.
        var settings = app.Services.GetRequiredService<WebSecuritySettings>();

        app.UseForwardedHeaders();
        app.UseMiddleware<SecurityHeadersMiddleware>();

        // Not Cookie.SecurePolicy on antiforgery, which throws on any request that isn't HTTPS. This
        // reaches every cookie, including ones that later issues add, and doesn't depend on the scheme.
        app.UseCookiePolicy(new CookiePolicyOptions
        {
            HttpOnly = HttpOnlyPolicy.Always,
            MinimumSameSitePolicy = SameSiteMode.Lax,
            Secure = app.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always,
        });

        // CORS doesn't cover WebSockets, and SameSite doesn't stop a sibling subdomain.
        var webSockets = new WebSocketOptions();

        if (settings.PublicOrigin is { } origin)
        {
            webSockets.AllowedOrigins.Add(origin);
        }

        app.UseWebSockets(webSockets);

        return app;
    }

    /// <summary>
    /// A probe that reaches the application directly, behind a proxy, would get a 307, and curl -f
    /// counts that as success without reaching the check. Health answers fixed values only.
    /// </summary>
    public static IApplicationBuilder UseHttpsRedirectionExceptHealth(this IApplicationBuilder app) =>
        app.UseWhen(
            context => !context.Request.Path.StartsWithSegments(HealthEndpoints.Prefix),
            branch => branch.UseHttpsRedirection());
}
