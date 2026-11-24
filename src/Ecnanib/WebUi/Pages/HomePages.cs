namespace Ecnanib.WebUi.Pages;

/// <summary>
/// The texts of the start page, in one place (NFR-USA-4): the landing page of a visitor, which a
/// user who is signed in sees too, with the way to their dashboard (FR-UI-2). The providers and
/// the exchange are named only to say which they are (NFR-LEG-4). See docs/layout.md.
/// </summary>
public static class HomePages
{
    /// <summary>The title of the page in the browser. Its heading is the name of the service.</summary>
    public const string Title = "AI trading agents on Binance";

    /// <summary>What the service is.</summary>
    public const string What =
        "Ecnanib is a free service for AI trading agents on Binance. You create agents, and each agent trades one symbol, " +
        "on Spot or on USDⓈ-margined perpetual Futures, on one timeframe.";

    /// <summary>Who decides, and what holds the decisions.</summary>
    public const string Who =
        "A team of four AI roles runs each agent, with models of Claude, Gemini, GPT, and Grok, and with your own API keys. " +
        "Every plan of the team is checked against fixed limits, which the AI cannot override.";

    /// <summary>What is at stake, before the link to the risk disclosure.</summary>
    public const string Risk =
        "Demo uses Binance's test environment and no real funds. Live trades on your real account, and can lose money. Read the";

    /// <summary>The text of the link to the risk disclosure, at the end of that sentence.</summary>
    public const string RiskLink = "risk disclosure";

    /// <summary>The text of the link that leads a visitor to register, while registration is open.</summary>
    public const string RegisterLink = "Create an account";

    /// <summary>What the page says in place of that link while registration is closed.</summary>
    public const string RegistrationClosed = "Registration is closed. No new accounts are being accepted at the moment.";

    /// <summary>The text of the link that leads a visitor to sign in.</summary>
    public const string SignInLink = "Sign in to your account";

    /// <summary>The text of the link that leads a user who is signed in to their dashboard.</summary>
    public const string DashboardLink = "Go to your dashboard";

    /// <summary>The text of the link that leads a user who is signed in to their agents.</summary>
    public const string AgentsLink = "Go to your agents";

    /// <summary>The heading of the page that says there is no page at an address.</summary>
    public const string NotFoundTitle = "Page not found";

    /// <summary>
    /// What that page says. It answers for an address that has no page, and for a page that is
    /// not for the user who asked for it.
    /// </summary>
    public const string NotFound = "There is no page for you at this address.";

    /// <summary>The text of the link that leads from there to the start page.</summary>
    public const string BackToStart = "Back to the start page";
}
