using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging;

/// <summary>The console output is on when the configuration file has its key, and off otherwise (NFR-LOG-2).</summary>
[Collection(ConsoleCollection.Name)]
public class ConsoleOutputTests
{
    [Fact]
    public void An_event_is_written_to_the_console_as_one_line_with_its_properties()
    {
        using var console = new ConsoleCapture();
        using var application = new LoggedApplication(LoggedApplication.Settings().Without("Logging:Outputs:File"));
        var logger = application.Logger("Ecnanib.Tests.Console");

        using (logger.BeginScope(new Dictionary<string, object> { ["Batch"] = 7 }))
        {
            logger.LogWarning("Order {OrderId} was filled", 42);
        }

        var line = Assert.Single(console.Text.Split('\n'), text => text.Contains("Ecnanib.Tests.Console"));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}Z \[WRN\] Ecnanib\.Tests\.Console: Order 42 was filled ", line);
        Assert.Contains("\"Batch\": 7", line);
    }

    [Fact]
    public void Without_the_console_output_nothing_is_written_to_the_console()
    {
        using var console = new ConsoleCapture();
        using var application = new LoggedApplication(LoggedApplication.Settings().Without("Logging:Outputs:Console"));

        application.Logger().LogWarning("Written {Marker}", "to the file only");

        Assert.DoesNotContain("to the file only", console.Text);
        Assert.Contains(application.Events(), written => written.Text("Marker") == "to the file only");
    }
}
