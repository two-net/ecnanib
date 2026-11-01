using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging;

/// <summary>
/// Some framework events can't be scanned, so configuration can't turn them on. These are the
/// places where a security rule outranks FR-LOG-03, on purpose.
/// </summary>
public sealed class LogFloorTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("Microsoft.AspNetCore.SignalR", LogLevel.Debug, LogLevel.Information)]
    [InlineData("Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher", LogLevel.Debug, LogLevel.Information)]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information, LogLevel.Warning)]
    [InlineData("Microsoft.AspNetCore.Routing.Matching.DfaMatcher", LogLevel.Debug, LogLevel.Information)]
    [InlineData("Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionMiddleware", LogLevel.Debug, LogLevel.Information)]
    [InlineData("Microsoft.AspNetCore.Components.Routing.Router", LogLevel.Debug, LogLevel.Information)]
    [InlineData("Microsoft.AspNetCore.Components.Server.Circuits.RemoteJSRuntime", LogLevel.Debug, LogLevel.Information)]
    [InlineData("Microsoft.AspNetCore.StaticAssets.StaticAssetsInvoker", LogLevel.Debug, LogLevel.Information)]
    [InlineData("Microsoft.AspNetCore.StaticFiles.StaticFileMiddleware", LogLevel.Debug, LogLevel.Information)]
    public async Task Configuration_cant_lower_a_category_below_its_floor(string category, LogLevel below, LogLevel at)
    {
        await using var app = new LoggedApp(
            postgres.NewConnectionString(),
            new()
            {
                ["Logging:MinimumLevel"] = "Verbose",
                [$"Logging:Overrides:{category}"] = "Verbose",
            });

        var logger = app.Logger(category);
        logger.Log(below, "markerBelowFloor");
        logger.Log(at, "markerAtFloor");
        await app.StopAsync();

        var text = app.FileText();

        Assert.DoesNotContain("markerBelowFloor", text, StringComparison.Ordinal);
        Assert.Contains("markerAtFloor", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_floor_doesnt_reach_a_category_that_only_shares_its_prefix()
    {
        await using var app = new LoggedApp(postgres.NewConnectionString(), new() { ["Logging:MinimumLevel"] = "Verbose" });

        app.Logger("Microsoft.AspNetCore.SignalRish").LogDebug("markerNeighbour");
        await app.StopAsync();

        Assert.Contains("markerNeighbour", app.FileText(), StringComparison.Ordinal);
    }
}
