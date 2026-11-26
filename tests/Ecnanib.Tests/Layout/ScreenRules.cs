using Ecnanib.Accounts;
using Ecnanib.WebUi;

namespace Ecnanib.Tests.Layout;

/// <summary>Who a screen opens for.</summary>
internal enum ScreenFor
{
    /// <summary>Everyone, signed in or not.</summary>
    Visitor,

    /// <summary>A user who is signed in.</summary>
    User,

    /// <summary>A user who is an operator, and nobody else (NFR-SEC-3).</summary>
    Operator,
}

/// <summary>A screen of the specification, with where it is and what leads to it.</summary>
/// <param name="Screen">The screen, as FR-UI-2 names it.</param>
/// <param name="Route">The route of its page, as the page says it.</param>
/// <param name="For">Who it opens for.</param>
/// <param name="From">A page that leads to it, which whoever it opens for can open.</param>
/// <param name="Link">What the link there says.</param>
internal sealed record ScreenRule(string Screen, string Route, ScreenFor For, string From, string Link);

/// <summary>
/// The screen map (FR-UI-2), in one place: each screen of the specification with its route, who
/// it opens for, and the navigation that leads to it. This file is the source of truth:
/// <c>docs/layout.md</c> explains it, and <see cref="ScreenRuleTests"/> enforces it.
/// </summary>
/// <remarks>
/// A screen may have more than one entry: the editor of an agent has a route for a new agent and
/// one for an agent that there is, and the settings are reached from the bar and then by a
/// navigation of their own. To move a screen is to change its entry here, where it shows.
/// </remarks>
internal static class ScreenRules
{
    /// <summary>The name of the agent that the tests give a user, which the list of the agents links by.</summary>
    public const string AgentName = "Seeded";

    /// <summary>The part of a route that names an agent, which a test puts the id of that agent in place of.</summary>
    public const string AgentInRoute = "{Id:guid}";

    /// <summary>The screens, as FR-UI-2 lists them.</summary>
    public static readonly IReadOnlyList<string> Specified =
    [
        "landing",
        "registration and sign-in",
        "dashboard",
        "agent list",
        "agent editor",
        "agent detail",
        "experience",
        "model catalog",
        "settings: profile",
        "settings: security",
        "settings: API keys",
        "settings: appearance",
        "settings: notifications",
        "operator status",
    ];

    public static readonly IReadOnlyList<ScreenRule> Screens =
    [
        // The name of the service in the bar leads to it from every page.
        new("landing", WebUiModule.HomePath, ScreenFor.Visitor, SessionAuthentication.SignInPath, "Ecnanib"),

        new("registration and sign-in", WebUiModule.RegisterPath, ScreenFor.Visitor, WebUiModule.HomePath, "Register"),
        new("registration and sign-in", SessionAuthentication.SignInPath, ScreenFor.Visitor, WebUiModule.HomePath, "Sign in"),

        // The bar at the top of every page.
        new("dashboard", WebUiModule.DashboardPath, ScreenFor.User, WebUiModule.HomePath, "Dashboard"),
        new("agent list", WebUiModule.AgentsPath, ScreenFor.User, WebUiModule.HomePath, "Agents"),
        new("experience", WebUiModule.ExperiencePath, ScreenFor.User, WebUiModule.HomePath, "Experience"),
        new("model catalog", WebUiModule.CatalogPath, ScreenFor.User, WebUiModule.HomePath, "Model catalog"),

        // The list of the agents leads to the editor, for a new agent and for each that there
        // is, and to the screen of each agent, which is the page of its state.
        new("agent editor", WebUiModule.NewAgentPath, ScreenFor.User, WebUiModule.AgentsPath, "Create an agent"),
        new("agent editor", $"/agents/{AgentInRoute}", ScreenFor.User, WebUiModule.AgentsPath, AgentName),
        new("agent detail", $"/agents/{AgentInRoute}/state", ScreenFor.User, WebUiModule.AgentsPath, $"State of {AgentName}"),

        // The bar leads to the settings, at the profile, and each page of the settings leads to
        // the others.
        new("settings: profile", WebUiModule.AccountPath, ScreenFor.User, WebUiModule.HomePath, "Settings"),
        new("settings: profile", WebUiModule.AccountPath, ScreenFor.User, WebUiModule.KeysPath, "Profile"),
        new("settings: security", WebUiModule.TwoFactorPath, ScreenFor.User, WebUiModule.AccountPath, "Security"),
        new("settings: API keys", WebUiModule.KeysPath, ScreenFor.User, WebUiModule.AccountPath, "API keys"),
        new("settings: appearance", WebUiModule.AppearancePath, ScreenFor.User, WebUiModule.AccountPath, "Appearance"),
        new("settings: notifications", WebUiModule.NotificationEmailPath, ScreenFor.User, WebUiModule.AccountPath, "Email notifications"),

        // The bar of an operator, and of nobody else.
        new("operator status", WebUiModule.OperatorStatusPath, ScreenFor.Operator, WebUiModule.HomePath, "Operator status"),
    ];
}
