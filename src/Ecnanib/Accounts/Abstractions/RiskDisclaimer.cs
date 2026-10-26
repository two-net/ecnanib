namespace Ecnanib.Accounts.Abstractions;

/// <summary>
/// The risk disclaimer shown on the landing page, during agent creation and on the agent detail page (FR-UI-05) and
/// accepted before a user's first live-mode agent starts (FR-ACC-08). It states what NFR-LEGAL-01 and NFR-LEGAL-02
/// require. Changing any paragraph means bumping <see cref="Version"/>: every user is then asked to accept again, and
/// each acceptance is kept per version in <c>disclaimer_acceptances</c> (<c>RiskDisclaimerTests</c> pins a hash of the
/// paragraphs beside the version). <see cref="Summary"/> is the one sentence every page's footer states (NFR-LEGAL-01
/// "prominently"); it is not part of what the user accepts and is outside the version rule.
/// </summary>
public static class RiskDisclaimer
{
    public const string Version = "2026-10-19";

    public const string Title = "Risk disclaimer";

    public const string Summary = "Ecnanib is software, not financial advice. Trading cryptocurrencies, and leveraged futures in particular, can lose your entire balance.";

    /// <summary>Shown with the acceptance form (FR-ACC-08), where a live agent can be started and on the settings page.</summary>
    public const string AcceptancePrompt = "You must read and accept the risk disclaimer before a live-mode agent can start.";

    /// <summary>The acceptance checkbox's label; it names the version being accepted.</summary>
    public const string AcceptanceLabel = $"I have read and accept the risk disclaimer (version {Version}).";

    /// <summary>The notice a page shows once the acceptance is recorded.</summary>
    public const string AcceptedNotice = "Thank you. The risk disclaimer is accepted.";

    public static readonly IReadOnlyList<string> Paragraphs =
    [
        "Ecnanib is software, not financial advice. Nothing it shows, including the output of the AI council, is a " +
        "recommendation to buy, sell or hold any asset. Every decision an agent makes runs under limits you configured, " +
        "and you remain responsible for it.",

        "Trading cryptocurrencies can result in the loss of your entire balance. Leveraged futures amplify both gains " +
        "and losses and can lose the entire balance in a very short time, including through liquidation. Only trade " +
        "with funds you can afford to lose, and never give an exchange key withdrawal permission.",

        "You are responsible for confirming that using Binance and the AI providers is legal in your jurisdiction and " +
        "for all taxes arising from your trading. Availability of these services varies by country and the system " +
        "cannot check this for you.",

        "The service is provided free of charge, as is, without warranty of any kind. The software may contain defects, " +
        "and exchanges, AI providers and network links may fail at any time.",
    ];
}
