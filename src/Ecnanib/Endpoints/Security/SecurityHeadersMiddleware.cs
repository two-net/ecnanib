using System.Security.Cryptography;

namespace Ecnanib.Endpoints.Security;

/// <summary>
/// NFR-SEC-06. Call it outside the exception handler, so that a request has one nonce however many
/// times the pipeline runs. See docs/architecture.md.
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    private const int NonceBytes = 16;

    /// <summary>In HttpContext.Items, because error and status pages get a new scope and a scoped service would differ.</summary>
    private static readonly object NonceKey = new();

    // dotnet watch refreshes the browser over a WebSocket on a port of its own.
    private readonly string _connectSources = environment.IsDevelopment()
        ? "'self' ws://localhost:* wss://localhost:*"
        : "'self'";

    /// <summary>The nonce an inline script needs in order to run on this request's page.</summary>
    public static string NonceOf(HttpContext context) => (string)context.Items[NonceKey]!;

    public Task InvokeAsync(HttpContext context)
    {
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(NonceBytes));

        context.Items[NonceKey] = nonce;

        // Not set directly, because the exception handler clears the headers before it responds.
        // Assigned rather than appended, so this replaces the frame-ancestors policy that interactive
        // server rendering adds and the X-Frame-Options value that antiforgery adds.
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            headers.ContentSecurityPolicy = ContentSecurityPolicy(nonce);
            headers.XFrameOptions = "DENY";
            headers.XContentTypeOptions = "nosniff";

            // Stops a token in a query string leaking to other sites, and unlike no-referrer it
            // keeps the Origin header on same-origin POSTs.
            headers["Referrer-Policy"] = "same-origin";

            return Task.CompletedTask;
        });

        return next(context);
    }

    private string ContentSecurityPolicy(string nonce) =>
        "default-src 'self'; "
        + $"script-src 'self' 'nonce-{nonce}'; "
        + "style-src 'self'; "
        + "img-src 'self'; "
        + $"connect-src {_connectSources}; "
        + "object-src 'none'; "
        + "base-uri 'self'; "
        + "form-action 'self'; "
        + "frame-ancestors 'none'";
}
