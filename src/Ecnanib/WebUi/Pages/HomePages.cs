namespace Ecnanib.WebUi.Pages;

/// <summary>
/// The texts of the start page, in one place (NFR-USA-4): the landing page of a visitor, which a
/// user who is signed in sees too, with the way to their dashboard (FR-UI-2). The providers and
/// the exchange are named only to say which they are (NFR-LEG-4): by their names as text, with
/// no logo and nothing that says more of them than whose model fills a role. See docs/layout.md,
/// and for the illustration of the team (FR-ANI-7) docs/motion.md.
/// </summary>
public static class HomePages
{
    /// <summary>The title of the page in the browser. Its heading is the name of the service.</summary>
    public static string Title => "AI trading agents on Binance";

    /// <summary>What the service is.</summary>
    public static string What =>
        "Ecnanib is a free service for AI trading agents on Binance. You create agents, and each agent trades one symbol, " +
        "on Spot or on USDⓈ-margined perpetual Futures, on one timeframe.";

    /// <summary>Who decides, and what holds the decisions.</summary>
    public static string Who =>
        "A team of four AI roles runs each agent, with models of Claude, Gemini, GPT, and Grok, and with your own API keys. " +
        "Every plan of the team is checked against fixed limits, which the AI cannot override.";

    /// <summary>
    /// What the illustration of the team says under it (FR-ANI-7): whose team it is, and the
    /// order that the roles work in, in words, so that nothing relies on what moves (FR-THM-5).
    /// It holds for whatever provider the configuration file gives a role, also when it gives
    /// one provider two roles.
    /// </summary>
    public static string TeamCaption =>
        "The team of a new agent. The Market Analyst and the Sentiment Analyst work at the same time, " +
        "then the Strategist, then the Risk Manager. You choose the model of each role, and can give a role to another provider.";

    /// <summary>
    /// Whose model fills a role of that team, by the name of the provider as the catalog has it:
    /// <c>A model of Claude</c>. The name says which provider it is, and nothing else (NFR-LEG-4).
    /// </summary>
    public static string ModelOf(string providerName) => $"A model of {providerName}";

    /// <summary>What is at stake, before the link to the risk disclosure.</summary>
    public static string Risk =>
        "Demo uses Binance's test environment and no real funds. Live trades on your real account, and can lose money. Read the";

    /// <summary>The text of the link to the risk disclosure, at the end of that sentence.</summary>
    public static string RiskLink => "risk disclosure";

    /// <summary>The text of the link that leads a visitor to register, while registration is open.</summary>
    public static string RegisterLink => "Create an account";

    /// <summary>What the page says in place of that link while registration is closed.</summary>
    public static string RegistrationClosed => "Registration is closed. No new accounts are being accepted at the moment.";

    /// <summary>The text of the link that leads a visitor to sign in.</summary>
    public static string SignInLink => "Sign in to your account";

    /// <summary>The text of the link that leads a user who is signed in to their dashboard.</summary>
    public static string DashboardLink => "Go to your dashboard";

    /// <summary>The text of the link that leads a user who is signed in to their agents.</summary>
    public static string AgentsLink => "Go to your agents";

    /// <summary>The heading of the page that says there is no page at an address.</summary>
    public static string NotFoundTitle => "Page not found";

    /// <summary>
    /// What that page says. It answers for an address that has no page, and for a page that is
    /// not for the user who asked for it.
    /// </summary>
    public static string NotFound => "There is no page for you at this address.";

    /// <summary>The text of the link that leads from there to the start page.</summary>
    public static string BackToStart => "Back to the start page";

    /// <summary>The title, in the browser, of the page that a request which failed is answered with.</summary>
    public static string ErrorTitle => "Something went wrong";

    /// <summary>The heading of that page.</summary>
    public static string ErrorHeading => "Something went wrong.";

    /// <summary>What that page says: nothing of the error itself (NFR-LOG-6).</summary>
    public static string ErrorRecorded => "The error has been recorded. Try again in a moment.";

    /// <summary>What it says before the reference that the error was logged under.</summary>
    public static string ErrorReference => "If it keeps happening, report it with this reference:";
}
