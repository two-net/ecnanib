using System.Net;
using Ecnanib.Components;
using Ecnanib.Components.Agents;
using Ecnanib.Kernel;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// FR-UI-03, FR-UI-07, NFR-USA-04 and the CSP: the chart draws as inline SVG with no style attribute or script, its
/// candles, lines and markers each with a title, and Demo prices labelled. It loads on a circuit, which the tests have
/// no browser for, so this renders the view the way the server does, with the framework's HtmlRenderer.
/// </summary>
public sealed class ChartViewTests
{
    [Fact]
    public async Task The_chart_draws_its_candles_lines_and_markers_with_a_title_each()
    {
        var layout = Layout();
        var html = await RenderAsync(layout, TradingEnvironment.Demo, loaded: true, failed: false);

        Assert.Contains($"role=\"img\" aria-label=\"{layout.Summary}\"", html, StringComparison.Ordinal);
        // Each candle's title, and none of the legend's, which has no title.
        Assert.Equal(4, Count(html, ": open "));
        Assert.Contains("class=\"chart-candle up forming\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"chart-candle down\"", html, StringComparison.Ordinal);
        Assert.Contains("<text x=\"99%\" y=\"72.695%\" dy=\"-0.4em\" text-anchor=\"end\">Stop 92</text>", html, StringComparison.Ordinal);
        Assert.Contains(">Target 125</text>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"chart-mark entry-long\" x=\"37.75%\" y=\"45.422%\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"chart-mark exit\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"chart-cycle acted\" x=\"13.25%\" y=\"88%\"", html, StringComparison.Ordinal);
        Assert.Contains(">2026-11-07 02:00 UTC+00:00: Held</title>", html, StringComparison.Ordinal);
        Assert.Contains(">09:00</text>", WebUtility.HtmlDecode(await RenderAsync(Layout(DisplayTime.For("Asia/Tokyo")), TradingEnvironment.Demo, true, false)), StringComparison.Ordinal);

        // The CSP refuses style attributes and inline script, and the chart needs neither.
        Assert.DoesNotContain("style=", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("viewBox=\"0 0", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Demo_prices_are_labelled_and_Live_ones_arent_said_to_be_Demo()
    {
        var demo = await RenderAsync(Layout(), TradingEnvironment.Demo, loaded: true, failed: false);
        var live = await RenderAsync(Layout(), TradingEnvironment.Live, loaded: true, failed: false);

        Assert.Contains("Demo prices resemble Live markets but don't equal them.", demo, StringComparison.Ordinal);
        Assert.Contains(">Demo</span>", demo, StringComparison.Ordinal);
        Assert.DoesNotContain("Demo prices", live, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_shows_a_placeholder_while_loading_and_says_when_there_is_nothing_to_draw()
    {
        var loading = await RenderAsync(null, TradingEnvironment.Demo, loaded: false, failed: false);
        var failed = await RenderAsync(null, TradingEnvironment.Demo, loaded: true, failed: true);
        var empty = await RenderAsync(null, TradingEnvironment.Demo, loaded: true, failed: false);

        Assert.Contains("class=\"skeleton chart-skeleton\" aria-hidden=\"true\"", loading, StringComparison.Ordinal);
        Assert.Contains("Loading the chart", loading, StringComparison.Ordinal);
        Assert.Contains("Binance couldn't give the candles just now.", failed, StringComparison.Ordinal);
        Assert.Contains("There are no candles for this pair yet.", empty, StringComparison.Ordinal);

        // The legend explains every shape, whatever there is to draw.
        foreach (var key in new[] { "Price rose: hollow", "Entry, pointing the trade's way", "Active stop", "Decision executed", "Failed" })
        {
            Assert.Contains(key, loading, StringComparison.Ordinal);
        }
    }

    private static ChartLayout Layout(DisplayTime? time = null)
    {
        ChartLayout? layout = null;
        ChartLayoutTests.InCulture(() => layout = ChartLayout.Of(ChartLayoutTests.Model(), time ?? DisplayTime.Utc));

        return layout!;
    }

    private static async Task<string> RenderAsync(ChartLayout? layout, TradingEnvironment environment, bool loaded, bool failed)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<ChartView>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(ChartView.Layout)] = layout,
                [nameof(ChartView.Loaded)] = loaded,
                [nameof(ChartView.Failed)] = failed,
                [nameof(ChartView.Environment)] = environment,
            }));

            return output.ToHtmlString();
        });

        return WebUtility.HtmlDecode(html);
    }

    private static int Count(string text, string part) => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;
}
