using System.Text.RegularExpressions;
using Ecnanib.DecisionEngine;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Settings;
using static Ecnanib.Tests.DecisionEngine.ComposedPrompts;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Prompts never contain secrets, email addresses, account identifiers, or other personal data
/// (FR-PRM-9), so what an AI provider is sent holds none (NFR-SEC-8). The prompts of a user who
/// has an address, keys, and agents are composed here and checked for all of it, and a prompt
/// that would hold any is not composed, however a part came to hold it.
/// </summary>
public partial class PromptPrivacyTests
{
    private const string Ada = "ada.lovelace@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A data package as a cycle writes one: candles, indicators, open orders, and what Futures
    /// adds. <see cref="DataPackageTests"/> composes the packages that the application builds.
    /// </summary>
    private const string Data =
        "Closed 4H candles: 2, oldest first, through 2026-11-12T08:00Z\n" +
        "Each line: close time (UTC), open, high, low, close, volume\n" +
        "2026-11-12T04:00Z, 61000.5, 61420, 60880.25, 61310, 1842.337\n" +
        "2026-11-12T08:00Z, 61310, 61975.5, 61200, 61890.75, 2210.004\n" +
        "Indicators at that close, computed by the system from 498 closed 4H candles:\n" +
        "- Moving averages of the close: SMA20 61122.48, SMA50 60310.9, SMA200 59876.12, EMA20 61190.33, EMA50 60402.71\n" +
        "- Relative strength: RSI14 58.2\n" +
        "- Average true range: ATR14 512.75\n" +
        "- Volume: 2210.004 in the latest candle, average of 20 candles 1930.11\n" +
        "\n" +
        "Open orders:\n" +
        "- stop-loss: sell 0.015 at 59500\n" +
        "\n" +
        "Futures: mark price 61888.1, funding rate 0.01%, next funding 2026-11-12T16:00Z, open interest 82450.5 BTC";

    /// <summary>Lessons as a model writes them after a closed position (FR-EXP-6).</summary>
    private static readonly string[] Lessons =
    [
        "Setup: a pullback to the 20-period average in an uptrend. Expected: a continuation to the prior high. " +
        "What happened: the stop was hit after a funding spike. Takeaway: wait for the retest to hold for one closed candle.",
        "Setup: a break of a range on rising volume. Expected: a move of one range height. What happened: the target was reached. " +
        "Takeaway: a break that closes outside the range on volume is worth the full risk.",
    ];

    /// <summary>A key in the format Binance issues: 64 letters and digits.</summary>
    private static readonly string ExchangeKey = string.Concat(Enumerable.Repeat("aB3xY9zQ", 8));

    /// <summary>
    /// What a prompt never holds, each with the kind that it is found as: addresses, the ids of
    /// accounts, agents, and keys, keys and tokens in the formats that they are issued in, and
    /// the secrets of the configuration file of the tests.
    /// </summary>
    public static TheoryData<string, PromptLeak> Leaks => new()
    {
        { Ada, PromptLeak.EmailAddress },
        { "grace.hopper+alerts@mail.example.org", PromptLeak.EmailAddress },
        { "019a7c3e-5b1f-7d2a-9c4e-1f2a3b4c5d6e", PromptLeak.Identifier },
        { "019A7C3E-5B1F-7D2A-9C4E-1F2A3B4C5D6E", PromptLeak.Identifier },
        { "sk-ant-api03-abcdefghijklmnop", PromptLeak.Secret },
        { "xai-abcdefghijklmnopqrstuvwxyz", PromptLeak.Secret },
        { "AIzaSyA1234567890abcdefghijklmnopqrstuvw", PromptLeak.Secret },
        { "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk", PromptLeak.Secret },
        { ExchangeKey, PromptLeak.Secret },
        { SettingsJson.MasterKey, PromptLeak.Secret },
        { "email-password", PromptLeak.Secret },
    };

    /// <summary>
    /// What a prompt about a market may well hold, and what is no secret, no address, and no
    /// identifier: none of it keeps a prompt from being composed.
    /// </summary>
    public static TheoryData<string> Harmless =>
    [
        "The basic structure held: basic resistance at 61000 was retested, and the bearer of bad news was the funding rate.",
        "Takeaway: the key = patience, the token = time, and the secret = position size.",
        "Posts by @whale_alert and @cz_binance moved the price within minutes.",
        "Bought 0.015 @ 61000.5, with BTC@61000 and ETH@3050.25 quoted at the close.",
        "A transfer of 4000 BTC, hash 5c504ed432cb51138bcf09aa5e8a410dd4a1e204ef84bfed1be16dfba1b22060, reached an exchange.",
        "The exploit, 0x5c504ed432cb51138bcf09aa5e8a410dd4a1e204ef84bfed1be16dfba1b22060, drained 0x742d35Cc6634C0532925a3b844Bc454e4438f44e.",
        "A wallet, bc1qxy2kgdygjrsqtzq2n0yrf2493p83kkfjhx0wlh, moved coins that had not moved since 2013.",
        "https://news.example/markets/bitcoin-etf-approval-sends-the-price-to-a-new-all-time-high-above-100000",
        "Order 4242424242 was filled in 3 parts at 61000.5, 61000.75, and 61001.",
    ];

    [Fact]
    public async Task A_composed_prompt_holds_nothing_of_the_account_the_agents_or_the_keys_behind_it()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        var user = await application.RegisterVerifiedAsync(Ada);

        // A key for each AI provider, and a Binance credential: everything that the user has
        // stored, and that the Decision Engine can reach.
        await application.GiveKeysAsync(user.Id, Morning);

        foreach (var provider in new[] { "gemini", "gpt", "grok" })
        {
            await application.GiveAiKeyAsync(user.Id, provider, Morning, accessibleModels: null);
        }

        // Two agents, on either market, whose names say who their user is.
        var swing = await StoredAsync(application, await application.GiveAgentAsync(
            user.Id, Morning, name: "Lovelace family savings", market: Market.Futures, timeframe: Timeframe.FourHours));
        var scalp = await StoredAsync(application, await application.GiveAgentAsync(
            user.Id, Morning, name: "Pension of A. King", market: Market.Spot, symbol: "ETHUSDT", timeframe: Timeframe.FifteenMinutes));

        var aiKeys = StoredAiCredential.All(application.Database);
        var exchangeKeys = StoredExchangeCredential.All(application.Database);

        Assert.Equal(["claude", "gemini", "gpt", "grok"], aiKeys.Select(key => key.Provider));
        Assert.Single(exchangeKeys);

        // What no prompt holds: the address and what it is made of, the password and its hash,
        // the names of the agents, every id of the account, the agents, and the keys, and the
        // keys themselves, in every form their bytes could be written in.
        var owner = user.Id.ToString("N");
        Guid[] ids = [user.Id, swing.Id, scalp.Id, .. aiKeys.Select(key => key.Id), .. exchangeKeys.Select(key => key.Id)];

        string[] never =
        [
            user.Email,
            "lovelace",
            "example.com",
            user.PasswordHash,
            AccountsApplication.Password,
            swing.Name,
            scalp.Name,
            "A. King",
            .. ids.SelectMany(id => new[] { id.ToString("D"), id.ToString("N") }),
            .. new[] { "claude", "gemini", "gpt", "grok", "demo", "seal" }.SelectMany(key => Keys.FormsOf(Keys.Of(owner, key))),
        ];

        var composed = 0;

        foreach (var (agent, position) in new[]
        {
            (swing, new PositionContext(PositionSide.Short, 61000m, 0.015m, 62500m, 58000m)),
            (scalp, (PositionContext?)null),
        })
        {
            // The context of the agent as it is stored: its own settings, and nothing else of its row.
            var parts = new PromptParts(AgentContext.Of(agent, equity: 1012.5m, position), Data, Lessons);

            foreach (var role in Enum.GetValues<Role>())
            {
                var prompt = (await application.PromptComposition.ComposeAsync(role, parts, TestContext.Current.CancellationToken)).Text;

                // It is the prompt of this agent: its symbol, its market data, and its lessons.
                Assert.Contains($"Symbol: {agent.Symbol}, priced in USDT\n", prompt, StringComparison.Ordinal);
                Assert.Contains(Data, prompt, StringComparison.Ordinal);
                Assert.Contains(Lessons[1], prompt, StringComparison.Ordinal);

                Assert.All(never, value => Assert.DoesNotContain(value, prompt, StringComparison.OrdinalIgnoreCase));

                // And nothing that has the shape of an address or of an id, whoever's it is.
                Assert.DoesNotMatch(AnAddress(), prompt);
                Assert.DoesNotMatch(AnId(), prompt);

                composed++;
            }
        }

        Assert.Equal(8, composed);
    }

    [Theory]
    [MemberData(nameof(Leaks))]
    public async Task A_prompt_that_would_hold_a_secret_an_address_or_an_identifier_is_not_composed(string leak, PromptLeak kind)
    {
        using var application = new AccountsApplication();
        var composition = application.PromptComposition;
        var context = Context(Market.Futures, Timeframe.FourHours);

        // Wherever it is handed over: in the data package, in a lesson, or in the context of
        // the agent, whose symbol and whose asset are text.
        (string Part, PromptParts Parts)[] handed =
        [
            ("Data package", new PromptParts(context, $"{Data}\nNotable events: a post names {leak} as its source.", Lessons)),
            ("Recalled lessons", new PromptParts(context, Data, [Lessons[0], $"Takeaway: ask {leak} first."])),
            ("Agent context", new PromptParts(context with { Symbol = leak }, Data, Lessons)),
            ("Agent context", new PromptParts(context with { QuoteAsset = leak }, Data, Lessons)),
        ];

        foreach (var (part, parts) in handed)
        {
            foreach (var role in Enum.GetValues<Role>())
            {
                var refused = await Assert.ThrowsAsync<PromptRefusedException>(() =>
                    composition.ComposeAsync(role, parts, TestContext.Current.CancellationToken));

                // It says where and what kind of thing, and never what was found: the message
                // of an error is logged.
                Assert.Equal((part, kind), (refused.Part, refused.Holds));
                Assert.Contains($"\"{part}\"", refused.Message, StringComparison.Ordinal);
                Assert.Contains("(FR-PRM-9)", refused.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(leak, refused.ToString(), StringComparison.OrdinalIgnoreCase);
            }
        }

        // Without it, the same parts are composed.
        var prompt = await composition.ComposeAsync(Role.Strategist, new PromptParts(context, Data, Lessons), TestContext.Current.CancellationToken);

        Assert.Equal(Headings, HeadingsOf(prompt.Text));
    }

    [Theory]
    [MemberData(nameof(Harmless))]
    public async Task What_is_no_secret_no_address_and_no_identifier_keeps_no_prompt_from_being_composed(string harmless)
    {
        using var application = new AccountsApplication();

        // In the data package and in a lesson, where text that a model wrote is placed.
        var prompt = await application.PromptComposition.ComposeAsync(
            Role.Strategist,
            new PromptParts(Context(Market.Spot, Timeframe.OneHour), $"{Data}\nNotable events: {harmless}", [harmless]),
            TestContext.Current.CancellationToken);

        Assert.EndsWith($"Notable events: {harmless}", PartOf(prompt.Text, "Data package"), StringComparison.Ordinal);
        Assert.Equal($"1. {harmless}", PartOf(prompt.Text, "Recalled lessons"));
    }

    // Looser than what the application goes by, and its own: anything around an at sign that
    // has a dot after it, and the two ways an id is written.
    [GeneratedRegex(@"[^\s@]+@[^\s@]+\.[^\s@]+")]
    private static partial Regex AnAddress();

    [GeneratedRegex("[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}|[0-9A-Fa-f]{32}")]
    private static partial Regex AnId();
}
