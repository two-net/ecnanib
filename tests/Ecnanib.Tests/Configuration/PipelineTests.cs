using System.Net;
using Ecnanib.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Ecnanib.Tests.Configuration;

/// <summary>
/// NFR-SEC-01/02 at the HTTP level, on the application's own pipeline: the trusted proxy's forwarded headers, https
/// redirection and HSTS behind an https public URL, the security headers, antiforgery on every POST and the challenge of
/// anonymous requests. Offline: no page here reaches the database.
/// </summary>
public class PipelineTests
{
    private static readonly Dictionary<string, string?> HttpsOrigin = new() { ["Server:PublicUrl"] = $"https://{PipelineTestHost.Host}" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryResponseCarriesTheSecurityHeadersOnce()
    {
        await using var host = await PipelineTestHost.StartAsync();

        var landing = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/"), Ct);
        var missing = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/no-such-page"), Ct);

        Assert.Equal(HttpStatusCode.OK, landing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        foreach (var response in new[] { landing, missing })
        {
            Assert.Equal("nosniff", PipelineTestHost.Header(response, "X-Content-Type-Options"));
            Assert.Equal("DENY", PipelineTestHost.Header(response, "X-Frame-Options"));
            Assert.Equal(EcnanibPipeline.ContentSecurityPolicy, PipelineTestHost.Header(response, "Content-Security-Policy"));
            Assert.Equal(EcnanibPipeline.ReferrerPolicy, PipelineTestHost.Header(response, "Referrer-Policy"));
            Assert.Equal(EcnanibPipeline.PermissionsPolicy, PipelineTestHost.Header(response, "Permissions-Policy"));
            Assert.Null(PipelineTestHost.Header(response, "Strict-Transport-Security"));
        }
    }

    /// <summary>NFR-SEC-02 behind the proxy: a request the proxy received over plain http is sent to the https origin, a direct plain hit too; one the proxy received over https gets HSTS.</summary>
    [Fact]
    public async Task WithAnHttpsPublicUrlPlainRequestsAreRedirectedAndAForwardedHttpsRequestGetsHsts()
    {
        await using var host = await PipelineTestHost.StartAsync(HttpsOrigin);

        var plain = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/sign-in?ReturnUrl=%2Fagents", forwardedProto: "http"), Ct);
        var secure = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/sign-in", forwardedProto: "https"), Ct);
        var direct = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/sign-in"), Ct);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, plain.StatusCode);
        Assert.Equal($"https://{PipelineTestHost.Host}/sign-in?ReturnUrl=%2Fagents", plain.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.OK, secure.StatusCode);
        Assert.Equal($"max-age={(int)ConfigurationServiceCollectionExtensions.HstsMaxAge.TotalSeconds}", PipelineTestHost.Header(secure, "Strict-Transport-Security"));
        // A direct hit on Kestrel belongs on the public origin too.
        Assert.Equal(HttpStatusCode.TemporaryRedirect, direct.StatusCode);
        Assert.Equal($"https://{PipelineTestHost.Host}/sign-in", direct.Headers.Location?.ToString());
    }

    /// <summary>The redirect is built from Server:PublicUrl, port included, not from a Host header the proxy may have rewritten to the upstream address.</summary>
    [Fact]
    public async Task TheRedirectGoesToThePublicOriginWhateverTheHostHeaderSays()
    {
        await using var host = await PipelineTestHost.StartAsync(new() { ["Server:PublicUrl"] = $"https://{PipelineTestHost.Host}:8443" });

        var plain = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/agents?tab=open", forwardedProto: "http", hostHeader: "127.0.0.1:5207"), Ct);
        var posted = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Post, "/sign-out", forwardedProto: "http", content: PipelineTestHost.Form()), Ct);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, plain.StatusCode);
        Assert.Equal($"https://{PipelineTestHost.Host}:8443/agents?tab=open", plain.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.TemporaryRedirect, posted.StatusCode);
        Assert.Equal($"https://{PipelineTestHost.Host}:8443/sign-out", posted.Headers.Location?.ToString());
    }

    /// <summary>Forwarded headers are believed from loopback and Server:TrustedProxies only; anyone else is a client claiming to be a proxy.</summary>
    [Fact]
    public async Task ForwardedHeadersAreBelievedFromTrustedProxiesOnly()
    {
        var config = new Dictionary<string, string?>(HttpsOrigin) { ["Server:TrustedProxies:0"] = "203.0.113.0/24" };
        await using var host = await PipelineTestHost.StartAsync(config, map: app => app.MapGet("/__test/connection", (HttpContext http) => Results.Text($"{http.Connection.RemoteIpAddress}|{http.Request.Scheme}")));

        var fromLoopback = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/__test/connection", "127.0.0.1", "https", "198.51.100.7"), Ct);
        var fromTrusted = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/__test/connection", "203.0.113.9", "https", "198.51.100.7"), Ct);
        var fromStranger = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/__test/connection", "192.0.2.5", "https", "198.51.100.7"), Ct);
        var plainViaTrusted = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/__test/connection", "203.0.113.9", "http", "198.51.100.7"), Ct);
        var schemeOnly = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/__test/connection", "203.0.113.9", "https"), Ct);

        Assert.Equal("198.51.100.7|https", await fromLoopback.Content.ReadAsStringAsync(Ct));
        Assert.Equal("198.51.100.7|https", await fromTrusted.Content.ReadAsStringAsync(Ct));
        // The stranger's claim is dropped: the connection is plain http from 192.0.2.5 and is redirected to the https origin.
        Assert.Equal(HttpStatusCode.TemporaryRedirect, fromStranger.StatusCode);
        // A trusted proxy that received plain http: redirected to the public origin before any endpoint runs.
        Assert.Equal(HttpStatusCode.TemporaryRedirect, plainViaTrusted.StatusCode);
        // A proxy that forwards the scheme but not the address is still believed.
        Assert.Equal("203.0.113.9|https", await schemeOnly.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task WithALoopbackHttpPublicUrlNothingIsRedirectedAndNoHstsIsSent()
    {
        await using var host = await PipelineTestHost.StartAsync();

        var plain = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/sign-in"), Ct);
        var forwarded = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/sign-in", forwardedProto: "https"), Ct);

        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal(HttpStatusCode.OK, forwarded.StatusCode);
        Assert.Null(PipelineTestHost.Header(forwarded, "Strict-Transport-Security"));
    }

    /// <summary>NFR-SEC-01: every POST needs the antiforgery token; with it, the sign-in handler runs.</summary>
    [Fact]
    public async Task APostWithoutTheAntiforgeryTokenIsRefusedAndOneWithItReachesTheHandler()
    {
        await using var host = await PipelineTestHost.StartAsync();

        var signOut = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Post, "/sign-out", content: PipelineTestHost.Form()), Ct);
        var signIn = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Post, "/sign-in", content: PipelineTestHost.Form(("_handler", "sign-in"), ("Input.Email", "nope"), ("Input.Password", "whatever"))), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, signOut.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, signIn.StatusCode);

        var page = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/sign-in"), Ct);
        var html = await page.Content.ReadAsStringAsync(Ct);
        var inputs = PipelineTestHost.Inputs(html);
        var email = inputs.Values.Single(i => i.TryGetValue("type", out var type) && type == "email")["name"];
        var password = inputs.Values.Single(i => i.TryGetValue("type", out var type) && type == "password")["name"];
        var cookie = PipelineTestHost.CookiePair(page, "ecnanib.antiforgery");
        Assert.NotNull(cookie);
        Assert.Equal("sign-in", inputs["_handler"]["value"]);

        // A malformed email never reaches the database: the handler answers with the credentials message.
        var attempt = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Post, "/sign-in", cookies: [cookie],
            content: PipelineTestHost.Form(("_handler", "sign-in"), ("__RequestVerificationToken", PipelineTestHost.AntiforgeryToken(html)), (email, "nope"), (password, "whatever"))), Ct);

        Assert.Equal(HttpStatusCode.OK, attempt.StatusCode);
        Assert.Contains("incorrect", await attempt.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
        Assert.Null(PipelineTestHost.SetCookie(attempt, "ecnanib.session"));
    }

    [Fact]
    public async Task ATokenFromAnotherBrowserIsRefused()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var page = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/sign-in"), Ct);
        var html = await page.Content.ReadAsStringAsync(Ct);

        // The field token without its cookie: what a cross-site form could carry at most.
        var attempt = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Post, "/sign-in",
            content: PipelineTestHost.Form(("_handler", "sign-in"), ("__RequestVerificationToken", PipelineTestHost.AntiforgeryToken(html)), ("Input.Email", "nope"), ("Input.Password", "whatever"))), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, attempt.StatusCode);
    }

    /// <summary>NFR-SEC-03: a visitor is sent to sign in, with the page they asked for to come back to.</summary>
    [Theory]
    [InlineData("/agents")]
    [InlineData("/keys")]
    [InlineData("/status")]
    [InlineData("/providers")]
    [InlineData("/settings")]
    public async Task AnonymousRequestsToProtectedPagesAreChallenged(string path)
    {
        await using var host = await PipelineTestHost.StartAsync();

        var response = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, path), Ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal($"http://{PipelineTestHost.Host}/sign-in?ReturnUrl={Uri.EscapeDataString(path)}", response.Headers.Location?.ToString());
    }

    /// <summary>NFR-SEC-01: no session exists before authentication, so there is nothing a fixation attempt could plant; the antiforgery cookie is Secure iff the origin is https.</summary>
    [Fact]
    public async Task TheSignInPageSetsNoSessionAndTheAntiforgeryCookieIsSecureBehindHttpsOnly()
    {
        await using var plainHost = await PipelineTestHost.StartAsync();
        await using var httpsHost = await PipelineTestHost.StartAsync(HttpsOrigin);

        var plain = await plainHost.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/sign-in"), Ct);
        var secure = await httpsHost.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/sign-in", forwardedProto: "https"), Ct);

        foreach (var response in new[] { plain, secure })
        {
            Assert.Null(PipelineTestHost.SetCookie(response, "ecnanib.session"));
            var antiforgery = PipelineTestHost.SetCookie(response, "ecnanib.antiforgery");
            Assert.NotNull(antiforgery);
            Assert.Contains("httponly", antiforgery, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", antiforgery, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("secure", PipelineTestHost.SetCookie(plain, "ecnanib.antiforgery")!.Replace("SameSite", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; secure", PipelineTestHost.SetCookie(secure, "ecnanib.antiforgery"), StringComparison.OrdinalIgnoreCase);
    }
}
