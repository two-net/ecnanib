using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Notifications;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.DecisionEngine;
using Ecnanib.Tests.Experience;
using Ecnanib.Tests.LiveUpdates;
using Ecnanib.Tests.Notifications;
using Ecnanib.WebUi;
using Ecnanib.WebUi.Live;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Accessibility;

/// <summary>
/// The application that the states of <see cref="AccessibilityRules"/> are states of: one
/// application on a database of its own, with its people and with everything that a screen can
/// show of the one who has it all. Every state but the two of a closed registration is reached
/// in this one application, so the screens are gone through once, and fast.
/// </summary>
/// <remarks>
/// <para>
/// Its people: a newcomer with nothing, a user whose address is not verified, an operator, a
/// user who turns two-factor authentication on, and the user. The user has keys for Claude and
/// for Binance in both environments, and four agents. One runs in Demo: it has an approved
/// cycle, a vetoed one, and a test cycle, an open position with its protective orders and a
/// price, recorded equity, more closed trades and written lessons than one page lists, and
/// calls and shadow trades that Experience scored. One is in Live, and stopped, with a cycle
/// and a lesson. One is a draft. And one runs with a cycle in progress. The user was told more
/// than one page of notifications lists, and three more users have trades that fill the
/// platform-wide board.
/// </para>
/// <para>
/// The clock stands at nine in the morning of 24 November 2026, UTC, and moves only for a code
/// of two-factor authentication. The prices and the cycles that run are in the hands of the
/// world: no page asks Binance for anything.
/// </para>
/// </remarks>
internal sealed class AccessibilityWorld : IDisposable
{
    /// <summary>The address of <see cref="Who.User"/>.</summary>
    public const string User = "ada@example.com";

    /// <summary>The address of <see cref="Who.Newcomer"/>.</summary>
    public const string Newcomer = "nora@example.com";

    /// <summary>The address of <see cref="Who.Unverified"/>.</summary>
    public const string Unverified = "una@example.com";

    /// <summary>The address of <see cref="Who.Operator"/>, which the configuration file names.</summary>
    public const string Operator = "olive@example.com";

    /// <summary>The address of <see cref="Who.Guarded"/>, and of her other browser.</summary>
    public const string Guarded = "tess@example.com";

    // One who registered and has not opened the link, and one who asked for a new password:
    // nobody signs in as either, and the pages of the links open with their tokens.
    private const string Registered = "victor@example.com";
    private const string Forgetful = "rita@example.com";

    private static readonly DateTimeOffset Morning = Feeding.Morning;

    private static readonly LivePrice Bitcoin = new(Market.Spot, TradingMode.Demo, "BTCUSDT", ExchangePriceKind.LastTraded);

    private readonly AccountsApplication _application;
    private readonly TestClock? _clock;
    private readonly IReadOnlyDictionary<string, string> _tokens;
    private readonly List<HttpClient> _browsers = [];

    // The authenticator app of the user who turns two-factor authentication on, once a page showed its key.
    private AuthenticatorApp? _app;

    private AccessibilityWorld(AccountsApplication application, TestClock? clock, IReadOnlyDictionary<string, string> tokens)
    {
        _application = application;
        _clock = clock;
        _tokens = tokens;
    }

    /// <summary>The application with its people and what they have: see the remarks of the class.</summary>
    /// <param name="listens">Whether the application listens on a port of this machine, for a browser that a test drives.</param>
    public static async Task<AccessibilityWorld> SeededAsync(bool listens = false)
    {
        var clock = new TestClock(Morning);
        var prices = new StandInPrices();
        var progress = new StandInProgress();

        // A third symbol, so that each agent of the user has one of its own.
        var market = new StandInMarketData
        {
            Symbols = (of, _) => of == Market.Spot
                ? StandInMarketData.Listed("BTCUSDT", "SOLUSDT", "ETHUSDT")
                : StandInMarketData.Listed("BTCUSDT"),
        };

        var application = AgentsApplication.With(
            market,
            clock,
            settings: file => file
                .WithJson("Application:Operators", $"[ \"{Operator}\" ]")
                .WithJson("Binance:Symbols", "[ \"BTCUSDT\", \"SOLUSDT\", \"ETHUSDT\" ]")

                // Few enough that what the world holds fills the strengths and the board.
                .With("Experience:MinSamples", 3)
                .With("Experience:Board:MinUsers", 3)
                .With("Experience:Board:MinTrades", 6),
            host: host => host.ConfigureServices(services =>
            {
                services.AddSingleton<IExchangePriceWatch>(prices);
                services.AddSingleton<ICycleProgress>(progress);
            }),
            listens: listens);

        try
        {
            return new AccessibilityWorld(application, clock, await SeedAsync(application, prices, progress));
        }
        catch
        {
            application.Dispose();

            throw;
        }
    }

    /// <summary>An application whose configuration file has registration closed, and nobody in it.</summary>
    /// <param name="listens">Whether the application listens on a port of this machine, for a browser that a test drives.</param>
    public static AccessibilityWorld WithRegistrationClosed(bool listens = false) =>
        new(new AccountsApplication(registrationOpen: false, listens: listens), clock: null, new Dictionary<string, string>());

    /// <summary>Where the application is reached by a browser, when it listens.</summary>
    public Uri ListeningAt => _application.ListeningAt;

    /// <summary>A browser of its own, with no cookie yet: nobody is signed in on it.</summary>
    public HttpClient CreateClient()
    {
        var browser = _application.CreateClient();
        _browsers.Add(browser);

        return browser;
    }

    /// <summary>
    /// Brings a person to their browser, which is new, by the pages that a person goes through:
    /// they sign in, and the user proves again who they are, so that the pages which ask for
    /// that open for them. The other browser of the user with two-factor authentication gives
    /// the password, and stays at the page that asks for the code.
    /// </summary>
    /// <param name="who">The person.</param>
    /// <param name="visit">Their browser.</param>
    /// <param name="timeZone">
    /// What the device of the person says its time zone is as they sign in, or null for a device
    /// that says none, whose pages are in UTC (NFR-USA-3).
    /// </param>
    public async Task EnterAsync(Who who, IVisit visit, string? timeZone = null)
    {
        var email = who switch
        {
            Who.Visitor => null,
            Who.Newcomer => Newcomer,
            Who.Unverified => Unverified,
            Who.User => User,
            Who.Operator => Operator,
            _ => Guarded,
        };

        if (email is null)
        {
            return;
        }

        Assert.True(_clock is not null, $"Nobody signs in where registration is closed: a state of that application is of a visitor, and not of {who}.");

        await visit.OpenAsync(SessionAuthentication.SignInPath);

        // The script of the times writes what the device says into the form. Here the test does.
        if (timeZone is null)
        {
            await visit.SubmitAsync("sign-in", ("Email", email), ("Password", AccountsApplication.Password));
        }
        else
        {
            await visit.SubmitAsync("sign-in", ("Email", email), ("Password", AccountsApplication.Password), ("TimeZone", timeZone));
        }

        var arrives = who == Who.AtTheCode ? SessionAuthentication.SignInCodePath : WebUiModule.HomePath;

        Assert.True(visit.Address == arrives, $"{who} signed in as {email} and ended up at {visit.Address}, and not at {arrives}.");

        if (who == Who.User)
        {
            await visit.OpenAsync(SessionAuthentication.ReauthenticationPath);
            await visit.SubmitAsync("confirm-identity", ("Password", AccountsApplication.Password));

            Assert.True(
                visit.Address == WebUiModule.HomePath,
                $"{who} confirmed who they are and ended up at {visit.Address}, and not at {WebUiModule.HomePath}.");
        }
    }

    /// <summary>The path and query of a state: its own path, or its route, with what the world has in place of each token.</summary>
    public string PathOf(ScreenState state) =>
        AddressOf(state.How is Reach.Standing standing ? standing.Address : state.Path ?? state.Route);

    /// <summary>An address with what the world has in place of each token, and of each parameter that stands for one.</summary>
    public string AddressOf(string address)
    {
        foreach (var (parameter, token) in AccessibilityRules.Defaults)
        {
            address = address.Replace(parameter, token, StringComparison.Ordinal);
        }

        foreach (var (token, value) in _tokens)
        {
            address = address.Replace($"{{{token}}}", value, StringComparison.Ordinal);
        }

        return address;
    }

    /// <summary>
    /// What is entered in a form, with a code in place of <see cref="AccessibilityRules.Code"/>
    /// and <see cref="AccessibilityRules.WrongCode"/>. The key of the app is read from the page
    /// that is open when it is the one that sets the app up, as its user scans it there. And the
    /// clock moves one step of the app on for each code: a code works once.
    /// </summary>
    public async Task<(string Name, string Value)[]> EnteredAsync((string Name, string Value)[] fields, IVisit visit)
    {
        if (!fields.Any(field => field.Value is AccessibilityRules.Code or AccessibilityRules.WrongCode))
        {
            return fields;
        }

        var page = await visit.MarkupAsync();

        if (page.Contains("class=\"setup-key\"", StringComparison.Ordinal))
        {
            _app = AuthenticatorApp.WithKeyOn(page);
        }

        if (_app is not { } app || _clock is not { } clock)
        {
            throw new VisitFailedException(
                $"A code is entered at {visit.Address} before a page showed the key of the app. Put the state after the one that sets the app up.");
        }

        clock.Set(clock.GetUtcNow().AddSeconds(30));

        var now = clock.GetUtcNow();

        return
        [
            .. fields.Select(field => field.Value switch
            {
                AccessibilityRules.Code => (field.Name, app.CodeAt(now)),
                AccessibilityRules.WrongCode => (field.Name, app.WrongCodeAt(now)),
                _ => field,
            }),
        ];
    }

    public void Dispose()
    {
        foreach (var browser in _browsers)
        {
            browser.Dispose();
        }

        _application.Dispose();
    }

    /// <summary>Gives the application its people and what they have.</summary>
    /// <returns>What each token of <see cref="AccessibilityRules.Tokens"/> stands for.</returns>
    private static async Task<IReadOnlyDictionary<string, string>> SeedAsync(
        AccountsApplication application, StandInPrices prices, StandInProgress progress)
    {
        var ada = await application.RegisterVerifiedAsync(User);
        await application.RegisterVerifiedAsync(Newcomer);
        await application.RegisterVerifiedAsync(Operator);
        await application.RegisterVerifiedAsync(Guarded);

        Assert.Equal(RegistrationOutcome.Accepted, (await application.RegisterAsync(Unverified)).Outcome);

        // The link that a registration sent, and the one that a forgotten password was sent.
        Assert.Equal(RegistrationOutcome.Accepted, (await application.RegisterAsync(Registered)).Outcome);
        var verification = AccountsApplication.TokenIn(application.Email.Sent[^1]);

        await application.RegisterVerifiedAsync(Forgetful);
        Assert.Equal(PasswordResetRequestOutcome.Accepted, await application.RequestResetAsync(Forgetful));
        var reset = AccountsApplication.TokenIn(application.Email.Sent[^1]);

        // The keys of the user: for Claude and for Binance in Demo as rows, and for Binance in
        // Live as a user stores one, on its page, which says what the key may do. Binance says
        // of it that it works from any address, which the list of the keys warns of.
        await application.GiveKeysAsync(ada.Id, Morning.AddDays(-7));

        application.Providers.BinanceAnswer = (environment, _) => environment == TradingMode.Live
            ? StandInProviders.TakenInLive with { IpRestricted = false }
            : StandInProviders.TakenInDemo;

        using (var browser = application.CreateClient())
        {
            await browser.SignInAsync(User);
            await browser.StoreLiveKeyAsync();
        }

        // The agent that has everything. It runs in Demo.
        var agent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-3), name: "Runner", state: AgentState.Running);
        var model = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning.AddDays(-3));

        // What its team decided: a plan that was approved, whose orders are of a position that
        // its stop-loss closed, one that was vetoed, and a cycle that its user ran as a test.
        var approved = await application.GiveCycleAsync(ada.Id, agent, Morning.AddHours(-3), CycleOutcome.Approved);
        var vetoed = await application.GiveCycleAsync(ada.Id, agent, Morning.AddHours(-2), CycleOutcome.Vetoed, orders: false);
        var test = await application.GiveCycleAsync(
            ada.Id, agent, Morning.AddMinutes(-150), CycleOutcome.Approved, orders: false, kind: CycleKind.Test);

        // One closed trade and its lesson more than a page lists, so the pager of each has a second page.
        var lessons = new List<Guid>();

        for (var number = 1; number <= 26; number++)
        {
            lessons.Add(await application.GiveLessonAsync(
                ada.Id,
                agent,
                Morning.AddHours(-4).AddMinutes(-10 * number),
                takeaway: $"Lesson {number}: wait for the retest of a broken level before entering.",
                resultInR: number % 3 == 0 ? -1m : 1.5m));
        }

        // The position that it holds now, with both protective orders on the exchange, and a price.
        var held = await application.GiveTradeAsync(ada.Id, agent, Morning, holdingSeconds: 600, closed: false);
        await application.GiveProtectionAsync(ada.Id, agent, held, OrderPurpose.StopLoss, 99_000m, Morning);
        await application.GiveProtectionAsync(ada.Id, agent, held, OrderPurpose.TakeProfit, 102_000m, Morning);
        prices.Say(Bitcoin, new WatchedPrice(100_250m, Morning.AddSeconds(-3)));

        // Its equity as it was recorded, which the curve draws.
        await application.GiveEquityAsync(
            ada.Id, agent, (Morning.AddDays(-2), 1000m), (Morning.AddDays(-1), 1040m), (Morning.AddHours(-1), 1009.3m));

        // What Experience scored of it, which the strengths of the roles are worked out from.
        await application.GiveCallsAsync(ada.Id, agent, Role.MarketAnalyst, model, Morning.AddHours(-5), matched: 3, missed: 1);
        await application.GiveShadowTradeAsync(ada.Id, agent, model, Morning.AddHours(-9), resultInR: -1.2m);
        await application.GiveShadowTradeAsync(ada.Id, agent, model, Morning.AddHours(-8), resultInR: -0.8m);
        await application.GiveShadowTradeAsync(ada.Id, agent, model, Morning.AddHours(-7), resultInR: 1.5m);

        // The agent in Live, which is stopped, with a cycle and a lesson.
        var inLive = await application.GiveAgentAsync(
            ada.Id, Morning.AddDays(-2), name: "Trader", state: AgentState.Stopped, symbol: "SOLUSDT", mode: TradingMode.Live);
        await application.GiveCycleAsync(ada.Id, inLive, Morning.AddHours(-6), orders: false);
        await application.GiveLessonAsync(ada.Id, inLive, Morning.AddHours(-7));

        // A draft, which has nothing recorded.
        var draft = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1), name: "Sketch", symbol: "ETHUSDT");

        // And an agent with a cycle in progress: its analysts are at work.
        var busy = await application.GiveAgentAsync(
            ada.Id, Morning.AddDays(-1), name: "Scout", state: AgentState.Running, symbol: "SOLUSDT");
        progress.Runs(
            ada.Id,
            busy,
            Feeding.Cycle(CycleStep.Settling, CycleStep.DataPackage, CycleStep.Analysts) with
            {
                Roles = Feeding.Roles(RoleState.Done, RoleState.Working),
            });

        // What the user was told, and has not read: one more than a page lists, about the agent
        // in Demo, about the one in Live, and about the account.
        for (var number = 1; number <= 24; number++)
        {
            await application.GiveNotificationAsync(ada.Id, agent, Morning.AddMinutes(-number), mode: TradingMode.Demo);
        }

        await application.GiveNotificationAsync(
            ada.Id,
            inLive,
            Morning.AddMinutes(-30),
            content: "The agent Trader was halted: it reached its daily loss limit.",
            type: NotificationType.AgentHalted,
            mode: TradingMode.Live);
        await application.GiveNotificationAsync(
            ada.Id,
            agentId: null,
            Morning.AddMinutes(-40),
            content: "Your Gemini key is no longer accepted by its provider. Replace it to go on.",
            type: NotificationType.AttentionRequired);

        // A model of another provider in the catalog, and the trades of three more users, which
        // fill the platform-wide board.
        await application.GiveModelAsync("gemini", "gemini-test-model", Morning.AddDays(-3), displayName: "Gemini Test");
        await application.GiveContributorsAsync(users: 3, tradesEach: 2, Morning.AddHours(-3), BoardContributors.TeamOf(model));

        var tokens = new Dictionary<string, string>
        {
            ["Agent"] = agent.ToString(),
            ["LiveAgent"] = inLive.ToString(),
            ["DraftAgent"] = draft.ToString(),
            ["BusyAgent"] = busy.ToString(),
            ["Cycle"] = approved.ToString(),
            ["VetoedCycle"] = vetoed.ToString(),
            ["TestCycle"] = test.ToString(),
            ["Lesson"] = lessons[0].ToString(),
            ["Model"] = model.ToString(),
            ["VerificationToken"] = Uri.EscapeDataString(verification),
            ["ResetToken"] = Uri.EscapeDataString(reset),
        };

        // The list of the rules is what a path may name, and the world fills every one of them.
        Assert.Equal(AccessibilityRules.Tokens.Order(StringComparer.Ordinal), tokens.Keys.Order(StringComparer.Ordinal));

        return tokens;
    }
}
