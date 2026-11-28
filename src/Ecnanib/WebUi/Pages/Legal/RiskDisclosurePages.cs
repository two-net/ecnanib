using Ecnanib.Accounts;

namespace Ecnanib.WebUi.Pages.Legal;

/// <summary>
/// The texts of the risk disclosure, in one place (NFR-USA-4): what a user risks when an agent
/// trades for them, which they accept at registration (FR-ACC-8, NFR-LEG-1).
/// </summary>
/// <remarks>
/// <para>
/// The disclosure states that automated trading can lose all allocated capital, that the
/// aggressive profile raises that risk, that leverage can cause rapid losses, that nothing in
/// the service is investment advice, and that past results do not predict future results
/// (NFR-LEG-1). Each of the five opens the section that its heading names.
/// </para>
/// <para>
/// <b>Whoever changes a word here changes the version in <see cref="LegalDocuments"/> in the
/// same commit,</b> so that an acceptance always names the text the user saw. The tests pin the
/// text to its version. See docs/accounts.md.
/// </para>
/// <para>
/// The text holds no value of the configuration file: a limit that changed there would change
/// what the document says, under a version that stayed.
/// </para>
/// </remarks>
public static class RiskDisclosurePages
{
    /// <summary>What the document is and which trading it is about, under its title and its version.</summary>
    public static IReadOnlyList<string> Opening { get; } =
    [
        "This document says what you risk when an agent of Ecnanib trades for you. Read all of it before you register.",
        "In Demo an agent trades in Binance's test environment, with no real funds. What follows is about Live, where an " +
        "agent places real orders in your own Binance account, with real money, and nobody looks at an order before it is placed.",
    ];

    /// <summary>The sections of the document, in their order: each a heading, and the paragraphs under it.</summary>
    public static IReadOnlyList<DisclosureSection> Sections { get; } =
    [
        new(
            "You can lose all of the capital you allocate",
            [
                "Automated trading can lose all of the capital that you allocate to an agent. The prices of cryptocurrencies " +
                "rise and fall fast and far, at any hour of any day. Allocate only money that you can afford to lose completely.",
                "The AI providers charge you for the calls that an agent makes with your keys, and Binance charges its fees. " +
                "You pay both whether the agent wins or loses.",
            ]),
        new(
            "The aggressive profile raises that risk",
            [
                "Ecnanib has one trading profile, and it is aggressive. That raises the risk of losing your capital.",
                "An agent looks for trades actively, acts on setups of moderate conviction, and uses the full risk that its " +
                "settings allow. Its risk settings are deliberately aggressive: they allow more risk on each trade than is usual " +
                "for private traders. Large losses, and several losses in a row, are more likely than with a cautious profile.",
            ]),
        new(
            "Leverage can cause rapid losses",
            [
                "On Futures an agent trades with leverage, and leverage can cause rapid losses.",
                "Leverage multiplies a loss as it multiplies a gain: a small move of the price against a position can take a " +
                "large part of its margin within minutes. Binance liquidates a position whose margin is used up. Fees and " +
                "funding payments add to a loss.",
                "On Spot an agent uses no leverage, and can lose your capital all the same.",
            ]),
        new(
            "The limits reduce the risk and do not remove it",
            [
                "Every trade is checked against fixed limits that the AI cannot override, every open position has a stop-loss " +
                "order at Binance, and an agent that reaches a loss limit is halted. None of this guarantees that a loss stays " +
                "within a limit.",
                "When the price moves fast, a stop-loss order can be filled at a worse price than the one it names. Binance, an " +
                "AI provider, or Ecnanib can be unavailable or make an error, and software has faults. During such a time an " +
                "agent may not act, and a position stays open with the orders that are at Binance.",
            ]),
        new(
            "No AI model is a proven trader",
            [
                "The decisions of an agent are made by AI models. No AI model has shown that it can trade at a profit reliably, " +
                "and a model can be wrong while it sounds certain. A model that did well in one test has done badly in another, " +
                "and a new version of a model can behave differently.",
            ]),
        new(
            "Nothing in this service is investment advice",
            [
                "Nothing in this service is investment advice. That holds for what an agent decides and the reasons its models " +
                "give, for the recommended team, for the figures, scores, and lessons of the experience screens, for the " +
                "platform-wide board, and for every text of these pages.",
                "Ecnanib does not know your finances or your goals, and recommends no trade, no asset, and no amount to you. " +
                "Whether you trade, and with how much, is your decision alone.",
            ]),
        new(
            "Past results do not predict future results",
            [
                "Past results do not predict future results. That holds for the results of your agents, for the track record " +
                "and the strength score of a model, and for the platform-wide board.",
                "Results in Demo do not predict results in Live: in Binance's test environment, prices and fills differ from " +
                "the real market. A result from a few trades says little.",
            ]),
    ];
}

/// <summary>One section of the risk disclosure.</summary>
/// <param name="Heading">What the section is about, as its heading.</param>
/// <param name="Paragraphs">What it says, a paragraph each.</param>
public sealed record DisclosureSection(string Heading, IReadOnlyList<string> Paragraphs);
