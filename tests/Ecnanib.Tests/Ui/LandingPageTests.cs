using System.Net;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Ui;

/// <summary>FR-UI-01, EXT-UI-03, NFR-LEG-02. These read the prerendered HTML, which is the first thing a visitor sees.</summary>
public sealed class LandingPageTests(SharedApp app) : IClassFixture<SharedApp>
{
    [Theory]
    [InlineData("Risk disclosure")]
    [InlineData("Automated cryptocurrency trading can lose money")]
    [InlineData("not financial advice")]
    [InlineData("not affiliated with or endorsed by Binance")]
    public async Task The_mandatory_notices_are_on_the_page(string notice) =>
        Assert.Contains(notice, await MainContentAsync(), StringComparison.Ordinal);

    /// <summary>A notice below the sales copy would be easy to miss.</summary>
    [Fact]
    public async Task The_notices_come_before_the_decision_sequence()
    {
        var content = await MainContentAsync();

        Assert.True(
            content.IndexOf("id=\"notices\"", StringComparison.Ordinal) < content.IndexOf("id=\"how-it-works\"", StringComparison.Ordinal));
    }

    /// <summary>The example is the page's main image of a trade, so it mustn't read as a real one (FR-UI-07).</summary>
    [Theory]
    [InlineData("An example, not a real trade.")]
    [InlineData("Demo")]
    public async Task The_decision_record_is_labelled_as_an_example(string label) =>
        Assert.Contains(label, await MainContentAsync(), StringComparison.Ordinal);

    [Fact]
    public async Task Links_on_the_page_have_a_target()
    {
        var page = await PageAsync();

        Assert.Contains("id=\"how-it-works\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"notices\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_has_a_title_and_description()
    {
        var page = await PageAsync();

        Assert.Contains("<title>Ecnanib – AI proposes, rules decide</title>", page, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"description\" content=\"Ecnanib runs", page, StringComparison.Ordinal);
    }

    private async Task<string> MainContentAsync()
    {
        var page = await PageAsync();
        var start = page.IndexOf("<main", StringComparison.Ordinal);

        return page[start..page.IndexOf("</main>", StringComparison.Ordinal)];
    }

    private async Task<string> PageAsync()
    {
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Razor encodes anything outside ASCII, and apostrophes too.
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
