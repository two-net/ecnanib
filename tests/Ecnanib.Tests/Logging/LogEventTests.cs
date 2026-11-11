using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging;

/// <summary>
/// Log events are structured: written from a message template with named properties, with a level,
/// enriched by the context they are written in (NFR-LOG-1).
/// </summary>
public class LogEventTests
{
    [Fact]
    public void An_event_keeps_its_template_its_named_properties_its_level_and_its_area()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());

        application.Logger("Ecnanib.Tests.Orders")
            .LogWarning("Order {OrderId} for {Symbol} was filled at {Price}", 42, "BTCUSDT", 64250.5m);

        var written = Assert.Single(application.Events(), e => e.Area == "Ecnanib.Tests.Orders");
        Assert.Equal("Order {OrderId} for {Symbol} was filled at {Price}", written.Template);
        Assert.Equal("Warning", written.Level);
        Assert.Equal(42, written["OrderId"].GetInt32());
        Assert.Equal("BTCUSDT", written["Symbol"].GetString());
        Assert.Equal(64250.5m, written["Price"].GetDecimal());

        // The time is written in UTC.
        var time = written["@t"].GetString()!;
        Assert.EndsWith("Z", time);
        Assert.InRange(
            DateTimeOffset.UtcNow - DateTimeOffset.Parse(time, CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(-5),
            TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void An_object_is_logged_with_its_members_when_the_template_asks_for_it()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());

        application.Logger().LogInformation("Placed {@Order}", new { Symbol = "ETHUSDT", Quantity = 3 });

        var order = Assert.Single(application.Events(), e => e.Has("Order"))["Order"];
        Assert.Equal(JsonValueKind.Object, order.ValueKind);
        Assert.Equal("ETHUSDT", order.GetProperty("Symbol").GetString());
        Assert.Equal(3, order.GetProperty("Quantity").GetInt32());
    }

    [Fact]
    public void An_event_is_enriched_with_the_properties_of_the_scopes_it_is_written_in()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());
        var logger = application.Logger();

        using (logger.BeginScope(new Dictionary<string, object> { ["Batch"] = 7 }))
        using (logger.BeginScope(new Dictionary<string, object> { ["Step"] = "sync" }))
        {
            // Another logger, as a component deeper in the call would have.
            application.Logger("Ecnanib.Tests.Deeper").LogInformation("Inside {Marker}", "the scopes");
        }

        logger.LogInformation("Outside {Marker}", "the scopes");

        var inside = Assert.Single(application.Events(), e => e.Template == "Inside {Marker}");
        Assert.Equal(7, inside["Batch"].GetInt32());
        Assert.Equal("sync", inside["Step"].GetString());

        var outside = Assert.Single(application.Events(), e => e.Template == "Outside {Marker}");
        Assert.False(outside.Has("Batch"));
        Assert.False(outside.Has("Step"));
    }
}
