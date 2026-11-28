using Ecnanib.Accounts;
using Ecnanib.Tests.Accounts;

namespace Ecnanib.Tests.Accessibility;

/// <summary>Whose browser opens a state. Each is a browser of its own, with its own cookies.</summary>
internal enum Who
{
    /// <summary>Nobody is signed in.</summary>
    Visitor,

    /// <summary>A user who has verified their address, and has nothing else yet.</summary>
    Newcomer,

    /// <summary>A user who has not verified their address.</summary>
    Unverified,

    /// <summary>The user who has everything: keys, agents, cycles, trades, lessons, and notifications.</summary>
    User,

    /// <summary>A user who turns two-factor authentication on, and goes on with it.</summary>
    Guarded,

    /// <summary>Another browser of that user, which gave the password and is asked for the code.</summary>
    AtTheCode,

    /// <summary>A user who is an operator.</summary>
    Operator,
}

/// <summary>Which application a state is of.</summary>
internal enum World
{
    /// <summary>The application with its people and what they have.</summary>
    Seeded,

    /// <summary>An application whose configuration file has registration closed.</summary>
    RegistrationClosed,
}

/// <summary>How a state is reached, in steps that a client over HTTP and a browser both take.</summary>
internal abstract record Reach
{
    /// <summary>The page is opened at its path.</summary>
    public static readonly Reach Opened = new Get();

    /// <summary>The page is opened at its path, and a form of it is answered.</summary>
    /// <param name="form">The name of the form.</param>
    /// <param name="fields">What is entered, in place of what the form holds. A field that names a button of the form presses it.</param>
    public static Reach Answered(string form, params (string Name, string Value)[] fields) => new Post(null, form, fields);

    /// <summary>Another address is opened, and a form of the page that is there is answered, which leads to the page.</summary>
    /// <param name="from">The address that is opened.</param>
    /// <param name="form">The name of the form.</param>
    /// <param name="fields">What is entered, in place of what the form holds.</param>
    public static Reach AnsweredFrom(string from, string form, params (string Name, string Value)[] fields) => new Post(from, form, fields);

    /// <summary>The page is shown in place of another address, which stays the address of the browser.</summary>
    /// <param name="address">The address that is asked for.</param>
    public static Reach InPlaceOf(string address) => new Standing(address);

    internal sealed record Get : Reach;

    internal sealed record Post(string? From, string Form, (string Name, string Value)[] Fields) : Reach;

    internal sealed record Standing(string Address) : Reach;
}

/// <summary>A state of a screen: a page as it looks to someone, after what led there.</summary>
/// <param name="Route">The template of the page, as its RouteAttribute says it.</param>
/// <param name="State">What the state is, in a few words. A page has no two states of one name for one person.</param>
/// <param name="As">Whose browser opens it.</param>
/// <param name="How">How it is reached.</param>
/// <param name="Shows">
/// A CSS selector of what makes the state: the proof that it was reached. A state that follows
/// from an answer names what the answer brought, and a state that is a page as it opens names
/// what that page holds.
/// </param>
/// <param name="Why">What a user finds different in it, which is why it is on the list.</param>
/// <param name="Path">The path and query, with tokens of the world in braces. The route itself when null.</param>
/// <param name="In">The application that it is a state of.</param>
internal sealed record ScreenState(
    string Route, string State, Who As, Reach How, string Shows, string Why, string? Path = null, World In = World.Seeded);

/// <summary>A rule that the markup of every state is held to.</summary>
/// <param name="Id">What the rule is called, in the tests and in what they say.</param>
/// <param name="Criterion">The success criteria of WCAG 2.1 that it serves.</param>
/// <param name="Holds">What a page that keeps to it is like.</param>
internal sealed record MarkupRule(string Id, string Criterion, string Holds);

/// <summary>
/// What every screen is held to for accessibility (NFR-USA-1), in one place: the states of the
/// screens, the rules that the markup of each state keeps to, and the exceptions. This file is
/// the source of truth: <c>docs/accessibility.md</c> explains it, and
/// <see cref="AccessibilityRuleTests"/>, <see cref="AccessibilityMarkupTests"/>, and
/// <see cref="AccessibilityStyleTests"/> enforce it.
/// </summary>
/// <remarks>
/// A screen is more than its page as it opens: it looks different with nothing to show, with a
/// form that was refused, and after an answer. Each such look is a state here, with whose
/// browser reaches it and how. A page that is added has no state, and fails
/// <see cref="AccessibilityRuleTests"/> until it has one. To leave a state off, or to list an
/// exception, is to decide it here, where it shows, with why.
/// </remarks>
internal static class AccessibilityRules
{
    /// <summary>What a link that opens a new tab says in its own text.</summary>
    public const string NewTab = "opens in a new tab";

    /// <summary>
    /// In a field of a state: the code that the authenticator app of <see cref="Who.Guarded"/>
    /// shows now. The world reads the key of the app from the page that sets it up.
    /// </summary>
    public const string Code = "{Code}";

    /// <summary>In a field of a state: six digits that are not the code of now.</summary>
    public const string WrongCode = "{WrongCode}";

    /// <summary>
    /// What a path of the list may name of the seeded world, in braces: the world puts what it
    /// stands for in its place.
    /// </summary>
    public static readonly IReadOnlyList<string> Tokens =
    [
        "Agent",
        "LiveAgent",
        "DraftAgent",
        "BusyAgent",
        "Cycle",
        "VetoedCycle",
        "TestCycle",
        "Lesson",
        "Model",
        "VerificationToken",
        "ResetToken",
        "IdleAccount",
        "OperatorAccount",
    ];

    /// <summary>
    /// The parameters of a route that a state need not fill, each with the token that stands in
    /// for it. Any other parameter is filled by the path of the state.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        ["{Id:guid}"] = "{Agent}",
        ["{CycleId:guid}"] = "{Cycle}",
        ["{LessonId:guid}"] = "{Lesson}",
        ["{ModelId:guid}"] = "{Model}",
    };

    private const string Password = AccountsApplication.Password;

    /// <summary>
    /// The states of the screens, in the order that they are gone through. Each person goes
    /// through theirs in this order, so a state that stores something stands after the states
    /// that it would change. The states of two-factor authentication stand last, as one story:
    /// each code moves the clock of the world on.
    /// </summary>
    public static readonly IReadOnlyList<ScreenState> States =
    [
        // The start page.
        new("/", "start page", Who.Visitor, Reach.Opened, "main a[href='/register']",
            "The landing page of a visitor: what the service is, the team as an illustration, and the ways to register and to sign in."),
        new("/", "of a user", Who.User, Reach.Opened, "main a[href='/dashboard']",
            "A user has the bar of a user, with the menu and the count of notifications, and is shown the way to the dashboard."),

        // Nothing on the page is there only when registration is closed: it lacks the link to
        // register, here and in the bar, which a selector of what there is cannot say.
        new("/", "registration closed", Who.Visitor, Reach.Opened, "main .team-illustration",
            "With registration closed, the page says so in place of the link, and the bar leads to the sign-in alone.",
            In: World.RegistrationClosed),

        // The page that says an address has no page.
        new("/not-found", "itself", Who.Visitor, Reach.Opened, "main a[href='/']",
            "The page of an address that has no page, at its own address."),
        new("/not-found", "an address without a page", Who.Visitor, Reach.InPlaceOf("/no-such-page"), "main a[href='/']",
            "It is shown in place of an address that there is not, which stays the address: the link past the bar names that one."),
        new("/not-found", "not for this user", Who.User, Reach.InPlaceOf("/operator"), "main a[href='/']",
            "It is shown to a user in place of a screen that is for operators, under the bar of a user."),

        new("/Error", "itself", Who.Visitor, Reach.Opened, "main > h1 + p",
            "The page that says something went wrong."),

        // The appearance of a visitor. The last state puts the theme back, so the pages of the
        // visitor after it are as their device is set.
        new("/appearance", "choice", Who.Visitor, Reach.Opened, "main input[name='Theme']",
            "A group of three choices, each with what it means under it."),
        new("/appearance", "saved dark", Who.Visitor, Reach.Answered("theme", ("Theme", "dark")), "html[data-theme='dark']",
            "The page comes back in the theme that was chosen: every colour of it is the dark one."),
        new("/appearance", "back to system", Who.Visitor, Reach.Answered("theme", ("Theme", "system")), "main input#theme-system[checked]",
            "The page comes back as the device is set, after a theme was chosen."),

        new("/legal/terms", "the document", Who.Visitor, Reach.Opened, "main > h1 + p",
            "A legal document: a heading, its version, and its text."),
        new("/legal/privacy", "the document", Who.Visitor, Reach.Opened, "main > h1 + p",
            "A legal document: a heading, its version, and its text."),
        new("/legal/risk-disclosure", "the document", Who.Visitor, Reach.Opened, "main > h1 + p",
            "A legal document: a heading, its version, and its text."),

        // Signing in. The page that asks for a code is among the states of two-factor authentication.
        new("/sign-in", "form", Who.Visitor, Reach.Opened, "main input[name='Password']",
            "The form of two fields that every user goes through."),
        new("/sign-in", "wrong password", Who.Visitor,
            Reach.Answered("sign-in", ("Email", "nobody@example.com"), ("Password", "not the password")), "main .form-error",
            "The form comes back with one message for the whole of it, above its fields."),

        // The file of the seeded world names a country that the service is not offered in, so
        // its form asks where a visitor lives. The form without that field is left off: see below.
        new("/register", "form", Who.Visitor, Reach.Opened, "main select#country",
            "Two fields, a list of the countries to choose one from, and a group of three boxes that each link to a legal document."),
        new("/register", "every field refused", Who.Visitor,
            Reach.Answered("register", ("Email", "no address"), ("Password", "short")), "main #country-error",
            "Every field, the list, and every box is marked as wrong, each with its message under it."),

        // Nothing is stored: nobody who lives there is registered.
        new("/register", "country refused", Who.Visitor,
            Reach.Answered(
                "register",
                ("Email", "kim@example.com"),
                ("Password", Password),
                ("Country", AccessibilityWorld.Restricted),
                (nameof(LegalDocument.TermsOfService), LegalDocuments.CurrentVersionOf(LegalDocument.TermsOfService)),
                (nameof(LegalDocument.PrivacyPolicy), LegalDocuments.CurrentVersionOf(LegalDocument.PrivacyPolicy)),
                (nameof(LegalDocument.RiskDisclosure), LegalDocuments.CurrentVersionOf(LegalDocument.RiskDisclosure))),
            "main select#country[aria-invalid='true'] option[value='AQ'][selected]",
            "A form that is in order but for where its visitor lives: the list alone is marked as wrong, keeps what was chosen, " +
            "and says that the service is not offered there."),
        new("/register", "closed", Who.Visitor, Reach.Opened, "main > h1 + p",
            "With registration closed, the page has a sentence in place of the form.", In: World.RegistrationClosed),
        new("/register/sent", "the page", Who.Visitor, Reach.Opened, "main a[href='/register']",
            "What a visitor is told after registering."),

        new("/forgot-password", "form", Who.Visitor, Reach.Opened, "main input#email",
            "A form of one field."),
        new("/forgot-password", "address refused", Who.Visitor, Reach.Answered("forgot-password", ("Email", "no address")), "main #email-error",
            "The field is marked as wrong, with its message under it."),
        new("/forgot-password/sent", "the page", Who.Visitor, Reach.Opened, "main a[href='/forgot-password']",
            "What a visitor is told after asking for a link."),

        new("/reset-password", "dead link", Who.Visitor, Reach.Opened, "main p[role='alert']",
            "A link that does not work has a message in place of the form."),
        new("/reset-password", "form", Who.Visitor, Reach.Opened, "main input#password",
            "A link that works has the form of one field, with a hint under it.", Path: "/reset-password?token={ResetToken}"),
        new("/reset-password", "too short", Who.Visitor, Reach.Answered("reset-password", ("Password", "short")),
            "main input#password[aria-invalid='true']",
            "The field is marked as wrong, with its message beside its hint.", Path: "/reset-password?token={ResetToken}"),
        new("/reset-password/done", "the page", Who.Visitor, Reach.Opened, "main p[role='status']",
            "What a visitor is told after choosing a new password."),

        new("/verify-email", "dead link", Who.Visitor, Reach.Opened, "main p[role='alert']",
            "A link that does not work has a message in place of the button."),
        new("/verify-email", "asks to confirm", Who.Visitor, Reach.Opened, "main input[name='_handler'][value='verify-email']",
            "A link that works has a sentence and one button.", Path: "/verify-email?token={VerificationToken}"),
        new("/verify-email", "confirmed", Who.Visitor, Reach.Answered("verify-email"), "main p[role='status']",
            "The answer says that the address is confirmed, and leads to the sign-in.", Path: "/verify-email?token={VerificationToken}"),

        new("/account/deleted", "the page", Who.Visitor, Reach.Opened, "main p[role='status']",
            "What a visitor is told once their account is gone."),

        // The settings.
        new("/account", "profile", Who.User, Reach.Opened, "main a[href='/account/two-factor']",
            "The first page of the settings, under their navigation: sections with a link each."),

        // Before the newcomer proves who they are, which the page of a key then asks of them.
        new("/confirm-identity", "form", Who.Newcomer, Reach.Opened, "main input#password",
            "The form that asks for the password again, before a sensitive action."),
        new("/confirm-identity", "wrong password", Who.Newcomer, Reach.Answered("confirm-identity", ("Password", "not the password")),
            "main input#password[aria-invalid='true']",
            "The field is marked as wrong, with its message under it."),

        new("/account/keys", "none", Who.Newcomer, Reach.Opened, "main ul.keys a[href='/account/keys/ai/claude']",
            "Every provider and environment says that no key is stored, with a link to add one."),
        new("/account/keys", "verify first", Who.Unverified, Reach.Opened, "main p[role='status']",
            "A user whose address is not verified is told so, and offered no link to add a key."),
        new("/account/keys", "keys", Who.User, Reach.Opened, "main ul.keys code",
            "A stored key has its last characters, its usage, two links, a button, and a warning where it works from any address."),
        new("/account/keys", "a key was checked", Who.User, Reach.Answered("check-ai-claude"), "main p[role='status']",
            "The answer of a button of the list says what the provider said, above the list."),

        // The newcomer is sent to prove who they are, and from there to the page.
        new("/account/keys/ai/{Provider}", "first key", Who.Newcomer,
            Reach.AnsweredFrom("/account/keys/ai/claude", "confirm-identity", ("Password", Password)), "main input#api-key",
            "The form of one field for a key, with a hint under it.", Path: "/account/keys/ai/claude"),
        new("/account/keys/ai/{Provider}", "not a key", Who.Newcomer, Reach.Answered("store-key", ("ApiKey", "short")),
            "main input#api-key[aria-invalid='true']",
            "The field is marked as wrong, with its message beside its hint.", Path: "/account/keys/ai/claude"),
        new("/account/keys/ai/{Provider}", "replace", Who.User, Reach.Opened, "main p code",
            "With a key stored, the page says which, above the form.", Path: "/account/keys/ai/claude"),
        new("/account/keys/ai/{Provider}/delete", "the page", Who.User, Reach.Opened, "main input[name='_handler'][value='delete-key']",
            "The page that asks once more before a key is deleted.", Path: "/account/keys/ai/claude/delete"),

        new("/account/keys/binance/{Environment}", "first key", Who.Newcomer, Reach.Opened, "main input#secret-key",
            "The form of two fields for the keys of Binance, with the addresses that the service calls from.",
            Path: "/account/keys/binance/demo"),
        new("/account/keys/binance/{Environment}", "both refused", Who.Newcomer,
            Reach.Answered("store-key", ("ApiKey", "short"), ("SecretKey", "short")), "main input#secret-key[aria-invalid='true']",
            "Both fields are marked as wrong, each with its message.", Path: "/account/keys/binance/demo"),
        new("/account/keys/binance/{Environment}", "stored", Who.User, Reach.Opened, "main p code",
            "With keys stored, the page says which, above the form.", Path: "/account/keys/binance/live"),
        new("/account/keys/binance/{Environment}/delete", "the page", Who.User, Reach.Opened,
            "main input[name='_handler'][value='delete-key']",
            "The page that asks once more before the keys of an environment are deleted.", Path: "/account/keys/binance/demo/delete"),

        new("/account/two-factor", "verify first", Who.Unverified, Reach.Opened, "main p[role='status']",
            "A user whose address is not verified is told so, in place of the setup."),

        // The newcomer saves, so that the pages of the user keep their theme.
        new("/account/appearance", "three forms", Who.User, Reach.Opened, "main input[name='Times']",
            "Three groups of choices, each in a form with a button of its own: the theme, how much moves, and the time zone."),
        new("/account/appearance", "saved dark", Who.Newcomer, Reach.Answered("theme", ("Theme", "dark")), "html[data-theme='dark']",
            "The page comes back in the theme that was chosen: every colour of a page of a user is the dark one."),
        new("/account/appearance", "back to system", Who.Newcomer, Reach.Answered("theme", ("Theme", "system")),
            "main input#theme-system[checked]",
            "The page comes back as the device is set, after a theme was chosen."),
        new("/account/appearance", "saved UTC", Who.Newcomer, Reach.Answered("times", ("Times", "utc")), "main input#times-utc[checked]",
            "The page comes back with UTC chosen for the times of the pages."),
        new("/account/appearance", "back to this device", Who.Newcomer, Reach.Answered("times", ("Times", "device")),
            "main input#times-device[checked]",
            "The page comes back with the time zone of the device chosen, after UTC was."),

        new("/account/notifications", "off", Who.User, Reach.Opened, "main input[name='_handler'][value='email-on']",
            "The list of the critical events, and the button that turns the email on."),
        new("/account/notifications", "turned on", Who.Newcomer, Reach.Answered("email-on"), "main input[name='_handler'][value='email-off']",
            "The page comes back with the address that is sent to, and the button that turns the email off."),
        new("/account/notifications", "not verified", Who.Unverified, Reach.Opened, "main p[role='note']",
            "A user whose address is not verified is told that it is sent nothing."),

        // The newcomer has proved who they are by now, and has no agent. The user has agents
        // that run, and is told so in place of the form as the page opens.
        new("/account/delete", "form", Who.Newcomer, Reach.Opened, "main input[name='_handler'][value='delete-account']",
            "What is deleted, as a list, and the button that deletes it."),
        new("/account/delete", "refused while an agent runs", Who.User, Reach.Opened, "main .form-error",
            "A message in place of the form, for a user who has an agent that is not stopped."),

        // An operator is asked for no proof, and is told as the page opens that the account
        // stays while the configuration file lists it.
        new("/account/delete", "refused for an operator", Who.Operator, Reach.Opened, "main .form-error",
            "A message in place of the form, for an operator, whose account is not deleted."),

        // The dashboard.
        new("/dashboard", "setup checklist", Who.Newcomer, Reach.Opened, "main .setup-checklist",
            "A first-time user has the checklist with its steps, and no agent in either mode."),
        new("/dashboard", "Demo and Live with data", Who.User, Reach.Opened, "main .dashboard-positions",
            "Both modes with figures, a table of positions, cards of agents, a table of decisions, and alerts with a button each."),
        new("/dashboard", "paused", Who.User, Reach.Opened, "main .live-pause a[href$='/dashboard']",
            "A page that follows, paused by its user: it says since when it is as it is, leads back to the page that follows, and makes no connection.",
            Path: "/dashboard?live=paused"),

        // The agents.
        new("/agents", "none", Who.Newcomer, Reach.Opened, "main a[href='/agents/new']",
            "A user without an agent has a sentence in place of the table."),
        new("/agents", "table", Who.User, Reach.Opened, "main table",
            "A table that scrolls by itself, with links and a form in each row, and what the states mean under it."),

        new("/agents/new", "form", Who.User, Reach.Opened, "main select#agent-market",
            "The longest form: fields, a group of risk settings, and a group of roles with two choices each."),
        new("/agents/new", "no model without a key", Who.Newcomer, Reach.Opened, "main form a[href='/account/keys']",
            "Without a key, each role says that no model is offered, with a link to the keys."),
        new("/agents/new", "every field refused", Who.User,
            Reach.Answered("save-agent", ("Name", ""), ("AllocatedCapital", "much"), ("RiskPerTradePercent", "90")),
            "main input#agent-name[aria-invalid='true']",
            "Fields of the form and its roles are marked as wrong, each with its message."),
        new("/agents/new", "other market's symbols", Who.User, Reach.Answered("save-agent", ("Market", "Futures"), ("Show", "symbols")),
            "main select#agent-market option[value='Futures'][selected]",
            "A button that stores nothing brings the form back with the symbols of the market that was chosen."),
        new("/agents/new", "candidates", Who.User, Reach.Answered("save-agent", ("Timeframe", "1H"), ("Show", "candidates")),
            "main .role-candidate",
            "With a timeframe, each role says its strongest candidate, and the form may offer the recommended team."),

        new("/agents/{Id:guid}", "draft", Who.User, Reach.Opened, "main select#agent-market",
            "The form of an agent that there is, with every field open, and the links to its other pages.", Path: "/agents/{DraftAgent}"),
        new("/agents/{Id:guid}", "running, locked", Who.User, Reach.Opened, "main input[type='hidden'][name='Market']",
            "An agent that runs has its market, symbol, and timeframe as text, with a note in place of the fields."),
        new("/agents/{Id:guid}", "asked once more for Live", Who.User, Reach.Answered("save-agent", ("AllocatedCapital", "2000")),
            "main button[name='Confirm']",
            "A change to an agent in Live comes back with a question at the foot of the form, and another button.",
            Path: "/agents/{LiveAgent}"),
        new("/agents/{Id:guid}", "numbers refused", Who.User, Reach.Answered("save-agent", ("AllocatedCapital", "much")),
            "main input#agent-capital[aria-invalid='true']",
            "A field of a form that is otherwise in order is marked as wrong.", Path: "/agents/{DraftAgent}"),

        new("/agents/{Id:guid}/state", "Demo with everything", Who.User, Reach.Opened, "main svg.equity-chart",
            "The screen of an agent: notifications, the team, a position, controls, figures, a curve, trades, decisions, and changes."),
        new("/agents/{Id:guid}/state", "trades page 2", Who.User, Reach.Opened, "main nav.lesson-pages a[href$='/state']",
            "The second page of the trades, which the pager leads back from.", Path: "/agents/{Agent}/state?trades=2"),
        new("/agents/{Id:guid}/state", "a cycle runs", Who.User, Reach.Opened, "main .cycle-step-now",
            "While a cycle runs, the team is shown at work, with the steps that the cycle has come to.",
            Path: "/agents/{BusyAgent}/state"),
        new("/agents/{Id:guid}/state", "a cycle runs, paused", Who.User, Reach.Opened, "main [data-live='paused'] .cycle-step-now",
            "The same screen paused: the team and the position are written once, as they were then, and nothing of them moves or changes.",
            Path: "/agents/{BusyAgent}/state?live=paused"),
        new("/agents/{Id:guid}/state", "Live, stopped", Who.User, Reach.Opened, "main a[href$='/confirm/start']",
            "An agent in Live has links in place of the forms of its controls, and the badge of Live.",
            Path: "/agents/{LiveAgent}/state"),
        new("/agents/{Id:guid}/state", "draft, nothing recorded", Who.User, Reach.Opened, "main input[name='_handler'][value='start-agent']",
            "A draft has the form that starts it, and a sentence where its curve, its trades, and its decisions would be.",
            Path: "/agents/{DraftAgent}/state"),

        new("/agents/{Id:guid}/confirm/{Control}", "asks once more", Who.User, Reach.Opened,
            "main input[name='_handler'][value='start-agent']",
            "The page that asks once more before an agent in Live is started.", Path: "/agents/{LiveAgent}/confirm/start"),
        new("/agents/{Id:guid}/confirm/{Control}", "not offered", Who.User, Reach.Opened, "main p[role='status']",
            "A control that the state of the agent does not offer has a sentence in place of the button.",
            Path: "/agents/{LiveAgent}/confirm/pause"),

        new("/agents/{Id:guid}/cycles", "table", Who.User, Reach.Opened, "main table",
            "The table of the cycles, each with its kind, its mode, its outcome, and a link to its record."),
        new("/agents/{Id:guid}/cycles", "none", Who.User, Reach.Opened, "main h1#cycles-heading",
            "An agent without a cycle has a sentence in place of the table.", Path: "/agents/{DraftAgent}/cycles"),

        new("/agents/{Id:guid}/cycles/{CycleId:guid}", "approved", Who.User, Reach.Opened, "main .cycle-approved",
            "The timeline of a cycle that placed orders: the data package as a table, each call, the check as a table, and the orders."),
        new("/agents/{Id:guid}/cycles/{CycleId:guid}", "vetoed", Who.User, Reach.Opened, "main .cycle-vetoed",
            "The timeline of a cycle whose plan was vetoed, which says that no order was placed.",
            Path: "/agents/{Agent}/cycles/{VetoedCycle}"),
        new("/agents/{Id:guid}/cycles/{CycleId:guid}", "test cycle", Who.User, Reach.Opened, "main .cycle-kind-test",
            "The timeline of a cycle that its user ran as a test, which says so under the badge.",
            Path: "/agents/{Agent}/cycles/{TestCycle}"),

        new("/agents/{Id:guid}/risk-checks", "the page", Who.User, Reach.Opened, "main .risk-check-rules",
            "Each check of the Risk Guard under a heading, with the outcome of every rule as a table."),
        new("/agents/{Id:guid}/prompts", "the page", Who.User, Reach.Opened, "main .prompt-text",
            "The prompts of the agent, as long texts under headings of two levels."),
        new("/agents/{Id:guid}/lessons/reset", "the page", Who.User, Reach.Opened, "main input[name='_handler'][value='reset-lessons']",
            "The page that asks once more before the lessons of an agent are reset."),
        new("/agents/{Id:guid}/lessons/{LessonId:guid}/delete", "the page", Who.User, Reach.Opened, "main article.lesson",
            "The page that asks once more before a lesson is deleted, with the lesson as it is read."),

        new("/agents/{Id:guid}/test-cycle", "form", Who.User, Reach.Opened, "main input[name='_handler'][value='run-agent-test-cycle']",
            "What a test cycle does and costs, and the button that runs one."),
        new("/agents/{Id:guid}/test-cycle", "while a cycle runs", Who.User, Reach.Opened, "main .cycle-step-now",
            "While a cycle runs, its steps are written under the button.", Path: "/agents/{BusyAgent}/test-cycle"),

        new("/agents/{Id:guid}/stop", "asks about the position", Who.User, Reach.Opened, "main input#stop-close",
            "An agent with an open position has a group of two choices, each with what it means."),

        // The message is of the whole form, or of the group of choices: either shows the state.
        new("/agents/{Id:guid}/stop", "no choice", Who.User, Reach.Answered("stop-agent"), "main .form-error, main .field-error",
            "A form that chose nothing for the position comes back with a message, and the agent as it was."),
        new("/agents/{Id:guid}/stop", "not now", Who.User, Reach.Opened, "main p[role='status']",
            "An agent that cannot be stopped has a sentence in place of the form.", Path: "/agents/{DraftAgent}/stop"),

        new("/agents/{Id:guid}/close-position", "form", Who.User, Reach.Opened, "main input[name='_handler'][value='close-agent-position']",
            "The page that asks once more before a position is closed at market."),
        new("/agents/{Id:guid}/close-position", "nothing open", Who.User, Reach.Opened, "main p[role='status']",
            "An agent without a position has a sentence in place of the form.", Path: "/agents/{DraftAgent}/close-position"),

        new("/agents/{Id:guid}/delete", "form", Who.User, Reach.Opened, "main input[name='_handler'][value='delete-agent']",
            "The page that asks once more before an agent is deleted.", Path: "/agents/{DraftAgent}/delete"),
        new("/agents/{Id:guid}/delete", "not now", Who.User, Reach.Opened, "main p[role='status']",
            "An agent that runs cannot be deleted, and has a sentence in place of the form."),

        new("/agents/{Id:guid}/live", "form", Who.User, Reach.Opened, "main input#live-risk",
            "The risks as a list, a link to the risk disclosure, and one box to tick.", Path: "/agents/{DraftAgent}/live"),
        new("/agents/{Id:guid}/live", "box not ticked", Who.User, Reach.Answered("switch-agent-to-live"), "main #live-risk-error",
            "The box is marked as wrong, with its message under it.", Path: "/agents/{DraftAgent}/live"),
        new("/agents/{Id:guid}/live", "already Live", Who.User, Reach.Opened, "main p[role='status']",
            "An agent in Live has a sentence in place of the form.", Path: "/agents/{LiveAgent}/live"),

        new("/agents/{Id:guid}/demo", "form", Who.User, Reach.Opened, "main input[name='_handler'][value='switch-agent-to-demo']",
            "The page that asks once more before an agent is switched back to Demo.", Path: "/agents/{LiveAgent}/demo"),
        new("/agents/{Id:guid}/demo", "already Demo", Who.User, Reach.Opened, "main p[role='status']",
            "An agent in Demo has a sentence in place of the form.", Path: "/agents/{DraftAgent}/demo"),

        // The catalog and the experience.
        new("/catalog", "models", Who.User, Reach.Opened, "main .catalog-models",
            "A wide table of models for each provider, which scrolls by itself, and a sentence for a provider without one."),

        new("/experience", "nothing", Who.Newcomer, Reach.Opened, "main form.experience-filter",
            "The filter of five choices, and a sentence in place of each table."),
        new("/experience", "records", Who.User, Reach.Opened, "main .track-records",
            "The track records as tables by provider and by model, and the strengths of the roles as a third."),
        new("/experience", "filtered", Who.User, Reach.Opened, "main select#filter-market option[value='spot'][selected]",
            "The filter holds what was chosen, and the tables count the trades of that choice.",
            Path: "/experience?market=spot&mode=demo"),
        new("/experience/models/{ModelId:guid}", "the page", Who.User, Reach.Opened, "main .track-records",
            "The records of one model by the role that it held, with the filter without its role."),

        new("/experience/lessons", "none", Who.Newcomer, Reach.Opened, "main h1#lessons-heading",
            "A user without a lesson has a sentence in place of the list."),
        new("/experience/lessons", "list", Who.User, Reach.Opened, "main nav.lesson-pages a[href$='page=2']",
            "A page of lessons, each an article with a button and a link, and the pager under them."),
        new("/experience/lessons", "page 2", Who.User, Reach.Opened, "main nav.lesson-pages a[href='/experience/lessons']",
            "The last page of the lessons, which the pager leads back from.", Path: "/experience/lessons?page=2"),

        new("/experience/board", "Demo records", Who.User, Reach.Opened, "main .track-records",
            "The records of all users as two tables, and the form that takes the trades of the user off the board."),
        new("/experience/board", "nothing in Live", Who.User, Reach.Opened, "main select#filter-mode option[value='live'][selected]",
            "A mode without enough trades has a sentence in place of each table.", Path: "/experience/board?mode=live"),

        // The notifications. The last state marks them as read, which takes them off the
        // dashboard and off the screen of the agent: those stand before it.
        new("/notifications", "none", Who.Newcomer, Reach.Opened, "main h1#notifications-heading",
            "A user who was told nothing has a sentence in place of the list."),
        new("/notifications", "unread with pages", Who.User, Reach.Opened, "main input[name='_handler'][value='mark-all-read']",
            "A page of notifications that are not read, each with a link and a button, one button for all, and the pager."),
        new("/notifications", "all read", Who.User, Reach.Answered("mark-all-read"), "main ul.notifications",
            "Once all are read, the list has no button, and the bar counts none."),

        new("/usage", "nothing", Who.Newcomer, Reach.Opened, "main form.experience-filter",
            "The filter of two choices, and a sentence in place of the tables."),
        new("/usage", "tables", Who.User, Reach.Opened, "main .track-records",
            "The usage as two tables, by provider and by day."),
        new("/usage", "one agent", Who.User, Reach.Opened, "main select#usage-agent option[selected]",
            "The usage of one agent, which the filter holds, with the links back.", Path: "/usage?agent={Agent}"),

        // The readings of the service, each under a heading of its own and most of them as a
        // table: the first is the scheduler, whose table every application has rows for.
        new("/operator", "the page", Who.Operator, Reach.Opened, "main h2#status-scheduler + p + p + .status-table tbody th[scope='row']",
            "The screen of an operator, under a bar that has the link to it, with a table for each reading of the service."),

        // The global halt, which the screen above leads to. The second state switches it on,
        // and stores that: no cycle runs in the world, and no other page says whether the halt
        // is on, so it changes no other state.
        new("/operator/global-halt", "off", Who.Operator, Reach.Opened, "main input[name='_handler'][value='halt-on']",
            "What the halt does and whom it acts on, that it is off, and the one button, which switches it on."),
        new("/operator/global-halt", "switched on", Who.Operator, Reach.Answered("halt-on"),
            "main input[name='_handler'][value='halt-off']",
            "The page comes back with since when the halt is on, and the one button, which switches it off."),

        // The accounts of users, which the screen above leads to as well: the page that finds one
        // by its address, and the page of the one that was found. The account is one that nobody
        // signs in as, so disabling it, which the last of these states stores, signs nobody out
        // and changes no other state.
        new("/operator/accounts", "form", Who.Operator, Reach.Opened, "main input[name='Email']",
            "What disabling an account does, and the form of one field that finds an account by its address."),
        new("/operator/accounts", "no such account", Who.Operator,
            Reach.Answered("find-account", ("Email", "nobody@example.com")), "main .form-error",
            "The form comes back with one message above its field."),
        new(AuditRules.OperatorAccountRoute, "enabled", Who.Operator,
            Reach.AnsweredFrom("/operator/accounts", "find-account", ("Email", AccessibilityWorld.Idle)),
            "main input[name='_handler'][value='disable-account']",
            "The account that was found, by its address, that it is enabled, and the one button, which disables it.",
            Path: "/operator/accounts/{IdleAccount}"),
        new(AuditRules.OperatorAccountRoute, "of an operator", Who.Operator, Reach.Opened, "main p[role='status'] + p + p + p",
            "The account of an operator has a sentence in place of the button: it is not disabled.",
            Path: "/operator/accounts/{OperatorAccount}"),
        new(AuditRules.OperatorAccountRoute, "disabled", Who.Operator, Reach.Answered("disable-account"),
            "main input[name='_handler'][value='enable-account']",
            "The page comes back with since when the account is disabled, and the one button, which enables it.",
            Path: "/operator/accounts/{IdleAccount}"),

        // The sync of the model catalog, which the screen above leads to as well. The file of the
        // world holds a key of the service for one provider, so there is something to sync: the
        // second state syncs it, which adds no model that the first sync of the world did not.
        new("/operator/catalog-sync", "button", Who.Operator, Reach.Opened, "main input[name='_handler'][value='sync-catalog']",
            "What a sync does, the last sync of each provider as a table, and the one button."),
        new("/operator/catalog-sync", "synced", Who.Operator, Reach.AnsweredFrom("/operator/catalog-sync", "sync-catalog"), "main .form-done",
            "The page comes back with a message that the sync is over, above the table that says how it ended.",
            Path: "/operator/catalog-sync?synced=true"),

        // The maintenance that an operator announces, which the screen above leads to as well.
        // The third state announces it, and from then on every page has the banner, which this
        // page shows as every other would. The fourth cancels it, so no state after these has
        // the banner, and none before them had it.
        new("/operator/maintenance", "none announced", Who.Operator, Reach.Opened,
            "main input[name='_handler'][value='announce-maintenance']",
            "What an announcement does and what it does not, that none is announced, and the form of a start and an end."),
        new("/operator/maintenance", "start in the past", Who.Operator,
            Reach.Answered("announce-maintenance", ("Starts", "2026-11-23 02:00"), ("Ends", "2026-11-23 04:00")), "main .field-error",
            "The form comes back with what is wrong said at the field of the start, which is marked as wrong and has the focus."),
        new("/operator/maintenance", "announced", Who.Operator,
            Reach.AnsweredFrom("/operator/maintenance", "announce-maintenance", ("Starts", "2026-11-26 02:00"), ("Ends", "2026-11-26 04:00")),
            "body > section.site-notice p[role='status']",
            "The page comes back with a message that the maintenance is announced, with a table of what is announced, " +
            "which has the button that cancels it, and with the banner under the bar, which every page has from then on.",
            Path: "/operator/maintenance?announced=true"),
        new("/operator/maintenance", "cancelled", Who.Operator, Reach.Answered("change-maintenance"),
            "main #maintenance-announced + p",
            "The page comes back without the banner and without the table: none is announced."),

        // What the user of that account is told from then on, with the right password. It stands
        // after the state that disabled the account, and before the clock of the world moves.
        new("/sign-in", "account disabled", Who.Visitor,
            Reach.Answered("sign-in", ("Email", AccessibilityWorld.Idle), ("Password", Password)), "main #sign-in-disabled",
            "The form comes back with what keeps its user out, said only to whoever has the password."),

        // Two-factor authentication, as its user goes through it: she sets it up, signs in on
        // another browser, proves who she is, makes new recovery codes, and stops before turning
        // it off. Each code moves the clock of the world on by one step of the app, so nothing
        // of anyone else stands after these. A code that is right is among the wrong ones often
        // enough: five wrong ones in a row would have every code refused.
        new("/account/two-factor", "setup with QR", Who.Guarded, Reach.Opened, "main svg.qr-code",
            "The setup: steps, the QR code as a picture, the key as text, and the form of two fields."),
        new("/account/two-factor", "wrong code", Who.Guarded,
            Reach.Answered("turn-on", ("Code", WrongCode), ("Password", Password)), "main input#code[aria-invalid='true']",
            "The field of the code is marked as wrong, and the key is the one that was scanned."),
        new("/account/two-factor", "turned on with codes", Who.Guarded,
            Reach.Answered("turn-on", ("Code", Code), ("Password", Password)), "main ul.recovery-codes",
            "The answer says that it is on, and shows the recovery codes once, as a list."),
        new("/account/two-factor", "on", Who.Guarded, Reach.Opened, "main a[href='/account/two-factor/turn-off']",
            "With it on, the page says since when, and leads to the two pages that change it."),
        new("/account", "with two-factor", Who.Guarded, Reach.Opened, "main a[href='/account/two-factor']",
            "The profile says that two-factor authentication is on, and how many recovery codes are left."),

        new("/sign-in/code", "asks for the code", Who.AtTheCode, Reach.Opened, "main input#code",
            "The second step of signing in: one field for a code, with a hint under it."),

        // The message is of the whole form, or of its one field: either shows the state.
        new("/sign-in/code", "wrong code", Who.AtTheCode, Reach.Answered("sign-in-code", ("Code", WrongCode)),
            "main .form-error, main .field-error",
            "The form comes back with a message that the code is not right."),

        new("/confirm-identity", "with a code", Who.Guarded, Reach.Opened, "main input#code",
            "For an account with two-factor authentication, the form asks for a code beside the password."),
        new("/confirm-identity", "wrong code", Who.Guarded,
            Reach.Answered("confirm-identity", ("Password", Password), ("Code", WrongCode)), "main input#code[aria-invalid='true']",
            "The field of the code is marked as wrong, with its message beside its hint."),

        new("/account/two-factor/recovery-codes", "form", Who.Guarded, Reach.Opened, "main input#code",
            "The page that asks for a code before new recovery codes are made."),
        new("/account/two-factor/recovery-codes", "wrong code", Who.Guarded, Reach.Answered("new-recovery-codes", ("Code", WrongCode)),
            "main input#code[aria-invalid='true']",
            "The field is marked as wrong, with its message beside its hint."),
        new("/account/two-factor/recovery-codes", "new codes", Who.Guarded, Reach.Answered("new-recovery-codes", ("Code", Code)),
            "main ul.recovery-codes",
            "The answer says that the codes were replaced, and shows the new ones once."),

        new("/account/two-factor/turn-off", "form", Who.Guarded, Reach.Opened, "main input#code",
            "The page that asks for a code before two-factor authentication is turned off."),
        new("/account/two-factor/turn-off", "wrong code", Who.Guarded, Reach.Answered("turn-off", ("Code", WrongCode)),
            "main input#code[aria-invalid='true']",
            "The field is marked as wrong, with its message beside its hint."),
    ];

    /// <summary>
    /// The states that are left off the list, each with why. Every one of them is a message of a
    /// kind that a listed state has, on a page that is listed.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LeftOff = new Dictionary<string, string>
    {
        ["The form of /register without the list of the countries, where the configuration file names no country"] =
            "The one world names a country, so that its form asks where a visitor lives. Without the list the form is " +
            "the same, less one field: two fields and three boxes, as its states here have them beside the list.",

        ["\"Too many attempts\" on the pages that take a password, an address, or a code"] =
            "Reaching it means spending the attempts of an address, which would keep the people of the one world out of " +
            "their own states. It is a message above the form, as the wrong password of /sign-in is.",

        ["\"Too many wrong codes were entered\" on the pages that take a code"] =
            "The fifth wrong code in a row has every code of the account refused for a quarter of an hour, and the states " +
            "of two-factor authentication would end there. It is a message above the form, as the wrong code of /sign-in/code is.",

        ["\"We could not send the email\" on /register and /forgot-password"] =
            "It takes a mail server that fails, and the one world sends the links that its states open. It is a message " +
            "above the form, as the wrong password of /sign-in is.",

        ["A sentence in place of the button on /operator/catalog-sync, where the configuration file holds a key for no provider"] =
            "The one world holds a key for one provider, so that its operator has something to sync. It is a sentence in " +
            "place of a button, as the page of the account of an operator has one.",

        ["/Error, shown in place of a request that failed"] =
            "The page is shown in place of a failed request only by an application that runs as deployed, which is another " +
            "host. The page at its own address is the same component, without the reference of an error.",
    };

    /// <summary>The rules that the markup of every state keeps to, each with the criteria of WCAG 2.1 that it serves.</summary>
    public static readonly IReadOnlyList<MarkupRule> Markup =
    [
        new("language", "3.1.1", "The first element says which language the page is in."),
        new("title", "2.4.2", "The page has one title, which says what it is."),
        new("one heading", "1.3.1, 2.4.6", "The page has exactly one h1, inside main."),
        new("heading order", "1.3.1", "No level of heading is skipped on the way down."),
        new("ids", "4.1.1", "No two elements have the same id."),
        new("references", "1.3.1, 4.1.2", "What for, aria-labelledby, aria-describedby, aria-controls, and the form of a button name is there."),
        new("field names", "1.3.1, 3.3.2, 4.1.2", "Every field has a label."),
        new("groups", "1.3.1, 3.3.2", "Radio buttons, and checkboxes of one name, are in a fieldset with a legend."),
        new("link and button names", "2.4.4, 4.1.2", "Every link, button, and summary says something."),
        new("label in name", "2.5.3", "An aria-label on something with visible text contains that text."),
        new("native controls", "2.1.1, 2.4.3, 4.1.2",
            "Only controls of the browser are stops of the keyboard, beside a named region that scrolls, in the order of the page."),
        new("tables", "1.3.1", "A table has a name, every heading cell says what it heads, and every row of the body has a cell that names it."),
        new("regions and landmarks", "1.3.1, 2.4.1",
            "The page has one main, one header, and one footer. Every navigation and region has a name, which no other of its kind has."),
        new("pictures", "1.1.1", "Every picture says what it shows, or is hidden from a screen reader."),
        new("wrong fields", "3.3.1, 3.3.3", "A field that is marked as wrong is described by its message, and every such message describes a field."),
        new("focus on load", "2.4.3", "At most one element asks for the focus, outside every dialog, and none beside a message that takes it."),
        new("messages", "4.1.3", "A message of a form says to a screen reader what it is: an alert, a status, or a note."),
        new("new tabs", "technique G201", $"A link that opens a new tab says \"{NewTab}\" in its text."),
        new("no title attribute", "3.3.2, 4.1.2", "Nothing says something in a title attribute, which a keyboard does not show."),
        new("input purpose", "1.3.5", "A field for an address, a password, or a code says what it is for in autocomplete."),
        new("dialogs", "4.1.2", "Every dialog has a name."),
        new("details", "4.1.2", "Every part that opens starts with a summary that says what it is."),
        new("hidden parts", "4.1.2", "Nothing that is hidden from a screen reader is a stop of the keyboard."),
        new("viewport", "1.4.4", "The page lets its user enlarge it."),
    ];

    /// <summary>
    /// The fields that hide what is typed and are not for a password of the user, by the name
    /// that the form brings them under, each with why. Such a field says <c>off</c> in
    /// autocomplete, so that no browser offers to keep what it holds.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> NotOfTheUser = new Dictionary<string, string>
    {
        ["ApiKey"] =
            "A key of an AI provider or of Binance. It is no password of the account, and the pages never show it again " +
            "(FR-KEY-8): a browser that kept it would.",

        ["SecretKey"] =
            "The secret key of Binance, which belongs to the API key beside it, and is kept from the browser for the same reason.",
    };

    /// <summary>
    /// The selectors that take the outline of the focus away, each with why. Everything else
    /// that has the focus of the keyboard shows it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> WithoutOutline = new Dictionary<string, string>
    {
        ["h1:focus"] =
            "The heading takes the focus as a page opens, so that a screen reader starts at it. It is where the focus is " +
            "put, and no control: nothing is done there, and the next key leaves it.",

        ["main:focus"] =
            "The content takes the focus from the link past the bar. It is where the focus is put, and no control.",
    };

    /// <summary>
    /// The selectors of the controls whose edge is a line that the stylesheet mixes from the
    /// colour of the text, each with why it is held to the contrast of a control (WCAG 1.4.11).
    /// A line of that kind which only divides one part of a page from another is not listed.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ControlBorders = new Dictionary<string, string>
    {
        [".site-menu > summary"] =
            "What opens the menu on a narrow screen looks like a button by its line alone, so the line is what shows that it is one.",
    };
}
