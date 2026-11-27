using Ecnanib.Agents;
using Ecnanib.ExchangeGateway;
using Ecnanib.Notifications;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.ExchangeGateway;
using Ecnanib.Tests.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// A Binance outage pauses entries and raises an alert (NFR-REL-4). The gateway says where
/// Binance is out. Once an outage has lasted for as long as the configuration file says, each
/// user who has an agent that runs there is told, once, and is told again when Binance answers.
/// See "A Binance outage" in docs/reliability.md.
/// </summary>
public class ExchangeOutageAlertTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";
    private const string Linus = "linus@example.com";
    private const string Mary = "mary@example.com";

    private const string Area = "Ecnanib.Agents.ExchangeOutageAlerts";

    private const string ToldOfOutage = "{UserCount} users whose agents run on {Market} {Environment} were told that Binance is out there";

    private const string ToldOfAnswer = "{UserCount} users whose agents run on {Market} {Environment} were told that Binance answers again there";

    private const string NotTold = "The user {UserId} could not be told of the outage of Binance on {Market} {Environment}";

    private const string IsOut =
        "Binance has not answered on Futures (Demo) since 09:30 UTC. Your agents there open no new position until it answers " +
        "again. Open positions stay as they are, with their protective orders, which rest at Binance. There is nothing for you " +
        "to do: you are told when Binance answers again.";

    private const string AnswersAgain =
        "Binance answers again on Futures (Demo). Your agents there open new positions again, from the next close of a candle on.";

    private static readonly DateTimeOffset Morning = new(2026, 11, 27, 9, 30, 0, TimeSpan.Zero);

    // What the file of the tests says: an outage is told of when it has lasted a minute and a half.
    private static readonly TimeSpan LastsFor = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task The_users_whose_agents_run_where_binance_is_out_are_told_once_when_it_has_lasted()
    {
        using var world = new World();
        var application = world.Application;

        // Ada has two agents that run on Futures in Demo. Grace runs one on Spot, Mary one on
        // Futures in Live, and the agent of Linus on Futures in Demo is paused.
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var linus = await application.RegisterVerifiedAsync(Linus);
        var mary = await application.RegisterVerifiedAsync(Mary);

        await world.GiveAgentAsync(ada.Id, "First", Market.Futures, TradingMode.Demo);
        await world.GiveAgentAsync(ada.Id, "Second", Market.Futures, TradingMode.Demo, symbol: "SOLUSDT");
        await world.GiveAgentAsync(grace.Id, "Spot", Market.Spot, TradingMode.Demo);
        await world.GiveAgentAsync(mary.Id, "Live", Market.Futures, TradingMode.Live);
        await world.GiveAgentAsync(linus.Id, "Paused", Market.Futures, TradingMode.Demo, AgentState.Paused);

        // Binance goes out on Futures in Demo. A second before it has lasted, nobody is told: a
        // connection that broke for a moment is told to nobody.
        world.Outages.Since[(Market.Futures, TradingMode.Demo)] = Morning;
        world.Clock.Set(Morning + LastsFor - TimeSpan.FromSeconds(1));
        await application.TellOfOutagesAsync();

        Assert.Empty(await world.AllToldAsync());

        // Then Ada is told: once for both her agents, about no agent, and about Demo.
        world.Clock.Set(Morning + LastsFor);
        await application.TellOfOutagesAsync();

        var told = Assert.Single(await world.AllToldAsync());

        Assert.Equal((ada.Id, NotificationType.ExchangeOutage, null, TradingMode.Demo, IsOut), (told.UserId, told.Type, told.AgentId, told.Mode, told.Content));

        // It is a critical event: a user who opted in is sent an email for it (FR-NTF-3).
        Assert.True(NotificationTypes.IsCritical(NotificationType.ExchangeOutage));
        Assert.Equal("Exchange outage", NotificationTypes.Describe(NotificationType.ExchangeOutage));

        // The outage goes on, and nobody is told of it a second time.
        world.Clock.Set(Morning.AddHours(2));
        await application.TellOfOutagesAsync();
        await application.TellOfOutagesAsync();

        Assert.Single(await world.AllToldAsync());

        var said = Assert.Single(application.Events(), written => written.Area == Area);

        Assert.Equal((ToldOfOutage, "Information"), (said.Template, said.Level));
        Assert.Equal(("1", "Futures", "Demo"), (said["UserCount"].ToString(), said.Text("Market"), said.Text("Environment")));
    }

    [Fact]
    public async Task They_are_told_when_binance_answers_again_and_nobody_is_who_was_told_of_no_outage()
    {
        using var world = new World();
        var application = world.Application;
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);

        await world.GiveAgentAsync(ada.Id, "Futures", Market.Futures, TradingMode.Demo);
        await world.GiveAgentAsync(grace.Id, "Spot", Market.Spot, TradingMode.Demo);

        // Futures in Demo is out for long enough to be told of. Spot in Demo is out as well, and
        // answers again before anyone was told.
        world.Outages.Since[(Market.Futures, TradingMode.Demo)] = Morning;
        world.Outages.Since[(Market.Spot, TradingMode.Demo)] = Morning.AddSeconds(60);
        world.Clock.Set(Morning + LastsFor);
        await application.TellOfOutagesAsync();

        world.Outages.Since.Remove((Market.Spot, TradingMode.Demo));
        world.Clock.Set(Morning.AddMinutes(3));
        await application.TellOfOutagesAsync();

        Assert.Equal([IsOut], (await world.AllToldAsync()).Select(told => told.Content));

        // Binance answers on Futures again: Ada is told, once, the same way.
        world.Outages.Since.Remove((Market.Futures, TradingMode.Demo));
        world.Clock.Set(Morning.AddMinutes(20));
        await application.TellOfOutagesAsync();
        await application.TellOfOutagesAsync();

        Assert.Equal(
            [(ada.Id, NotificationType.ExchangeOutage, TradingMode.Demo, IsOut), (ada.Id, NotificationType.ExchangeOutage, TradingMode.Demo, AnswersAgain)],
            (await world.AllToldAsync()).Select(told => (told.UserId, told.Type, told.Mode!.Value, told.Content)));

        // Grace was told of nothing, so she is told of no end of it.
        Assert.Empty(await application.ToldAsync(grace.Id));
        Assert.Equal(
            [ToldOfOutage, ToldOfAnswer], application.Events().Where(written => written.Area == Area).Select(written => written.Template));

        // The next outage there is told of anew.
        world.Outages.Since[(Market.Futures, TradingMode.Demo)] = Morning.AddMinutes(30);
        world.Clock.Set(Morning.AddMinutes(32));
        await application.TellOfOutagesAsync();

        Assert.Equal(3, (await application.ToldAsync(ada.Id)).Count);
    }

    [Fact]
    public async Task Each_market_and_each_environment_is_told_of_on_its_own()
    {
        using var world = new World();
        var application = world.Application;
        var ada = await application.RegisterVerifiedAsync(Ada);

        // One user with an agent that runs on each of the four.
        foreach (var (market, mode) in BinanceCalls.Pairs)
        {
            await world.GiveAgentAsync(ada.Id, $"{market} {mode}", market, mode);
        }

        // Binance is out everywhere: the user is told four times, each about its environment.
        foreach (var pair in BinanceCalls.Pairs)
        {
            world.Outages.Since[pair] = Morning;
        }

        world.Clock.Set(Morning + LastsFor);
        await application.TellOfOutagesAsync();

        Assert.Equal(
            ["Futures (Demo)", "Futures (Live)", "Spot (Demo)", "Spot (Live)"],
            (await world.AllToldAsync())
                .Select(told => $"{(told.Content.Contains("Futures", StringComparison.Ordinal) ? "Futures" : "Spot")} ({told.Mode})")
                .Order());

        // One of them answers again: that one is told to be over, and the other three are not.
        world.Outages.Since.Remove((Market.Spot, TradingMode.Live));
        world.Clock.Set(Morning.AddMinutes(10));
        await application.TellOfOutagesAsync();

        var over = Assert.Single(await world.AllToldAsync(), told => told.Content.StartsWith("Binance answers again", StringComparison.Ordinal));

        Assert.Equal(("Binance answers again on Spot (Live).", TradingMode.Live), (over.Content[..37], over.Mode));
    }

    [Fact]
    public async Task A_user_who_cannot_be_told_keeps_no_other_user_from_being_told()
    {
        Guid? fails = null;
        using var world = new World(failsFor: userId => userId == fails);
        var application = world.Application;
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);

        await world.GiveAgentAsync(ada.Id, "Futures", Market.Futures, TradingMode.Demo);
        await world.GiveAgentAsync(grace.Id, "Futures", Market.Futures, TradingMode.Demo);

        // Whichever of the two comes first cannot be told.
        fails = new[] { ada.Id, grace.Id }.Min();
        world.Outages.Since[(Market.Futures, TradingMode.Demo)] = Morning;
        world.Clock.Set(Morning + LastsFor);
        await application.TellOfOutagesAsync();

        // The other is, and the run of the job does not fail for it. It is an error in the log.
        var told = Assert.Single(await world.AllToldAsync());

        Assert.Equal(new[] { ada.Id, grace.Id }.Max(), told.UserId);

        var said = application.Events().Where(written => written.Area == Area).ToList();

        Assert.Equal([(NotTold, "Error"), (ToldOfOutage, "Information")], said.Select(written => (written.Template, written.Level)));
        Assert.Equal((fails.ToString(), "1"), (said[0].Text("UserId"), said[1]["UserCount"].ToString()));
    }

    /// <summary>
    /// The application with a watch on outages that says what the test says, a clock that the
    /// test moves, and the job that tells of outages, which the test runs.
    /// </summary>
    private sealed class World : IDisposable
    {
        public World(Func<Guid, bool>? failsFor = null)
        {
            Application = new AccountsApplication(
                clock: Clock,
                host: host => host.ConfigureServices(services =>
                {
                    services.AddSingleton<IExchangeOutages>(Outages);

                    if (failsFor is not null)
                    {
                        var real = services.Last(service => service.ServiceType == typeof(INotifications)).ImplementationType!;

                        services.AddSingleton<INotifications>(provider =>
                            new NotificationsThatFail((INotifications)ActivatorUtilities.CreateInstance(provider, real), failsFor));
                    }
                }));
        }

        public TestClock Clock { get; } = new(Morning);

        public OutagesOfTheTest Outages { get; } = new();

        public AccountsApplication Application { get; }

        public Task<Guid> GiveAgentAsync(
            Guid userId, string name, Market market, TradingMode mode, AgentState state = AgentState.Running, string symbol = "BTCUSDT") =>
            Application.GiveAgentAsync(
                userId,
                Morning.AddHours(-1),
                name: name,
                state: state,
                market: market,
                symbol: symbol,
                mode: mode,
                cause: state == AgentState.Running ? AgentStateCause.UserStarted : AgentStateCause.UserPaused,
                team: false);

        /// <summary>Every notification of every user, in the order they were raised in.</summary>
        public async Task<IReadOnlyList<Notification>> AllToldAsync()
        {
            await using var context = await Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

            var stored = await context.Set<Notification>().AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);

            return [.. stored.OrderBy(notification => notification.CreatedAt).ThenBy(notification => notification.Content, StringComparer.Ordinal)];
        }

        public void Dispose() => Application.Dispose();
    }

    /// <summary>Stands for the watch of the gateway: Binance is out where the test says, since when it says.</summary>
    private sealed class OutagesOfTheTest : IExchangeOutages
    {
        public Dictionary<(Market Market, TradingMode Mode), DateTimeOffset> Since { get; } = [];

        public IReadOnlyList<ExchangeAnswers> Answers() =>
        [
            .. BinanceCalls.Pairs.Select(pair => new ExchangeAnswers(
                pair.Market,
                pair.Mode,
                Unanswered: Since.ContainsKey(pair) ? 50 : 0,
                OutageSince: Since.TryGetValue(pair, out var since) ? since : null,
                AnsweredAt: null)),
        ];

        public bool EntriesPaused(Market market, TradingMode mode) => Since.ContainsKey((market, mode));
    }

    /// <summary>Notifications that cannot be raised for some users, as when a row cannot be written.</summary>
    private sealed class NotificationsThatFail(INotifications notifications, Func<Guid, bool> failsFor) : INotifications
    {
        public Task RaiseAsync(
            Guid userId, NotificationType type, Guid? agentId, TradingMode? mode, string content, CancellationToken cancellationToken) =>
            failsFor(userId)
                ? throw new InvalidOperationException("The notification could not be written.")
                : notifications.RaiseAsync(userId, type, agentId, mode, content, cancellationToken);

        public Task RaiseInAsync(
            EcnanibDbContext context,
            Guid userId,
            NotificationType type,
            Guid? agentId,
            TradingMode? mode,
            string content,
            CancellationToken cancellationToken) =>
            notifications.RaiseInAsync(context, userId, type, agentId, mode, content, cancellationToken);

        public Task<bool> RaiseOnceAsync(
            Guid userId,
            NotificationType type,
            Guid agentId,
            TradingMode mode,
            string content,
            TimeSpan within,
            CancellationToken cancellationToken) =>
            notifications.RaiseOnceAsync(userId, type, agentId, mode, content, within, cancellationToken);

        public Task<IReadOnlyList<NotificationView>> UnreadOfAgentAsync(HttpContext request, Guid agentId) =>
            notifications.UnreadOfAgentAsync(request, agentId);

        public Task<NotificationList> OfUserAsync(HttpContext request, int page) => notifications.OfUserAsync(request, page);

        public Task<UnreadByMode> UnreadByModeAsync(HttpContext request) => notifications.UnreadByModeAsync(request);

        public Task<int> UnreadCountAsync(HttpContext request) => notifications.UnreadCountAsync(request);

        public Task MarkReadAsync(HttpContext request, Guid notificationId) => notifications.MarkReadAsync(request, notificationId);

        public Task<int> MarkAllReadAsync(HttpContext request, Guid through) => notifications.MarkAllReadAsync(request, through);
    }
}
