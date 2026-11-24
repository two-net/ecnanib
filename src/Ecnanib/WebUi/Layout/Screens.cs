using Ecnanib.Accounts;
using Ecnanib.WebUi.Pages.Dashboard;
using Ecnanib.WebUi.Pages.Experience;
using Ecnanib.WebUi.Pages.Notifications;
using Ecnanib.WebUi.Pages.Usage;

namespace Ecnanib.WebUi.Layout;

/// <summary>A screen that the navigation leads to.</summary>
/// <param name="Name">What the link to it says.</param>
/// <param name="Path">Where the link leads.</param>
/// <param name="Under">
/// The paths that are part of the screen, each with every path below it. The link is marked
/// while one of them is open.
/// </param>
public sealed record Screen(string Name, string Path, params string[] Under);

/// <summary>
/// The screens of the application and how the navigation names them, in one place (FR-UI-2,
/// NFR-USA-4): the bar at the top of every page, the navigation of the settings, and the texts of
/// the layout. See docs/layout.md.
/// </summary>
public static class Screens
{
    /// <summary>The name of the service, which leads to the start page from every page.</summary>
    public const string SiteName = "Ecnanib";

    /// <summary>The text of the link that leads past the bar, which is the first of every page.</summary>
    public const string SkipLink = "Skip to the content";

    /// <summary>The id of the part of a page that holds its content, which that link leads to.</summary>
    public const string ContentId = "content";

    /// <summary>What opens and closes the navigation on a narrow screen.</summary>
    public const string Menu = "Menu";

    /// <summary>The name of the navigation of the bar, as a screen reader says it.</summary>
    public const string MainNavigation = "Main";

    /// <summary>The name of the navigation of the settings, as a screen reader says it.</summary>
    public const string SettingsNavigation = "Settings";

    /// <summary>The name of the links to the legal documents at the foot of every page.</summary>
    public const string LegalNavigation = "Legal";

    /// <summary>The name of what the foot of a page leads a visitor to beside them, as a screen reader says it.</summary>
    public const string DeviceNavigation = "This device";

    /// <summary>The text of the link of a visitor to the page they sign in on.</summary>
    public const string SignIn = "Sign in";

    /// <summary>The text of the link of a visitor to the page they register on.</summary>
    public const string Register = "Register";

    /// <summary>The text of the button that signs a user out.</summary>
    public const string SignOut = "Sign out";

    public static readonly Screen Dashboard = new(DashboardPages.Title, WebUiModule.DashboardPath, WebUiModule.DashboardPath);

    /// <summary>The list of the agents, with the editor and the screen of each agent under it.</summary>
    public static readonly Screen Agents = new("Agents", WebUiModule.AgentsPath, WebUiModule.AgentsPath);

    public static readonly Screen Experience = new(ExperiencePages.Title, WebUiModule.ExperiencePath, WebUiModule.ExperiencePath);

    public static readonly Screen Usage = new(UsagePages.Title, WebUiModule.UsagePath, WebUiModule.UsagePath);

    public static readonly Screen Catalog = new("Model catalog", WebUiModule.CatalogPath, WebUiModule.CatalogPath);

    /// <summary>The settings: the link leads to the profile, and every page of the account is under it.</summary>
    public static readonly Screen Settings = new("Settings", WebUiModule.AccountPath, WebUiModule.AccountPath);

    /// <summary>The operator status screen, which the bar leads an operator to, and nobody else.</summary>
    public static readonly Screen OperatorStatus = new("Operator status", WebUiModule.OperatorStatusPath, WebUiModule.OperatorStatusPath);

    /// <summary>
    /// The notifications of a user. The link to them is in the bar on a narrow screen too, and
    /// says how many are not read: see <c>NotificationsLink</c>.
    /// </summary>
    public static readonly Screen Notifications = new(NotificationPages.Title, WebUiModule.NotificationsPath, WebUiModule.NotificationsPath);

    /// <summary>The profile: the page of the account itself, and the page that deletes it.</summary>
    public static readonly Screen Profile = new("Profile", WebUiModule.AccountPath, WebUiModule.DeleteAccountPath);

    /// <summary>The security of an account, which is its two-factor authentication.</summary>
    public static readonly Screen Security = new("Security", WebUiModule.TwoFactorPath, WebUiModule.TwoFactorPath);

    public static readonly Screen Keys = new("API keys", WebUiModule.KeysPath, WebUiModule.KeysPath);

    public static readonly Screen Appearance = new("Appearance", WebUiModule.AppearancePath, WebUiModule.AppearancePath);

    /// <summary>
    /// The appearance for a visitor, who chooses the theme of their device there (FR-THM-3).
    /// The foot of a page of a visitor leads to it. A user has <see cref="Appearance"/>.
    /// </summary>
    public static readonly Screen DeviceAppearance =
        new("Appearance", WebUiModule.DeviceAppearancePath, WebUiModule.DeviceAppearancePath);

    /// <summary>The choice of email for critical events. What the service told a user is <see cref="Notifications"/>.</summary>
    public static readonly Screen NotificationSettings =
        new(NotificationPages.Title, WebUiModule.NotificationEmailPath, WebUiModule.NotificationEmailPath);

    /// <summary>The screens that the bar leads every user to, in its order.</summary>
    public static readonly IReadOnlyList<Screen> Bar = [Dashboard, Agents, Experience, Usage, Catalog, Settings];

    /// <summary>The settings of FR-UI-2, in the order of the specification.</summary>
    public static readonly IReadOnlyList<Screen> OfSettings = [Profile, Security, Keys, Appearance, NotificationSettings];

    /// <summary>The legal documents, in the order that the foot of a page links to them.</summary>
    public static readonly IReadOnlyList<LegalDocument> Legal =
        [LegalDocument.TermsOfService, LegalDocument.PrivacyPolicy, LegalDocument.RiskDisclosure];

    /// <summary>The path of an address, as a request asked for it: without what follows a question mark.</summary>
    public static string PathOf(string address) => new Uri(address, UriKind.Absolute).AbsolutePath;

    /// <summary>
    /// What the link to a screen says about the page that is open, for <c>aria-current</c>:
    /// <c>page</c> when the link leads to that very page, <c>true</c> when the page is a part of
    /// the screen, and nothing otherwise.
    /// </summary>
    /// <param name="screen">The screen that the link leads to.</param>
    /// <param name="path">The path of the page that is open.</param>
    public static string? CurrentOf(Screen screen, string path)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(path);

        if (Same(path, screen.Path))
        {
            return "page";
        }

        return screen.Under.Any(part => Same(path, part) || Below(path, part)) ? "true" : null;
    }

    /// <summary>Whether a page is one of the settings, which carry the navigation of the settings.</summary>
    public static bool IsOfSettings(string path) => CurrentOf(Settings, path) is not null;

    // An address is found whatever its case, and with a slash at its end or without.
    private static bool Same(string path, string other) =>
        string.Equals(Trimmed(path), Trimmed(other), StringComparison.OrdinalIgnoreCase);

    private static bool Below(string path, string other) =>
        Trimmed(path).StartsWith(Trimmed(other) + "/", StringComparison.OrdinalIgnoreCase);

    private static string Trimmed(string path) => path.Length > 1 ? path.TrimEnd('/') : path;
}
