using System.Net;
using System.Security.Cryptography;
using Ecnanib.Tests.Database.Live;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Configuration;

/// <summary>
/// NFR-SEC-01/03 end to end: the application's pipeline on the in-memory server against a real PostgreSQL database. The
/// session cookie as the browser receives it, a planted cookie replaced at sign-in (fixation), sign-out ending the session
/// on the server, and one user's view of another user's agent.
/// </summary>
[Trait("Category", "Live")]
public class PipelineLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public PipelineLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SigningUpThroughTheFormIssuesAHardenedPersistentSessionCookie()
    {
        await StartAsync();
        await using var host = await PipelineTestHost.StartAsync(connectionString: _fixture.ConnectionString);

        var (html, antiforgery) = await PipelineSession.PageAsync(host, "/sign-up");
        var response = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Post, "/sign-up", cookies: [antiforgery],
            content: PipelineTestHost.Form(("_handler", "sign-up"), ("__RequestVerificationToken", PipelineTestHost.AntiforgeryToken(html)),
                ("Input.Email", "pipeline-signup@example.com"), ("Input.Password", PipelineSession.Password), ("Input.ConfirmPassword", PipelineSession.Password))), Ct);

        Assert.True(response.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther, response.StatusCode.ToString());
        Assert.Equal("/", new Uri(host.Client.BaseAddress!, response.Headers.Location!).AbsolutePath);
        var session = PipelineTestHost.SetCookie(response, "ecnanib.session");
        Assert.NotNull(session);
        Assert.Contains("httponly", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", session, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("; secure", session, StringComparison.OrdinalIgnoreCase);

        var dashboard = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/agents", cookies: [PipelineTestHost.CookiePair(response, "ecnanib.session")!]), Ct);
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        await DeleteUsersAsync("pipeline-signup@example.com");
    }

    /// <summary>NFR-SEC-01: a cookie planted before authentication never becomes a session; sign-in replaces it, and sign-out ends the session on the server, so a copy of the ticket stops working.</summary>
    [Fact]
    public async Task SigningInReplacesAPlantedCookieAndSigningOutEndsTheSessionOnTheServer()
    {
        await StartAsync();
        await using var host = await PipelineTestHost.StartAsync(connectionString: _fixture.ConnectionString);
        await PipelineSession.RegisterAsync(host, "pipeline-fixation@example.com");
        var planted = "ecnanib.session=" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(96)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var (html, antiforgery) = await PipelineSession.PageAsync(host, "/sign-in", planted);
        Assert.Contains("_handler", PipelineTestHost.Inputs(html).Keys);
        var signedIn = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Post, "/sign-in", cookies: [planted, antiforgery],
            content: PipelineTestHost.Form(("_handler", "sign-in"), ("__RequestVerificationToken", PipelineTestHost.AntiforgeryToken(html)), ("Input.Email", "pipeline-fixation@example.com"), ("Input.Password", PipelineSession.Password))), Ct);

        Assert.True(signedIn.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther, signedIn.StatusCode.ToString());
        var fresh = PipelineTestHost.CookiePair(signedIn, "ecnanib.session");
        Assert.NotNull(fresh);
        Assert.NotEqual(planted, fresh);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/agents", cookies: [fresh]), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Found, (await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/agents", cookies: [planted]), Ct)).StatusCode);

        // The layout's sign-out form carries a token bound to the signed-in identity.
        var dashboard = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/agents", cookies: [fresh, antiforgery]), Ct);
        var token = PipelineTestHost.AntiforgeryToken(await dashboard.Content.ReadAsStringAsync(Ct));
        var signedOut = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Post, "/sign-out", cookies: [fresh, antiforgery],
            content: PipelineTestHost.Form(("__RequestVerificationToken", token))), Ct);

        Assert.Equal(HttpStatusCode.Found, signedOut.StatusCode);
        Assert.Equal("/", new Uri(host.Client.BaseAddress!, signedOut.Headers.Location!).AbsolutePath);
        Assert.Contains("expires=Thu, 01 Jan 1970", PipelineTestHost.SetCookie(signedOut, "ecnanib.session"), StringComparison.Ordinal);
        // A copy of the old ticket is rejected: the stamp was rotated and the ticket revoked.
        var replay = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/agents", cookies: [fresh]), Ct);
        Assert.Equal(HttpStatusCode.Found, replay.StatusCode);
        Assert.StartsWith($"http://{PipelineTestHost.Host}/sign-in", replay.Headers.Location?.ToString());
        await DeleteUsersAsync("pipeline-fixation@example.com");
    }

    /// <summary>NFR-SEC-03: another user's agent id is a page that does not exist; the status page names no user's data.</summary>
    [Fact]
    public async Task AnotherUsersAgentIsNotFoundAndTheStatusPageNamesNoUserData()
    {
        await StartAsync();
        await using var host = await PipelineTestHost.StartAsync(connectionString: _fixture.ConnectionString);
        var owner = await PipelineSession.RegisterAsync(host, "pipeline-owner@example.com");
        await PipelineSession.RegisterAsync(host, "pipeline-intruder@example.com");
        Guid agentId;
        await using (var db = _fixture.CreateContext())
        {
            var agent = LiveSeed.NewAgent(await db.Users.SingleAsync(u => u.Id == owner.Id, Ct), "Owner's BTC");
            db.Add(agent);
            await db.SaveChangesAsync(Ct);
            agentId = agent.Id;
        }

        var ownerSession = await PipelineSession.SignInAsync(host, "pipeline-owner@example.com");
        var intruderSession = await PipelineSession.SignInAsync(host, "pipeline-intruder@example.com");

        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, $"/agents/{agentId}", cookies: [ownerSession]), Ct)).StatusCode);
        foreach (var path in new[] { $"/agents/{agentId}", $"/agents/{agentId}/log", $"/agents/{agentId}/setup" })
            Assert.Equal(HttpStatusCode.NotFound, (await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, path, cookies: [intruderSession]), Ct)).StatusCode);

        var status = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, "/status", cookies: [intruderSession]), Ct);
        var statusHtml = await status.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Contains("plain http, allowed for this machine only", statusHtml, StringComparison.Ordinal);
        Assert.Contains("http from 127.0.0.1", statusHtml, StringComparison.Ordinal);
        Assert.Contains("loopback only", statusHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(agentId.ToString(), statusHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BTCUSDT", statusHtml, StringComparison.OrdinalIgnoreCase);
        await DeleteUsersAsync("pipeline-owner@example.com", "pipeline-intruder@example.com");
    }

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }

    private async Task DeleteUsersAsync(params string[] emails)
    {
        await using var db = _fixture.CreateContext();
        var normalized = emails.Select(e => e.ToUpperInvariant()).ToList();
        await db.Users.Where(u => normalized.Contains(u.NormalizedEmail)).ExecuteDeleteAsync(Ct);
    }
}
