using Microsoft.Playwright;

namespace Ecnanib.Tests.Accessibility;

/// <summary>
/// The browser that a run was told to drive, for the check of every screen in one
/// (NFR-USA-1): Chromium, started by Playwright, without a window. A run that was not told to
/// starts none, and its tests are skipped, as the tests that call a provider for real are:
/// the suite reaches no network and needs no browser by itself. See docs/accessibility.md.
/// </summary>
internal sealed class DrivenBrowser : IAsyncDisposable
{
    /// <summary>The variable that tells a run to drive a browser, and which one: <c>chromium</c>.</summary>
    public const string Variable = "ECNANIB_BROWSER";

    /// <summary>The variable that lets a run fetch the browser that it drives, once, on a machine that has none.</summary>
    public const string InstallVariable = "ECNANIB_BROWSER_INSTALL";

    private const string Chromium = "chromium";

    /// <summary>The width and the height of a desk, which a page is opened at unless a test says otherwise.</summary>
    public static readonly ViewportSize Desk = new() { Width = 1280, Height = 800 };

    /// <summary>The width of a phone, in the pixels of a stylesheet: the narrowest that a page must fit (WCAG 2.1, 1.4.10).</summary>
    public static readonly ViewportSize Phone = new() { Width = 320, Height = 640 };

    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;

    private DrivenBrowser(IPlaywright playwright, IBrowser browser)
    {
        _playwright = playwright;
        _browser = browser;
    }

    /// <summary>
    /// The browser that the run was told to drive. Without it, the test is skipped: the suite
    /// starts no browser by itself.
    /// </summary>
    public static void IsAskedFor()
    {
        var browser = Environment.GetEnvironmentVariable(Variable);

        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(browser),
            $"Set {Variable}={Chromium} to check every screen in a browser, with axe and a walk with the keyboard. See docs/accessibility.md.");

        Assert.True(
            string.Equals(browser!.Trim(), Chromium, StringComparison.OrdinalIgnoreCase),
            $"{Variable} is \"{browser}\", and the checks drive {Chromium} alone.");
    }

    /// <summary>Starts the browser, and fetches it first where the run allows that.</summary>
    public static async Task<DrivenBrowser> StartAsync()
    {
        IsAskedFor();

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(InstallVariable)))
        {
            // The one moment that a run of the tests reaches the network, and only when told to.
            var fetched = Microsoft.Playwright.Program.Main(["install", Chromium]);

            Assert.True(fetched == 0, $"The browser could not be fetched: the installer of Playwright answered {fetched}.");
        }

        var playwright = await Playwright.CreateAsync();

        try
        {
            return new DrivenBrowser(playwright, await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }));
        }
        catch (PlaywrightException error)
        {
            playwright.Dispose();

            throw new InvalidOperationException(
                $"The browser could not be started. A machine that has none of the version that the tests ask for gets it with {InstallVariable}=1 beside {Variable}, once. {error.Message}",
                error);
        }
    }

    /// <summary>
    /// A browser of its own for one person, with its own cookies, at an application that
    /// listens. It takes the certificate that the application made for the run, has what moves
    /// reduced, so what a check measures stands still, and is as wide as a desk unless told
    /// otherwise. Nothing of the content security policy is set aside for it. Its device is in
    /// UTC unless told otherwise: the script of the times says the time zone of the device as
    /// its person signs in (NFR-USA-3), and a run reads the same on every machine.
    /// </summary>
    /// <param name="address">Where the application listens.</param>
    /// <param name="size">The width and the height of its window.</param>
    /// <param name="dark">Whether the device is set to dark, which a page that says nothing of its theme follows.</param>
    /// <param name="timeZone">The time zone of the device, as the time zone database names it.</param>
    public async Task<IBrowserContext> OpenAsync(Uri address, ViewportSize? size = null, bool dark = false, string timeZone = "UTC") =>
        await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = address.ToString(),
            IgnoreHTTPSErrors = true,
            ReducedMotion = ReducedMotion.Reduce,
            ColorScheme = dark ? ColorScheme.Dark : ColorScheme.Light,
            ViewportSize = size ?? Desk,
            TimezoneId = timeZone,
        });

    public async ValueTask DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }
}
