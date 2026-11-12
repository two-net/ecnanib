using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Ecnanib.Platform.Security;

/// <summary>
/// What every answer tells the browser to refuse, against cross-site scripting and clickjacking
/// (NFR-SEC-4): scripts and styles that are not the application's own, and showing the
/// application in a frame. See docs/security.md.
/// </summary>
/// <remarks>
/// These are a second line. The first is that a page writes what a person typed as text, and
/// that no page is meant to be framed. The headers hold when a page gets that wrong.
/// </remarks>
internal static class BrowserDefenses
{
    private static readonly object NonceKey = new();

    /// <summary>
    /// Sets the headers on every answer: pages, the API, static files, the error page, and the
    /// redirect of plain HTTP. It goes first in the pipeline, so that nothing answers without them.
    /// </summary>
    public static IApplicationBuilder UseBrowserDefenses(this IApplicationBuilder app)
    {
        var policy = ContentSecurityPolicy(app.ApplicationServices.GetRequiredService<IHostEnvironment>());

        return app.Use((context, next) =>
        {
            // In the letters that a page writes as they are, so that the page and the header
            // carry the same text.
            var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
            context.Items[NonceKey] = nonce;

            // As the answer starts, and not now: handling an error clears the headers that were
            // set before it, and the error page needs them as much as any other. It is also the
            // last word, over a header of the same name that something on the way has set.
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;

                headers.ContentSecurityPolicy = string.Format(null, policy, nonce);

                // For a browser that does not know frame-ancestors.
                headers.XFrameOptions = "DENY";

                // A file is what its content type says it is, and is never guessed to be a script.
                headers.XContentTypeOptions = "nosniff";

                // The address of a page, which can hold the token of a link from an email, is
                // told to no other site.
                headers["Referrer-Policy"] = "same-origin";

                return Task.CompletedTask;
            });

            return next(context);
        });
    }

    /// <summary>
    /// The value that a script written into a page of this request must carry to be run. It is
    /// new for every request, so text that reaches a page cannot know it.
    /// </summary>
    public static string? NonceOf(HttpContext request) =>
        request.Items.TryGetValue(NonceKey, out var nonce) ? nonce as string : null;

    /// <summary>The policy, with <c>{0}</c> where the nonce of the request goes.</summary>
    private static CompositeFormat ContentSecurityPolicy(IHostEnvironment environment)
    {
        // The connection of an interactive page is to this host. In development the tool that
        // reloads a page as its code changes connects to another port of the machine.
        var connections = environment.IsDevelopment() ? "'self' ws://localhost:* wss://localhost:*" : "'self'";

        return CompositeFormat.Parse(string.Join(
            "; ",
            "default-src 'self'",

            // The application's own files, and the one script that the framework writes into
            // the page, which carries the nonce. No other inline script, and no eval.
            "script-src 'self' 'nonce-{0}'",

            // The application's own files. No style element and no style attribute in a page.
            "style-src 'self'",
            $"connect-src {connections}",
            "object-src 'none'",
            "base-uri 'self'",
            "form-action 'self'",

            // No site may show the application in a frame, this one included.
            "frame-ancestors 'none'"));
    }
}
