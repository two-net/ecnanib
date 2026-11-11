using Ecnanib.Platform.Settings;
using Microsoft.AspNetCore.CookiePolicy;

namespace Ecnanib.Platform.Security;

/// <summary>
/// How requests travel: over TLS, with cookies that are sent only over TLS and that scripts
/// cannot read (NFR-SEC-1). See docs/security.md.
/// </summary>
/// <remarks>
/// This registers the options. The middleware that goes by them is added in the composition root,
/// where the order of the request pipeline is in one place.
/// </remarks>
internal static class TransportSecurity
{
    /// <summary>
    /// The cookie that carries the token against request forgery. The <c>__Host-</c> prefix makes
    /// a browser refuse the cookie unless this host set it, so another site under the same domain
    /// cannot plant one.
    /// </summary>
    public const string AntiforgeryCookie = "__Host-ecnanib-antiforgery";

    public static IServiceCollection AddTransportSecurity(this IServiceCollection services, ApplicationSettings application)
    {
        // A request over plain HTTP is sent on to the same address over TLS. The port is named
        // here because the host cannot always work it out, and then lets the request through.
        services.AddHttpsRedirection(options => options.HttpsPort = application.PublicUrl.Port);

        // A browser that has been here once does not try plain HTTP again for a year.
        services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));

        // Every cookie, whoever sets it.
        services.Configure<CookiePolicyOptions>(options =>
        {
            options.Secure = CookieSecurePolicy.Always;
            options.HttpOnly = HttpOnlyPolicy.Always;
            options.MinimumSameSitePolicy = SameSiteMode.Lax;
        });

        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = AntiforgeryCookie;

            // The prefix requires the whole host as the path.
            options.Cookie.Path = "/";
        });

        return services;
    }
}
