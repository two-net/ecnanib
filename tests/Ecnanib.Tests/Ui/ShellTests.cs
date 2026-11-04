using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Components;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Ui;

/// <summary>
/// CON-14 and the layout every page plugs into. The error page is here because it renders in a scope of
/// its own, and with no circuit. Layout at each width needs a browser: see docs/architecture.md.
/// </summary>
public sealed class ShellTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/", HttpStatusCode.OK)]
    [InlineData("/nothing-here", HttpStatusCode.NotFound)]
    public async Task A_page_has_the_shell(string path, HttpStatusCode status)
    {
        await using var app = new AppFactory(postgres.NewConnectionString());
        using var client = app.CreateClient();

        using var response = await client.GetAsync(path, Cancellation);

        Assert.Equal(status, response.StatusCode);
        await AssertShellAsync(response);
    }

    [Fact]
    public async Task The_error_page_has_the_shell()
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            services => services.AddTransient<IStartupFilter>(
                _ => new TerminalMiddleware(_ => throw new InvalidOperationException("Failing on purpose."))),
            environment: Environments.Production);
        using var client = app.CreatePublicClient();

        using var response = await client.GetAsync("/nothing-here", Cancellation);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertShellAsync(response);
    }

    /// <summary>The script writes the cookie DisplayTime reads, and nothing else ties the two names together.</summary>
    [Fact]
    public async Task The_shell_script_writes_the_browser_time_zone_cookie()
    {
        await using var app = new AppFactory(postgres.NewConnectionString());
        using var client = app.CreateClient();

        var page = await client.GetStringAsync("/", Cancellation);
        var script = Regex.Match(page, """<script type="module" src="(?<src>[^"]*shell[^"]*\.js)">""").Groups["src"].Value;

        Assert.Contains($"{DisplayTime.BrowserTimeZoneCookie}=", await client.GetStringAsync(script, Cancellation), StringComparison.Ordinal);
    }

    private static async Task AssertShellAsync(HttpResponseMessage response)
    {
        var page = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Contains("href=\"#main\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"main\"", page, StringComparison.Ordinal);
        Assert.Contains("<nav", page, StringComparison.Ordinal);

        // Before sign-in, and on the error page whoever asks (FR-PREF-03).
        Assert.Contains("<html lang=\"en\" data-theme=\"system\"", page, StringComparison.Ordinal);

        // These only work with the script, on a page that may never have a circuit.
        Assert.Matches("""<script type="module" src="[^"]*shell[^"]*\.js">""", page);

        var footer = page[page.IndexOf("<footer", StringComparison.Ordinal)..];

        Assert.Contains("not financial advice", footer, StringComparison.Ordinal);
        Assert.Contains("not affiliated with or endorsed by Binance", footer, StringComparison.Ordinal);
    }
}
