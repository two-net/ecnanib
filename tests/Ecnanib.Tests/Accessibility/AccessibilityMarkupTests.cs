using AngleSharp.Dom;

namespace Ecnanib.Tests.Accessibility;

/// <summary>A state of a screen as it was reached: the page that was there, read into its elements.</summary>
/// <param name="State">The state, as the list has it.</param>
/// <param name="Path">The path and query that it is at.</param>
/// <param name="Page">The page that the browser was at after the steps of the state.</param>
/// <param name="NotReached">Why the browser was not at the state then, or null when it was.</param>
internal sealed record CapturedState(ScreenState State, string Path, IDocument Page, string? NotReached);

/// <summary>
/// Every state of <see cref="AccessibilityRules.States"/>, reached once and kept for the tests
/// of a class: the worlds are built and the states are gone through when the first test asks,
/// in the order of the list, each person on a browser of their own.
/// </summary>
public sealed class AccessibilityPages : IDisposable
{
    private readonly List<AccessibilityWorld> _worlds = [];
    private readonly Lazy<Task<IReadOnlyList<CapturedState>>> _captured;

    public AccessibilityPages() => _captured = new(CaptureAsync);

    /// <summary>Every state of the list as it was reached, in the order of the list.</summary>
    internal Task<IReadOnlyList<CapturedState>> AllAsync() => _captured.Value;

    public void Dispose()
    {
        foreach (var world in _worlds)
        {
            world.Dispose();
        }
    }

    private async Task<IReadOnlyList<CapturedState>> CaptureAsync()
    {
        var worlds = new Dictionary<World, AccessibilityWorld>();
        var visits = new Dictionary<(World, Who), IVisit>();
        var captured = new List<CapturedState>();

        foreach (var state in AccessibilityRules.States)
        {
            if (!worlds.TryGetValue(state.In, out var world))
            {
                world = state.In == World.Seeded ? await AccessibilityWorld.SeededAsync() : AccessibilityWorld.WithRegistrationClosed();

                worlds[state.In] = world;
                _worlds.Add(world);
            }

            // The browser of the person, which they come to as they are first asked for.
            if (!visits.TryGetValue((state.In, state.As), out var visit))
            {
                visit = new ReadVisit(world.CreateClient());
                await world.EnterAsync(state.As, visit);

                visits[(state.In, state.As)] = visit;
            }

            var notReached = await ScreenStates.ReachAsync(state, world, visit);

            captured.Add(new CapturedState(state, world.PathOf(state), AccessibleMarkup.Read(await visit.MarkupAsync()), notReached));
        }

        return captured;
    }
}

/// <summary>
/// The UI meets WCAG 2.1 AA, and controls are labeled for screen readers (NFR-USA-1). These
/// tests hold the markup of every state of every screen to the rules of
/// <see cref="AccessibilityRules.Markup"/>, in every run: each state of
/// <see cref="AccessibilityRules.States"/> is reached once, by the steps that the list says, and
/// each rule is a test of its own that names every state which breaks it. See
/// docs/accessibility.md.
/// </summary>
/// <remarks>
/// The pages are read, and no browser is driven: what is held is what the markup says. What a
/// browser makes of it, the colours of a page, where the focus goes, and what the keyboard
/// reaches, are held by the run that drives one, which the same list leads through the same
/// states. That the checks can fail is shown with pages that break one rule each, and that they
/// pass, with a page that has one of everything.
/// </remarks>
public class AccessibilityMarkupTests(AccessibilityPages pages) : IClassFixture<AccessibilityPages>
{
    // Where what a test plants goes: into the content of the page.
    private const string Planted = "<!--planted-->";

    // A page that keeps to every rule, in the frame that the layout gives every page.
    private const string Clean =
        """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1.0" />
            <title>Clean</title>
        </head>
        <body>
            <a class="skip-link" href="/planted#content">Skip to the content</a>
            <header>
                <a href="/">Ecnanib</a>
                <nav aria-label="Main"><ul><li><a href="/sign-in">Sign in</a></li></ul></nav>
            </header>
            <main id="content" tabindex="-1">
                <h1>Clean</h1>
                <!--planted-->
            </main>
            <footer>
                <nav aria-label="Legal"><ul><li><a href="/legal/terms">Terms of Service</a></li></ul></nav>
            </footer>
        </body>
        </html>
        """;

    // One of everything that the pages have, each as the rules ask for it.
    private const string OneOfEverything =
        """
        <p class="form-error" role="alert" tabindex="-1">Nothing was saved.</p>
        <p class="form-done" role="status">The key was checked.</p>
        <p class="form-warning" role="note">Two roles have one provider.</p>
        <p class="live-pause"><span role="status">This page follows what changes.</span> <a href="/planted?live=paused">Pause live updates</a></p>
        <form id="sign-out" method="post" action="/sign-out"><button type="submit">Sign out</button></form>
        <form method="post">
            <div class="field">
                <label for="email">Email address</label>
                <input id="email" name="Email" type="email" autocomplete="username" required aria-describedby="email-hint" />
                <p id="email-hint" class="field-hint">The address of your account.</p>
            </div>
            <div class="field">
                <label for="password">Password</label>
                <input id="password" name="Password" type="password" autocomplete="new-password" required
                       aria-invalid="true" aria-describedby="password-error password-hint" />
                <p id="password-error" class="field-error">Choose a password of at least 12 characters.</p>
                <p id="password-hint" class="field-hint">A long phrase is a good password.</p>
            </div>
            <div class="field">
                <label for="api-key">API key</label>
                <input id="api-key" name="ApiKey" type="password" autocomplete="off" spellcheck="false" />
            </div>
            <div class="field">
                <label for="code">Code from your authenticator app</label>
                <input id="code" name="Code" type="text" autocomplete="one-time-code" />
            </div>
            <div class="field">
                <label for="market">Market</label>
                <select id="market" name="Market"><option value="Spot" selected>Spot</option><option value="Futures">Futures</option></select>
            </div>
            <fieldset aria-describedby="theme-error">
                <legend>Theme</legend>
                <div class="choice">
                    <input id="theme-dark" type="radio" name="Theme" value="dark" aria-describedby="theme-dark-hint" />
                    <label for="theme-dark">Dark</label>
                    <p id="theme-dark-hint" class="field-hint">Light text on a dark page.</p>
                </div>
                <div class="choice">
                    <input id="theme-light" type="radio" name="Theme" value="light" />
                    <label for="theme-light">Light</label>
                </div>
                <p id="theme-error" class="field-error">Choose one.</p>
            </fieldset>
            <div class="field">
                <input id="risk" name="Risk" type="checkbox" value="confirmed" />
                <label for="risk">I have read the <a href="/legal/risk-disclosure" target="_blank" rel="noopener">risk disclosure (opens in a new tab)</a></label>
            </div>
            <button type="submit">Save</button>
        </form>
        <h2 id="trades-heading">Trades</h2>
        <div class="agents" role="region" aria-labelledby="trades-heading" tabindex="0">
            <table>
                <caption>The closed trades of the agent</caption>
                <thead>
                    <tr><th scope="col">Closed</th><th scope="col">Net result</th></tr>
                </thead>
                <tbody>
                    <tr><th scope="row">24 November 2026</th><td>+15 USDT</td></tr>
                    <tr><td colspan="2">No other trade is closed.</td></tr>
                </tbody>
            </table>
        </div>
        <h3 id="check-heading">Risk Guard</h3>
        <table aria-labelledby="check-heading">
            <tbody>
                <tr><th scope="row">Risk per trade</th><td>Passed</td></tr>
            </tbody>
        </table>
        <figure>
            <svg class="equity-chart" viewBox="0 0 10 10" role="img" aria-label="The equity of the agent over time" aria-describedby="equity-caption">
                <polyline points="0,0 10,10" />
            </svg>
            <figcaption id="equity-caption">It rose from 1000 to 1040 USDT.</figcaption>
        </figure>
        <p><span class="role-marker" aria-hidden="true"></span>working <img src="/mark.png" alt="" /></p>
        <details>
            <summary>Menu</summary>
            <nav aria-label="Pages of trades"><a href="/planted?trades=2">Older trades</a></nav>
        </details>
        <dialog id="session-warning" role="alertdialog" aria-labelledby="session-title" aria-describedby="session-says">
            <p id="session-title">Your session ends soon</p>
            <p id="session-says">You are signed out at 14:32 UTC unless you stay.</p>
            <button type="submit" form="sign-out" formaction="/session/stay">Stay signed in</button>
            <button type="submit" form="sign-out">Sign out now</button>
        </dialog>
        <div id="blazor-error-ui" role="alert">
            An unhandled error has occurred.
            <a href="." class="reload">Reload</a>
            <button type="button" class="dismiss" aria-label="Dismiss"><span aria-hidden="true">🗙</span></button>
        </div>
        <button type="button" aria-label="Dismiss the message">Dismiss</button>
        """;

    // A page that asks for the focus, where no message of its form takes it: a warning that
    // answers the form, at its foot.
    private const string AsksForTheFocus =
        """
        <form method="post">
            <div class="field">
                <label for="capital">Allocated capital</label>
                <input id="capital" name="AllocatedCapital" type="text" inputmode="decimal" autocomplete="off" />
            </div>
            <p class="form-warning" role="alert" tabindex="-1" autofocus>The agent is in Live. Save it once more to change it.</p>
            <button type="submit" name="Confirm" value="live">Save for Live</button>
        </form>
        """;

    /// <summary>The rules, by what each is called: one test for each.</summary>
    public static TheoryData<string> Rules => [.. AccessibilityRules.Markup.Select(rule => rule.Id)];

    /// <summary>Markup that breaks one rule in the content of a page, with a part of what the check says of it.</summary>
    public static TheoryData<string, string, string> PlantedPages => new()
    {
        { "heading order", "<h3>Roles</h3>", "goes from level 1 to \"Roles\" of level 3. Give it level 2." },
        { "heading order", "<h2>Team</h2><h4>Market Analyst</h4>", "goes from level 2 to \"Market Analyst\" of level 4" },

        { "ids", "<p id=\"hint\">One.</p><p id=\"hint\">Another.</p>", "has the id \"hint\" 2 times." },

        { "references", "<label for=\"gone\">Name</label>", "names \"gone\" in for, and no element has that id." },
        { "references", "<p aria-labelledby=\"gone\">Text</p>", "names \"gone\" in aria-labelledby" },
        {
            "references",
            "<label for=\"name\">Name</label><input id=\"name\" name=\"Name\" type=\"text\" aria-describedby=\"name-hint name-error\" />" +
            "<p id=\"name-hint\">What you call it.</p>",
            "names \"name-error\" in aria-describedby"
        },
        { "references", "<button type=\"button\" aria-controls=\"gone\">Open</button>", "names \"gone\" in aria-controls" },
        { "references", "<button type=\"submit\" form=\"gone\">Sign out</button>", "names \"gone\" in form" },

        { "field names", "<input name=\"Email\" type=\"email\" autocomplete=\"email\">", "has the field Email without a label." },
        {
            // What only some users are shown is no label.
            "field names",
            "<input name=\"Email\" type=\"email\" autocomplete=\"email\" placeholder=\"Email address\" title=\"Email address\">",
            "has the field Email without a label."
        },
        {
            // A label that names no field, beside a field.
            "field names",
            "<label>Market</label><select id=\"market\" name=\"Market\"><option>Spot</option></select>",
            "has the field Market without a label."
        },
        { "field names", "<textarea id=\"notes\"></textarea>", "has the field notes without a label." },
        {
            // A label that says nothing to a screen reader.
            "field names",
            "<label for=\"empty\"><span aria-hidden=\"true\">*</span></label><input id=\"empty\" name=\"Name\" type=\"text\" />",
            "has the field Name without a label."
        },

        {
            "groups",
            "<input id=\"close\" type=\"radio\" name=\"Position\" value=\"close\" /><label for=\"close\">Close it</label>",
            "has the choice Position outside a fieldset with a legend."
        },
        {
            "groups",
            "<fieldset><input id=\"close\" type=\"radio\" name=\"Position\" value=\"close\" /><label for=\"close\">Close it</label></fieldset>",
            "has the choice Position outside a fieldset with a legend."
        },
        {
            "groups",
            "<input id=\"one\" type=\"checkbox\" name=\"Accepts\" value=\"terms\" /><label for=\"one\">Terms</label>" +
            "<input id=\"two\" type=\"checkbox\" name=\"Accepts\" value=\"privacy\" /><label for=\"two\">Privacy</label>",
            "has the choice Accepts outside a fieldset with a legend."
        },

        { "link and button names", "<a href=\"/agents\"></a>", "has <a href=\"/agents\">, which says nothing." },
        { "link and button names", "<button type=\"button\"><span aria-hidden=\"true\">🗙</span></button>", "which says nothing." },
        { "link and button names", "<a href=\"/agents\"><svg aria-hidden=\"true\" viewBox=\"0 0 1 1\"></svg></a>", "which says nothing." },
        { "link and button names", "<a href=\"/agents\"><img src=\"/agents.png\" alt=\"\" /></a>", "which says nothing." },
        { "link and button names", "<details><summary></summary><p>What it holds.</p></details>", "has <summary>, which says nothing." },
        { "link and button names", "<input type=\"submit\" value=\"\" />", "which says nothing." },

        {
            "label in name",
            "<button type=\"button\" aria-label=\"Close\">Dismiss the message</button>",
            "\"Close\" and shows \"Dismiss the message\"."
        },
        {
            "label in name",
            "<label for=\"name\">Name</label><input id=\"name\" name=\"Name\" type=\"text\" aria-label=\"What the agent is called\" />",
            "\"What the agent is called\" and shows \"Name\"."
        },

        { "native controls", "<div role=\"button\">Go</div>", "gives <div role=\"button\"> the role of a control, and it is none." },
        { "native controls", "<a>Nowhere</a>", "a link that leads nowhere" },
        { "native controls", "<p tabindex=\"3\">x</p>", "a place of its own in the order of the keyboard" },
        { "native controls", "<div tabindex=\"0\">x</div>", "a stop of the keyboard, and it is neither a control nor a named region that scrolls." },
        {
            // A region that scrolls is a stop of the keyboard once it says what it is.
            "native controls",
            "<div class=\"agents\" role=\"region\" tabindex=\"0\"><p>x</p></div>",
            "a stop of the keyboard, and it is neither a control nor a named region that scrolls."
        },
        { "native controls", "<p tabindex=\"-1\">x</p>", "lets the focus be put on <p tabindex=\"-1\">" },

        { "tables", "<table><tbody><tr><th scope=\"row\">Now</th><td>1</td></tr></tbody></table>", "has a table without a name." },
        {
            "tables",
            "<table><caption>Trades</caption><thead><tr><th>Closed</th><th scope=\"col\">Result</th></tr></thead>" +
            "<tbody><tr><th scope=\"row\">Now</th><td>1</td></tr></tbody></table>",
            "has the table \"Trades\" with the heading cell \"Closed\", which does not say what it heads."
        },
        {
            "tables",
            "<table><caption>State changes</caption><thead><tr><th scope=\"col\">When</th><th scope=\"col\">State</th></tr></thead>" +
            "<tbody><tr><td>Now</td><td>Running</td></tr></tbody></table>",
            "has the table \"State changes\" with a row that no cell names."
        },
        { "tables", "<table><caption>Trades</caption><tr><td>1</td></tr></table>", "with a row that no cell names." },

        { "regions and landmarks", "<nav><a href=\"/agents\">Agents</a></nav>", "has a navigation without a name" },
        { "regions and landmarks", "<div role=\"region\"><p>x</p></div>", "has a region without a name" },
        { "regions and landmarks", "<nav aria-label=\"Main\"><a href=\"/agents\">Agents</a></nav>", "has two navigations called \"Main\"." },
        {
            "regions and landmarks",
            "<h2 id=\"one\">Recent decisions</h2><div role=\"region\" aria-labelledby=\"one\"><p>x</p></div>" +
            "<h2 id=\"two\">Recent decisions</h2><div role=\"region\" aria-labelledby=\"two\"><p>y</p></div>",
            "has two regions called \"Recent decisions\"."
        },

        { "pictures", "<svg viewBox=\"0 0 1 1\"><path d=\"M0 0\" /></svg>", "has a picture that says nothing and is not hidden" },
        { "pictures", "<svg role=\"img\" viewBox=\"0 0 1 1\"><path d=\"M0 0\" /></svg>", "has a picture that says nothing and is not hidden" },
        { "pictures", "<img src=\"/chart.png\" />", "has a picture that says nothing and is not hidden" },

        {
            "wrong fields",
            "<label for=\"password\">Password</label>" +
            "<input id=\"password\" name=\"Password\" type=\"password\" autocomplete=\"new-password\" aria-invalid=\"true\"" +
            " aria-describedby=\"password-hint\" />" +
            "<p id=\"password-hint\" class=\"field-hint\">At least 12 characters.</p><p class=\"field-error\">Choose a longer one.</p>",
            "marks Password as wrong, and nothing it is described by says what is wrong."
        },
        {
            "wrong fields",
            "<label for=\"name\">Name</label><input id=\"name\" name=\"Name\" type=\"text\" autocomplete=\"off\" />" +
            "<p id=\"name-error\" class=\"field-error\">Choose another name.</p>",
            "says \"Choose another name.\" of a field, and no field is described by it."
        },
        {
            "wrong fields",
            "<label for=\"name\">Name</label><input id=\"name\" name=\"Name\" type=\"text\" aria-invalid=\"true\" aria-describedby=\"name-error\" />" +
            "<p id=\"name-error\" class=\"field-error\"></p>",
            "marks Name as wrong"
        },

        {
            "focus on load",
            "<button type=\"button\" autofocus>One</button><button type=\"button\" autofocus>Another</button>",
            "has 2 elements that ask for the focus. One does."
        },
        {
            "focus on load",
            "<dialog aria-label=\"Your session ends soon\"><button type=\"button\" autofocus>Stay signed in</button></dialog>",
            "asks for the focus inside a dialog"
        },
        {
            "focus on load",
            "<p class=\"form-error\" role=\"alert\">Nothing was saved.</p>" +
            "<label for=\"name\">Name</label><input id=\"name\" name=\"Name\" type=\"text\" autofocus />",
            "beside a message of its form"
        },
        {
            "focus on load",
            "<p class=\"form-done\" role=\"status\">The key was checked.</p><button type=\"button\" autofocus>Check it again</button>",
            "beside a message of its form"
        },

        { "messages", "<p class=\"form-error\">Nothing was saved.</p>", "Give it role=\"alert\"." },
        { "messages", "<p class=\"form-error\" role=\"status\">Nothing was saved.</p>", "Give it role=\"alert\"." },
        { "messages", "<p class=\"form-done\">The key was checked.</p>", "Give it role=\"status\"." },
        { "messages", "<p class=\"form-warning\">Two roles have one provider.</p>", "which does not say what it is." },
        {
            // A warning that takes the focus answers a form: it is said as it appears.
            "messages",
            "<p class=\"form-warning\" role=\"note\" tabindex=\"-1\" autofocus>Save it once more to change it.</p>",
            "which does not say what it is."
        },

        {
            "new tabs",
            "<a href=\"/legal/terms\" target=\"_blank\" rel=\"noopener\">Terms of Service</a>",
            "in a new tab and does not say so."
        },
        {
            // Said to a screen reader alone, it is not said to everyone.
            "new tabs",
            "<a href=\"/legal/terms\" target=\"_blank\" rel=\"noopener\" aria-label=\"Terms of Service (opens in a new tab)\">Terms of Service</a>",
            "in a new tab and does not say so."
        },

        { "no title attribute", "<abbr title=\"Take-profit\">TP</abbr>", "says something in a title, which a keyboard does not show." },

        {
            "input purpose",
            "<label for=\"email\">Email address</label><input id=\"email\" name=\"Email\" type=\"email\" />",
            "does not say what Email is for in autocomplete."
        },
        {
            "input purpose",
            "<label for=\"password\">Password</label><input id=\"password\" name=\"Password\" type=\"password\" autocomplete=\"off\" />",
            "does not say what Password is for in autocomplete."
        },
        {
            "input purpose",
            "<label for=\"code\">Code</label><input id=\"code\" name=\"Code\" type=\"text\" inputmode=\"numeric\" />",
            "does not say what Code is for in autocomplete."
        },
        {
            // A key is kept from the browser by saying so, and by nothing less.
            "input purpose",
            "<label for=\"key\">API key</label><input id=\"key\" name=\"ApiKey\" type=\"password\" />",
            "does not say what ApiKey is for in autocomplete."
        },

        { "dialogs", "<dialog><p>Rejoining the server...</p><button type=\"button\">Retry</button></dialog>", "has a dialog without a name" },
        { "dialogs", "<dialog aria-labelledby=\"gone\"><p>Rejoining the server...</p></dialog>", "has a dialog without a name" },

        { "details", "<details><p>What it holds.</p></details>", "has a part that opens and does not say what it is." },
        {
            "details",
            "<details><summary><span aria-hidden=\"true\">≡</span></summary><p>What it holds.</p></details>",
            "has a part that opens and does not say what it is."
        },

        {
            "hidden parts",
            "<div aria-hidden=\"true\"><a href=\"/agents\">Agents</a></div>",
            "hides <a href=\"/agents\"> from a screen reader and leaves it a stop of the keyboard."
        },
        { "hidden parts", "<button type=\"button\" aria-hidden=\"true\">Go</button>", "from a screen reader and leaves it a stop of the keyboard." },
    };

    /// <summary>A change to the frame of a page that breaks one rule, with a part of what the check says of it.</summary>
    public static TheoryData<string, string, string, string> FramedPages => new()
    {
        { "language", "<html lang=\"en\">", "<html>", "does not say which language it is in. Give its first element lang." },
        { "language", "<html lang=\"en\">", "<html lang=\"\">", "does not say which language it is in." },

        { "title", "<title>Clean</title>", string.Empty, "has no title. Give it a PageTitle that says what it is." },
        { "title", "<title>Clean</title>", "<title> </title>", "has no title." },
        { "title", "<title>Clean</title>", "<title>Clean</title><title>Again</title>", "has 2 titles. A page has one." },

        { "one heading", "<h1>Clean</h1>", "<p>Clean</p>", "has 0 first headings. A page has one h1." },
        { "one heading", "<h1>Clean</h1>", "<h1>Clean</h1><h1>Again</h1>", "has 2 first headings. A page has one h1." },
        { "one heading", "</footer>", "<h1>Again</h1></footer>", "has 2 first headings." },
        { "one heading", "<main id=\"content\" tabindex=\"-1\">", "<div id=\"content\">", "has its h1 outside main." },
        { "heading order", "<h1>Clean</h1>", "<h2>Clean</h2>", "starts with \"Clean\" of level 2." },

        { "regions and landmarks", "</main>", "</main><main></main>", "has 2 main elements. A page has one." },
        { "regions and landmarks", "<header>", "<header></header><header>", "has 2 header elements at its top level." },
        { "regions and landmarks", "<footer>", "<footer></footer><footer>", "has 2 footer elements at its top level." },
        { "regions and landmarks", "<nav aria-label=\"Legal\">", "<nav aria-label=\"Main\">", "has two navigations called \"Main\"." },
        { "regions and landmarks", "<nav aria-label=\"Legal\">", "<nav>", "has a navigation without a name" },

        { "viewport", "initial-scale=1.0", "initial-scale=1.0, user-scalable=no", "keeps its user from enlarging it." },
        { "viewport", "initial-scale=1.0", "initial-scale=1.0, maximum-scale=1.0", "keeps its user from enlarging it." },

        { "native controls", "<main id=\"content\" tabindex=\"-1\">", "<main id=\"content\" tabindex=\"0\">", "a stop of the keyboard" },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public async Task Every_state_of_every_screen_keeps_to_the_rule(string rule)
    {
        var held = AccessibilityRules.Markup.Single(listed => listed.Id == rule);
        var reached = (await pages.AllAsync()).Where(state => state.NotReached is null).ToList();

        Assert.True(reached.Count > 0, "No state of AccessibilityRules.States was reached, so nothing was held to the rule.");

        var broken = reached
            .Select(state => (state.State, Findings: AccessibleMarkup.FindingsOn(state.Path, state.Page, held)))
            .Where(state => state.Findings.Count > 0)
            .ToList();

        Assert.True(
            broken.Count == 0,
            $"{broken.Count} of the {reached.Count} states break the rule \"{held.Id}\" (WCAG {held.Criterion}). {held.Holds} " +
            "See docs/accessibility.md:" + Environment.NewLine +
            string.Join(Environment.NewLine, broken.Select(state =>
                $"{state.State.Route}, \"{state.State.State}\", as {state.State.As}:{Environment.NewLine}" +
                string.Join(Environment.NewLine, state.Findings.Select(finding => $"  {finding}")))));
    }

    [Fact]
    public async Task Every_state_on_the_list_is_reached_and_shows_what_it_is_listed_for()
    {
        var states = await pages.AllAsync();

        // Each state of the list, once, in its order.
        Assert.Equal(AccessibilityRules.States, states.Select(state => state.State));

        var missed = states.Where(state => state.NotReached is not null).Select(state => state.NotReached!).ToList();

        Assert.True(
            missed.Count == 0,
            $"{missed.Count} of the {states.Count} states of AccessibilityRules.States were not reached, so nothing checks them. " +
            "Give each the steps that lead to it now, or what it shows now:" + Environment.NewLine +
            string.Join(Environment.NewLine, missed.Select(why => $"  {why}")));
    }

    [Fact]
    public async Task A_state_that_is_not_reached_says_why()
    {
        // An application with nobody in it, and the browser of a visitor.
        using var world = AccessibilityWorld.WithRegistrationClosed();
        var visit = new ReadVisit(world.CreateClient());

        Task<string?> ReachAsync(string route, string state, Reach how, string shows) =>
            ScreenStates.ReachAsync(new ScreenState(route, state, Who.Visitor, how, shows, "Planted."), world, visit);

        // A page that is not for whoever asks sends them elsewhere.
        var elsewhere = await ReachAsync("/account", "profile", Reach.Opened, "main h1");

        Assert.StartsWith(
            "The state \"profile\" of /account, as Visitor, is listed at /account, and the browser ended up at /sign-in?",
            elsewhere,
            StringComparison.Ordinal);

        // A page that is there, without what the state is listed for.
        Assert.Equal(
            "The state \"wrong password\" of /sign-in, as Visitor, is listed at /sign-in, and the page there has nothing that " +
            "matches main .form-error, which is what the state shows.",
            await ReachAsync("/sign-in", "wrong password", Reach.Opened, "main .form-error"));

        // A form that the page does not have.
        Assert.Equal(
            "The state \"registered\" of /sign-in, as Visitor, is listed at /sign-in, and was not reached. " +
            "The page at /sign-in has no form called \"register\".",
            await ReachAsync("/sign-in", "registered", Reach.Answered("register"), "main h1"));

        // And a state that is reached says nothing.
        Assert.Null(await ReachAsync("/sign-in", "form", Reach.Opened, "main input[name='Password']"));
    }

    [Theory]
    [MemberData(nameof(PlantedPages))]
    public void A_page_that_breaks_a_rule_is_found(string rule, string markup, string phrase)
    {
        var page = AccessibleMarkup.Read(Clean.Replace(Planted, markup, StringComparison.Ordinal));

        AssertIsFound(rule, page, phrase);
    }

    [Theory]
    [MemberData(nameof(FramedPages))]
    public void A_page_whose_frame_breaks_a_rule_is_found(string rule, string find, string replace, string phrase)
    {
        Assert.Contains(find, Clean, StringComparison.Ordinal);

        var page = AccessibleMarkup.Read(Clean.Replace(find, replace, StringComparison.Ordinal));

        AssertIsFound(rule, page, phrase);
    }

    [Fact]
    public void A_page_that_keeps_to_every_rule_is_left_alone()
    {
        // The frame alone, a page with one of everything, and a page that asks for the focus.
        AccessibleMarkup.AssertIsAccessible("/planted", AccessibleMarkup.Read(Clean));
        AccessibleMarkup.AssertIsAccessible("/planted", AccessibleMarkup.Read(Clean.Replace(Planted, OneOfEverything, StringComparison.Ordinal)));
        AccessibleMarkup.AssertIsAccessible("/planted", AccessibleMarkup.Read(Clean.Replace(Planted, AsksForTheFocus, StringComparison.Ordinal)));

        // And a line after the content that says what a page is doing, which only a screen reader is told.
        AccessibleMarkup.AssertIsAccessible(
            "/planted",
            AccessibleMarkup.Read(Clean.Replace(
                "</main>", "</main><p class=\"visually-hidden\" role=\"status\" data-loading=\"The page is loading.\"></p>", StringComparison.Ordinal)));
    }

    [Fact]
    public void Every_rule_has_a_check_and_a_planted_page()
    {
        var rules = AccessibilityRules.Markup.Select(rule => rule.Id).ToList();

        // A rule of the list has a check, and a check is of a rule of the list.
        Assert.Equal(rules.Order(StringComparer.Ordinal), AccessibleMarkup.Checked.Order(StringComparer.Ordinal));

        // The rule that each planted page is for, which is the first thing that its row says.
        var planted = PlantedPages.Cast<ITheoryDataRow>()
            .Concat(FramedPages.Cast<ITheoryDataRow>())
            .Select(row => (string)row.GetData()[0]!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(rules, rule => Assert.True(
            planted.Contains(rule),
            $"No planted page breaks the rule \"{rule}\", so nothing shows that its check can fail. " +
            "Add a row to PlantedPages, or to FramedPages where the rule is about the frame of a page."));

        Assert.All(planted, rule => Assert.True(
            rules.Contains(rule),
            $"A planted page is for the rule \"{rule}\", which AccessibilityRules.Markup does not list."));
    }

    /// <summary>
    /// Asserts that the check of a rule finds a page, and says a phrase of it, and that the
    /// page fails as a whole with a sentence that names it.
    /// </summary>
    private static void AssertIsFound(string rule, IDocument page, string phrase)
    {
        var findings = AccessibleMarkup.FindingsOn("/planted", page, AccessibilityRules.Markup.Single(listed => listed.Id == rule));

        Assert.True(
            findings.Any(finding => finding.Contains(phrase, StringComparison.Ordinal)),
            $"The check of the rule \"{rule}\" does not say \"{phrase}\" of the page. It says:{Environment.NewLine}" +
            (findings.Count == 0 ? "  nothing" : string.Join(Environment.NewLine, findings.Select(finding => $"  {finding}"))));

        Assert.All(findings, finding => Assert.StartsWith("The page at /planted ", finding, StringComparison.Ordinal));

        var failure = Record.Exception(() => AccessibleMarkup.AssertIsAccessible("/planted", page));

        Assert.NotNull(failure);
        Assert.Contains(phrase, failure.Message, StringComparison.Ordinal);
    }
}
