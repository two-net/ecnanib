using System.Net;
using System.Net.WebSockets;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Security;

/// <summary>
/// NFR-SEC-03. These run on Kestrel, because a test host presets the WebSocket feature and the
/// middleware under test then skips its check, so a test there would pass with no check at all.
/// http is allowed in Development, and it keeps the redirect out of the way.
/// </summary>
public sealed class WebSocketTests(PostgresFixture postgres)
{
    private const string PublicOrigin = "http://ecnanib.test";

    [Theory]
    [InlineData(PublicOrigin, HttpStatusCode.SwitchingProtocols)]
    [InlineData("https://evil.test", HttpStatusCode.Forbidden)]
    [InlineData("http://ecnanib.test.evil.test", HttpStatusCode.Forbidden)]
    public async Task Only_the_public_origin_can_open_a_websocket(string origin, HttpStatusCode expected)
    {
        await using var app = KestrelApp();

        Assert.Equal(expected, await ConnectAsync(app, origin));
    }

    /// <summary>SignalR accepts a WebSocket that skips negotiation, so counting only negotiation would leave this open.</summary>
    [Fact]
    public async Task A_websocket_that_skips_negotiation_is_still_limited()
    {
        await using var app = KestrelApp(new() { ["Security:RateLimits:RequestsPerMinute"] = "1" });

        Assert.Equal(HttpStatusCode.SwitchingProtocols, await ConnectAsync(app, PublicOrigin));
        Assert.Equal(HttpStatusCode.TooManyRequests, await ConnectAsync(app, PublicOrigin));
    }

    private static async Task<HttpStatusCode> ConnectAsync(AppFactory app, string origin)
    {
        var address = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses));

        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Origin", origin);

        try
        {
            await socket.ConnectAsync(new Uri($"ws://{address.Authority}/_blazor"), TestContext.Current.CancellationToken);
        }
        catch (WebSocketException)
        {
            // A refused upgrade throws. The status is what's being tested.
        }

        return socket.HttpStatusCode;
    }

    private AppFactory KestrelApp(Dictionary<string, string?>? settings = null)
    {
        var app = new AppFactory(
            postgres.NewConnectionString(),
            settings: new Dictionary<string, string?>(settings ?? []) { ["App:PublicBaseUrl"] = PublicOrigin });

        app.UseKestrel(0);
        app.StartServer();

        return app;
    }
}
