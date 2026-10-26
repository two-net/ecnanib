using System.Net;
using Ecnanib.Tests.Configuration;
using Ecnanib.Tests.Support;

namespace Ecnanib.Tests.Ui;

/// <summary>
/// NFR-USE-01 on the pages a visitor can reach without a database, rendered by the application's own pipeline: the structural
/// checks of <see cref="AccessibilityChecks"/>, the English document language (NFR-USE-03) and a specific title per page.
/// The layout, the hero, the sign-in and sign-up forms, the error and not-found pages and the reconnection dialog are all
/// here; <c>AccessibilityLiveTests</c> tours the signed-in pages against PostgreSQL.
/// </summary>
public class AccessibilityTests
{
    /// <summary>Every anonymous route with the status and title it must answer.</summary>
    public static readonly (string Path, HttpStatusCode Status, string Title)[] Pages =
    [
        ("/", HttpStatusCode.OK, "Ecnanib"),
        ("/?notice=account-deleted", HttpStatusCode.OK, "Ecnanib"),
        ("/sign-in", HttpStatusCode.OK, "Sign in"),
        ("/sign-in?ReturnUrl=%2Fagents", HttpStatusCode.OK, "Sign in"),
        ("/sign-up", HttpStatusCode.OK, "Sign up"),
        ("/no-such-page", HttpStatusCode.NotFound, "Not found"),
        ("/not-found", HttpStatusCode.OK, "Not found"),
        ("/Error", HttpStatusCode.OK, "Error"),
    ];

    [Fact]
    public async Task EveryAnonymousPageIsStructurallyAccessibleEnglishAndTitled()
    {
        await using var host = await PipelineTestHost.StartAsync();

        var problems = await PageTour.RunAsync(host, Pages.Select(p => new PageExpectation(p.Path, p.Status, title => title == p.Title, p.Title)), session: null);

        Assert.Empty(problems);
    }

    /// <summary>The shared chrome every page carries: the skip link to the main landmark, the named navigation, the alert bar and the named dialog.</summary>
    [Fact]
    public async Task TheLayoutCarriesTheLandmarksTheSkipLinkTheAlertBarAndTheNamedDialog()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var (_, html) = await PipelineSession.GetAsync(host, "/");
        var document = AccessibilityChecks.Parse(html);

        Assert.Equal("en", document.DocumentElement.GetAttribute("lang"));
        Assert.Equal("#main", document.QuerySelector("a.skip-link")?.GetAttribute("href"));
        Assert.NotNull(document.QuerySelector("main#main"));
        Assert.Equal("Primary", document.QuerySelector("nav.primary")?.GetAttribute("aria-label"));
        Assert.Equal("alert", document.GetElementById("blazor-error-ui")?.GetAttribute("role"));
        Assert.Equal("Dismiss", document.QuerySelector("#blazor-error-ui button.dismiss")?.GetAttribute("aria-label"));
        var dialog = document.GetElementById("components-reconnect-modal");
        Assert.NotNull(dialog);
        Assert.Equal("alertdialog", dialog.GetAttribute("role"));
        Assert.Equal("Connection to the server", document.GetElementById(dialog.GetAttribute("aria-labelledby")!)?.TextContent.Trim());
        Assert.Equal("polite", document.GetElementById(dialog.GetAttribute("aria-describedby")!)?.GetAttribute("aria-live"));
        // The hero's pause control follows the scene in the DOM, as it does on screen (1.3.2).
        var figure = document.QuerySelector("figure.hero-figure");
        Assert.NotNull(figure);
        Assert.Equal(["svg", "figcaption", "input", "label"], figure.Children.Select(c => c.LocalName));
    }
}

/// <summary>What a toured page must answer: its status, a title predicate (and how to describe it when it fails).</summary>
internal sealed record PageExpectation(string Path, HttpStatusCode Status, Func<string, bool> Title, string TitleDescription);

/// <summary>Fetches pages and gathers every problem at once (status, language, title, structural violations), so one run reports every page's gaps.</summary>
internal static class PageTour
{
    public static async Task<IReadOnlyList<string>> RunAsync(PipelineTestHost host, IEnumerable<PageExpectation> pages, string? session)
    {
        var problems = new List<string>();
        foreach (var page in pages)
        {
            var (response, html) = await PipelineSession.GetAsync(host, page.Path, session);
            if (response.StatusCode != page.Status)
            {
                problems.Add($"{page.Path}: answered {(int)response.StatusCode} {response.StatusCode}, expected {(int)page.Status}");
                continue;
            }

            var document = AccessibilityChecks.Parse(html);
            if (document.DocumentElement.GetAttribute("lang") != "en")
                problems.Add($"{page.Path}: <html lang=\"{document.DocumentElement.GetAttribute("lang")}\"> is not English");
            if (!page.Title(document.Title?.Trim() ?? ""))
                problems.Add($"{page.Path}: title \"{document.Title?.Trim()}\" is not {page.TitleDescription}");
            problems.AddRange(AccessibilityChecks.Check(document).Select(v => $"{page.Path}: {v}"));
        }

        return problems;
    }
}
