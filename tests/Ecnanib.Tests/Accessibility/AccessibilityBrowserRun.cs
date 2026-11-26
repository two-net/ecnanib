using System.Collections.Concurrent;
using System.Text;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Ecnanib.WebUi.Layout;
using Microsoft.Playwright;

namespace Ecnanib.Tests.Accessibility;

/// <summary>What a browser found at a state of a screen. Each list is empty for a state that keeps to what is checked.</summary>
/// <param name="State">The state, as the list has it.</param>
/// <param name="Path">The path and query that it is at.</param>
/// <param name="NotReached">Why the browser was not at the state, or null when it was.</param>
/// <param name="Axe">What axe found on a light page, by the rules of WCAG 2.0 and 2.1, levels A and AA.</param>
/// <param name="AxeDark">What axe found for the colours of the page on a dark one.</param>
/// <param name="Walk">What the walk with the keyboard found.</param>
/// <param name="Widths">Where the page scrolls sideways.</param>
/// <param name="Console">What the browser said of the page: an error of a script, or a report of the content security policy.</param>
/// <param name="ToLookAt">What axe could not decide, which is for a person to look at, and fails nothing.</param>
/// <param name="Picture">The picture that was taken of a state that something was found at.</param>
internal sealed record StateFindings(
    ScreenState State,
    string Path,
    string? NotReached,
    IReadOnlyList<string> Axe,
    IReadOnlyList<string> AxeDark,
    IReadOnlyList<string> Walk,
    IReadOnlyList<string> Widths,
    IReadOnlyList<string> Console,
    IReadOnlyList<string> ToLookAt,
    string? Picture)
{
    /// <summary>The state as a failure names it.</summary>
    public string Named => $"{State.Route}, \"{State.State}\", as {State.As}, at {Path}";

    public bool Clean => NotReached is null && Axe.Count + AxeDark.Count + Walk.Count + Widths.Count + Console.Count == 0;
}

/// <summary>
/// The one run that leads a browser through every state of <see cref="AccessibilityRules.States"/>
/// (NFR-USA-1), in the order of the list, each person in a browser of their own, on an
/// application that listens. At each state it has axe check the page on a light and on a dark
/// one, walks the page with the keyboard, looks for a page that scrolls sideways at the width
/// of a phone and at twice the size of a desk, and listens to the console. What it finds is
/// kept for the tests, and written to a file. See docs/accessibility.md.
/// </summary>
/// <remarks>
/// It is one run and not a test for each state: a state may use up what it shows, as the page
/// that sets two-factor authentication up does, so the states are gone through once and in order.
/// </remarks>
internal static class AccessibilityBrowserRun
{
    /// <summary>The rules that axe checks: WCAG 2.0 and 2.1, levels A and AA.</summary>
    private static readonly string[] Levels = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

    /// <summary>The rules of axe that read the colours of a page, which are checked again on a dark one.</summary>
    private static readonly string[] OfColours = ["color-contrast", "link-in-text-block"];

    /// <summary>Twice the size of a desk: a window of 1280 that is enlarged to 200 percent is as wide as this (WCAG 2.1, 1.4.4).</summary>
    private static readonly ViewportSize TwiceTheSize = new() { Width = 640, Height = 400 };

    /// <summary>Where the findings of a run are written: beside the tests as they were built.</summary>
    public static string Folder => System.IO.Path.Combine(AppContext.BaseDirectory, "TestResults", "accessibility");

    /// <summary>The file that says what a run found, state by state.</summary>
    public static string File => System.IO.Path.Combine(Folder, "findings.md");

    public static async Task<IReadOnlyList<StateFindings>> GoThroughAsync(DrivenBrowser browser)
    {
        var worlds = new Dictionary<World, AccessibilityWorld>();
        var browsers = new Dictionary<(World, Who), (IBrowserContext Context, DrivenVisit Visit, ConcurrentQueue<string> Said)>();
        var found = new List<StateFindings>();

        Directory.CreateDirectory(Folder);

        foreach (var old in Directory.GetFiles(Folder))
        {
            System.IO.File.Delete(old);
        }

        try
        {
            foreach (var state in AccessibilityRules.States)
            {
                if (!worlds.TryGetValue(state.In, out var world))
                {
                    world = state.In == World.Seeded
                        ? await AccessibilityWorld.SeededAsync(listens: true)
                        : AccessibilityWorld.WithRegistrationClosed(listens: true);

                    worlds[state.In] = world;
                }

                // The browser of the person, which they come to as they are first asked for.
                if (!browsers.TryGetValue((state.In, state.As), out var theirs))
                {
                    var context = await browser.OpenAsync(world.ListeningAt);
                    var page = await context.NewPageAsync();

                    theirs = (context, new DrivenVisit(page), Listen(page));
                    browsers[(state.In, state.As)] = theirs;

                    await world.EnterAsync(state.As, theirs.Visit);
                }

                theirs.Said.Clear();

                var notReached = await ScreenStates.ReachAsync(state, world, theirs.Visit);

                found.Add(notReached is null
                    ? await LookAsync(state, world.PathOf(state), theirs.Visit.Page, theirs.Said)
                    : new StateFindings(state, world.PathOf(state), notReached, [], [], [], [], [], [], Picture: null));
            }
        }
        finally
        {
            foreach (var (context, _, _) in browsers.Values)
            {
                await context.DisposeAsync();
            }

            foreach (var world in worlds.Values)
            {
                world.Dispose();
            }
        }

        Write(found);

        return found;
    }

    /// <summary>Looks at the page that is open, in every way that the run looks at one.</summary>
    private static async Task<StateFindings> LookAsync(ScreenState state, string path, IPage page, ConcurrentQueue<string> said)
    {
        // What the browser said while the state was reached, before anything is done to the
        // page. A page that is shown in place of an address which has none, or is not for its
        // user, is answered with the status that says so, and the browser notes that: it is
        // what the state is.
        var console = said
            .Where(line => !(state.How is Reach.Standing && line.StartsWith("Failed to load resource", StringComparison.Ordinal)))
            .ToList();

        // The keyboard first, while the focus is where the page put it.
        var walk = await KeyboardWalk.FindingsAsync(page, Screens.FocusOnLoad);

        var light = await page.RunAxe(new AxeRunOptions { RunOnly = RunOnlyOptions.Tags(Levels) });

        // A page that says nothing of its theme follows the device, so a device that is set to
        // dark shows the page as its dark theme does: no rule of the stylesheet asks the device.
        await page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Dark });

        var dark = await page.RunAxe(new AxeRunOptions { RunOnly = RunOnlyOptions.Rules(OfColours) });

        await page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Light });

        var widths = new List<string>();

        foreach (var (size, called) in new[] { (DrivenBrowser.Phone, "at the width of a phone"), (TwiceTheSize, "at twice the size of a desk") })
        {
            await page.SetViewportSizeAsync(size.Width, size.Height);

            if (await SidewaysAsync(page) is { } wider)
            {
                widths.Add($"The page scrolls sideways {called}, {size.Width} wide: {wider}");
            }
        }

        await page.SetViewportSizeAsync(DrivenBrowser.Desk.Width, DrivenBrowser.Desk.Height);

        var findings = new StateFindings(
            state,
            path,
            NotReached: null,
            Sentences(light.Violations),
            Sentences(dark.Violations),
            walk,
            widths,
            console,
            Sentences(light.Incomplete),
            Picture: null);

        if (findings.Clean)
        {
            return findings;
        }

        var picture = System.IO.Path.Combine(Folder, $"{found(state)}.png");

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = picture, FullPage = true });

        return findings with { Picture = picture };

        static string found(ScreenState of) =>
            string.Concat($"{of.Route}-{of.State}-{of.As}".Select(letter => char.IsLetterOrDigit(letter) ? char.ToLowerInvariant(letter) : '-')).Trim('-');
    }

    /// <summary>
    /// What makes a page wider than its window, or null for a page that fits: a table that is
    /// wider scrolls by itself, in a part of its own, and is not counted.
    /// </summary>
    private static Task<string?> SidewaysAsync(IPage page) =>
        page.EvaluateAsync<string?>(
            """
            () => {
                const root = document.documentElement;

                if (root.scrollWidth <= root.clientWidth) {
                    return null;
                }

                const wider = [...document.body.querySelectorAll("*")].filter(element =>
                    element.getBoundingClientRect().right > root.clientWidth + 1
                    && element.checkVisibility()
                    && !element.closest("[role=region][tabindex='0']")
                    && !element.classList.contains("skip-link"));
                const innermost = wider.filter(element => !wider.some(other => other !== element && element.contains(other))).slice(0, 3);

                return `the page is ${root.scrollWidth} wide. ` + (innermost.length > 0
                    ? "Wider than the window: " + innermost.map(element => `<${element.tagName.toLowerCase()}${element.id ? "#" + element.id : ""}${element.classList.length ? "." + [...element.classList].join(".") : ""}>`).join(", ") + "."
                    : "Nothing that is shown is wider than the window.");
            }
            """);

    /// <summary>What axe found, one sentence for each element that a rule found something at.</summary>
    private static List<string> Sentences(AxeResultItem[] items) =>
    [
        .. items.SelectMany(item => item.Nodes.Select(node =>
            $"{item.Id} ({item.Impact ?? "to look at"}) at {node.Target}: {item.Help}. See {item.HelpUrl}")),
    ];

    /// <summary>
    /// What the browser says of a page while it is open: an error of a script, and a report of
    /// the content security policy. A page that works says neither.
    /// </summary>
    private static ConcurrentQueue<string> Listen(IPage page)
    {
        var said = new ConcurrentQueue<string>();

        page.PageError += (_, error) => said.Enqueue(error);
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                said.Enqueue(message.Text);
            }
        };

        return said;
    }

    /// <summary>Writes what the run found, state by state, for whoever reads it after the run.</summary>
    private static void Write(IReadOnlyList<StateFindings> found)
    {
        var text = new StringBuilder();

        text.AppendLine("# What the browser found");
        text.AppendLine();
        text.AppendLine(
            $"{found.Count} states of {found.Select(state => state.State.Route).Distinct().Count()} pages, " +
            $"{found.Count(state => !state.Clean)} with something found. See docs/accessibility.md.");

        foreach (var state in found.Where(state => !state.Clean))
        {
            text.AppendLine();
            text.AppendLine($"## {state.Named}");

            if (state.NotReached is not null)
            {
                text.AppendLine();
                text.AppendLine(state.NotReached);
            }

            Part("axe, on a light page", state.Axe);
            Part("axe, for the colours of a dark page", state.AxeDark);
            Part("The walk with the keyboard", state.Walk);
            Part("The widths", state.Widths);
            Part("The console", state.Console);

            if (state.Picture is not null)
            {
                text.AppendLine();
                text.AppendLine($"Picture: {state.Picture}");
            }
        }

        if (found.Any(state => state.ToLookAt.Count > 0))
        {
            text.AppendLine();
            text.AppendLine("## To look at by hand");
            text.AppendLine();
            text.AppendLine("What axe could not decide. Nothing of it fails a test.");

            foreach (var state in found.Where(state => state.ToLookAt.Count > 0))
            {
                Part(state.Named, state.ToLookAt);
            }
        }

        System.IO.File.WriteAllText(File, text.ToString());

        void Part(string heading, IReadOnlyList<string> sentences)
        {
            if (sentences.Count == 0)
            {
                return;
            }

            text.AppendLine();
            text.AppendLine($"{heading}:");
            text.AppendLine();

            foreach (var sentence in sentences)
            {
                text.AppendLine($"- {sentence}");
            }
        }
    }
}
