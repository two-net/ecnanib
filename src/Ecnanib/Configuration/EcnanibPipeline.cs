using Ecnanib.Accounts;
using Ecnanib.Components;
using Microsoft.Extensions.Options;

namespace Ecnanib.Configuration;

/// <summary>
/// The request pipeline, shared by <c>Program.cs</c> and the tests' in-memory server so that what is tested is what
/// runs. The order is the security requirements' (NFR-SEC-01, NFR-SEC-02): the trusted proxy's forwarded headers
/// first, so the client address and the scheme behind it are the real ones (the sign-in throttle and the Secure cookie
/// policy read them); the error and status-code pages; when <c>Server:PublicUrl</c> is https, a request the proxy
/// received over plain http is redirected to the public origin and HSTS is sent; the response headers every page
/// carries; authentication before antiforgery, whose tokens are bound to the identity; then the endpoints.
/// </summary>
public static class EcnanibPipeline
{
    public const string ContentSecurityPolicy = "frame-ancestors 'none'";

    public const string ReferrerPolicy = "same-origin";

    public const string PermissionsPolicy = "camera=(), microphone=(), geolocation=(), payment=()";

    public static WebApplication UseEcnanibPipeline(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var server = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;

        app.UseForwardedHeaders();

        // There is no Development environment: the error page and status-code pages are the same everywhere.
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

        if (server.IsHttps)
        {
            var publicOrigin = server.PublicUri!;
            app.Use((context, next) => RedirectToPublicOrigin(context, next, publicOrigin));
            app.UseHsts();
        }

        app.Use(SecurityHeaders);

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
        app.MapAccountEndpoints();

        return app;
    }

    /// <summary>
    /// NFR-SEC-02 behind the proxy: every request Kestrel sees as plain http is sent to the same path on the public
    /// origin, built from <c>Server:PublicUrl</c> rather than a Host header the proxy may have rewritten to the upstream
    /// address. That is a request the trusted proxy received over http (<c>X-Forwarded-Proto: http</c>) and a direct hit
    /// on Kestrel alike; a proxy that is not trusted or forwards no scheme makes every request look like that and loops
    /// here, which is the visible failure (the status page's Transport card shows what the application sees), whereas
    /// serving such a request would fail on every form, because the antiforgery cookie is Secure. 307 keeps the method.
    /// </summary>
    private static Task RedirectToPublicOrigin(HttpContext context, RequestDelegate next, Uri publicOrigin)
    {
        var request = context.Request;
        if (request.IsHttps)
            return next(context);

        var target = new UriBuilder(publicOrigin)
        {
            Path = (request.PathBase + request.Path).Value,
            Query = request.QueryString.HasValue ? request.QueryString.Value![1..] : string.Empty,
        };
        context.Response.Redirect(target.Uri.AbsoluteUri, permanent: false, preserveMethod: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Set when the response starts, so an error page that cleared the response still carries them: no content-type
    /// sniffing, no framing (the antiforgery service's own X-Frame-Options is suppressed in favour of this pair), referrers
    /// stay on this origin, and no browser feature the application does not use. A full Content-Security-Policy is not set:
    /// the Blazor script, the import map and the reconnect UI would need nonces.
    /// </summary>
    private static Task SecurityHeaders(HttpContext context, RequestDelegate next)
    {
        context.Response.OnStarting(static state =>
        {
            var headers = ((HttpContext)state).Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers["Referrer-Policy"] = ReferrerPolicy;
            headers["Permissions-Policy"] = PermissionsPolicy;
            return Task.CompletedTask;
        }, context);
        return next(context);
    }
}
