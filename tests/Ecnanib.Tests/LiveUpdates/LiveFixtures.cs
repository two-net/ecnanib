using System.Security.Claims;
using System.Text.RegularExpressions;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.LiveUpdates;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Logging;
using Ecnanib.WebUi.Live;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ecnanib.Tests.LiveUpdates;

/// <summary>What follows the signals of a user in a test, and keeps what it heard, in the order that it heard it.</summary>
internal sealed class Following : IDisposable
{
    private readonly List<LiveSignal> _heard = [];
    private readonly IDisposable _following;

    /// <param name="signals">The signals of the application.</param>
    /// <param name="userId">The user that is followed.</param>
    /// <param name="since">What the test read at, in place of now.</param>
    public Following(ILiveSignals signals, Guid userId, long? since = null)
    {
        _following = signals.Follow(
            userId,
            since ?? signals.Mark(),
            signal =>
            {
                lock (_heard)
                {
                    _heard.Add(signal);
                }
            });
    }

    public IReadOnlyList<LiveSignal> Heard
    {
        get
        {
            lock (_heard)
            {
                return [.. _heard];
            }
        }
    }

    /// <summary>Waits for a signal that the application hears in its own time, as the database says it.</summary>
    public Task HearsAsync(LiveSubject subject, Guid? agentId) =>
        Wait.UntilAsync(
            () => Heard.Contains(new LiveSignal(subject, agentId)),
            $"a signal about {subject} of the agent {agentId?.ToString() ?? "(none)"}. Heard: {string.Join(", ", Heard)}");

    /// <summary>Forgets what was heard so far.</summary>
    public void Forget()
    {
        lock (_heard)
        {
            _heard.Clear();
        }
    }

    public void Dispose() => _following.Dispose();
}

internal static class LiveFixtures
{
    /// <summary>What a follower is told when it may have missed something.</summary>
    public static readonly LiveSignal Missed = new(LiveSubject.Missed, AgentId: null);

    /// <summary>
    /// Waits until the application listens to its database. It begins to with the first that
    /// follows, in its own time, and tells every follower then that it may have missed
    /// something: a test that follows only afterwards hears what its own changes cause, and
    /// nothing else.
    /// </summary>
    public static async Task ListensAsync(this ILiveSignals signals)
    {
        // What says that it read before anything was heard is told that it missed something,
        // as soon as the application listens.
        using var first = new Following(signals, Guid.NewGuid(), since: -1);

        await Wait.UntilAsync(() => first.Heard.Contains(Missed), "the application to listen to what its database says");
    }
}

/// <summary>
/// The team at work as a page has it (FR-ANI-1): the cards of its roles, and the stages that
/// the work was handed to. A test reads a page as a person does, with
/// <see cref="Agents.AgentBrowser.Read"/>, before it asks.
/// </summary>
internal static partial class TeamPipeline
{
    /// <summary>The pipelines of a page, each as it is written: one for each cycle, or one of the team at rest.</summary>
    public static IReadOnlyList<string> On(string page) => [.. Pipeline().Matches(page).Select(pipeline => pipeline.Value)];

    /// <summary>
    /// The cards of a pipeline, in the order that they are written in: the role, its model with
    /// the provider, what the role does as the page says it, and as the stylesheet is told it.
    /// </summary>
    public static IReadOnlyList<(string Role, string Model, string State, string Class)> CardsOf(string pipeline) =>
    [
        .. Card().Matches(pipeline).Select(card => (
            card.Groups["role"].Value.Trim(), card.Groups["model"].Value.Trim(), card.Groups["state"].Value.Trim(), card.Groups["class"].Value)),
    ];

    /// <summary>Whether the work was handed to each of the three stages of a pipeline: the first never says so.</summary>
    public static IReadOnlyList<bool> HandedOf(string pipeline) =>
        [.. Stage().Matches(pipeline).Select(stage => stage.Groups[1].Success)];

    [GeneratedRegex("<ol class=\"team-pipeline\" aria-labelledby=\"team-heading\" data-moves>.*?</ol>", RegexOptions.Singleline)]
    private static partial Regex Pipeline();

    [GeneratedRegex(
        "<div class=\"pipeline-role (?<class>role-[a-z]+)\" aria-atomic=\"true\">\\s*<span class=\"role-name\">(?<role>.*?)</span>\\s*" +
        "<span class=\"role-model\">(?<model>.*?)</span>\\s*" +
        "<span class=\"role-state\"><span class=\"role-marker\" aria-hidden=\"true\"></span>(?<state>.*?)</span>\\s*</div>",
        RegexOptions.Singleline)]
    private static partial Regex Card();

    [GeneratedRegex("<li class=\"pipeline-stage( stage-handed)?\">")]
    private static partial Regex Stage();
}

/// <summary>Stands for a page that follows what changes: it keeps what its feed asked of it.</summary>
internal sealed class StandInPage(string address = "/agents") : ILivePage
{
    private int _shown;
    private int _asked;
    private int _left;
    private Exception? _failed;

    /// <summary>Whether the connection of the page is up, which a test says.</summary>
    public bool IsConnected { get; set; } = true;

    public string Address { get; set; } = address;

    /// <summary>How often the feed had the parts of the page written again.</summary>
    public int Shown => Volatile.Read(ref _shown);

    /// <summary>How often the feed had the page ask for itself again.</summary>
    public int Asked => Volatile.Read(ref _asked);

    /// <summary>How often the feed had the browser load the page anew.</summary>
    public int Left => Volatile.Read(ref _left);

    /// <summary>The error that the feed failed with, when it did.</summary>
    public Exception? Failed => Volatile.Read(ref _failed);

    public Task ShowAsync()
    {
        Interlocked.Increment(ref _shown);

        return Task.CompletedTask;
    }

    public Task AskAgainAsync()
    {
        Interlocked.Increment(ref _asked);

        return Task.CompletedTask;
    }

    public Task LeaveAsync()
    {
        Interlocked.Increment(ref _left);

        return Task.CompletedTask;
    }

    public Task FailAsync(Exception error)
    {
        Volatile.Write(ref _failed, error);

        return Task.CompletedTask;
    }
}

/// <summary>Stands for the watch of the prices: a test says each price, and sees what is followed.</summary>
internal sealed class StandInPrices : IExchangePriceWatch
{
    private readonly Lock _gate = new();
    private readonly List<(LivePrice Of, Action<WatchedPrice> Heard)> _followers = [];
    private readonly Dictionary<LivePrice, WatchedPrice> _latest = [];

    /// <summary>The prices that are followed now, one for each follower.</summary>
    public IReadOnlyList<LivePrice> Followed
    {
        get
        {
            lock (_gate)
            {
                return [.. _followers.Select(follower => follower.Of)];
            }
        }
    }

    /// <summary>Says a price, as the watch does when it has read one: to everyone who follows it.</summary>
    public void Say(LivePrice of, WatchedPrice price)
    {
        Action<WatchedPrice>[] told;

        lock (_gate)
        {
            _latest[of] = price;
            told = [.. _followers.Where(follower => follower.Of == of).Select(follower => follower.Heard)];
        }

        foreach (var heard in told)
        {
            heard(price);
        }
    }

    public IDisposable Follow(Market market, TradingMode mode, string symbol, ExchangePriceKind kind, Action<WatchedPrice> heard)
    {
        var follower = (new LivePrice(market, mode, symbol, kind), heard);

        lock (_gate)
        {
            _followers.Add(follower);
        }

        return new Unfollow(this, follower);
    }

    public WatchedPrice? Latest(Market market, TradingMode mode, string symbol, ExchangePriceKind kind)
    {
        lock (_gate)
        {
            return _latest.GetValueOrDefault(new LivePrice(market, mode, symbol, kind));
        }
    }

    private sealed class Unfollow(StandInPrices prices, (LivePrice Of, Action<WatchedPrice> Heard) follower) : IDisposable
    {
        public void Dispose()
        {
            lock (prices._gate)
            {
                prices._followers.Remove(follower);
            }
        }
    }
}

/// <summary>Stands for the cycles that run: a test says which, for which agent of which user.</summary>
internal sealed class StandInProgress : ICycleProgress
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid UserId, Guid AgentId), IReadOnlyList<RunningCycle>> _running = [];

    /// <summary>The error that asking ends with, when a test gives it one.</summary>
    public Exception? Fails { get; set; }

    /// <summary>Says the cycles of an agent of a user that run now.</summary>
    public void Runs(Guid userId, Guid agentId, params RunningCycle[] cycles)
    {
        lock (_gate)
        {
            _running[(userId, agentId)] = cycles;
        }
    }

    public IReadOnlyList<RunningCycle> OfAgent(Guid userId, Guid agentId)
    {
        if (Fails is { } error)
        {
            throw error;
        }

        lock (_gate)
        {
            return _running.GetValueOrDefault((userId, agentId), []);
        }
    }
}

/// <summary>
/// The application on a clock that only the test moves, with the timers that run on it, with
/// the prices and the cycles that run in the hands of the test, and the feeds of the pages that
/// a test opens.
/// </summary>
internal sealed class Feeding : IDisposable
{
    public static readonly DateTimeOffset Morning = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    public Feeding()
    {
        Clock = new FakeTimeProvider(Morning);
        Application = new AccountsApplication(host: host => host.ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IExchangePriceWatch>(Prices);
            services.AddSingleton<ICycleProgress>(Progress);
        }));
    }

    public FakeTimeProvider Clock { get; }

    public AccountsApplication Application { get; }

    public StandInPrices Prices { get; } = new();

    public StandInProgress Progress { get; } = new();

    public ILiveSignals Signals => Application.LiveSignals;

    /// <summary>Registers a user and signs them in on a browser: the user, the browser, and the connection that a page of it makes.</summary>
    public async Task<(StoredUser User, HttpClient Browser, ClaimsPrincipal Connection)> SignedInAsync(string email)
    {
        // The application listens to its database before a test follows: what follows from
        // then on is told what the test causes, and not that it missed something before.
        await Signals.ListensAsync();

        var user = await Application.RegisterVerifiedAsync(email);
        var browser = Application.CreateClient();
        await browser.SignInAsync(email);

        var session = StoredSession.All(Application.Database).Single(stored => stored.UserId == user.Id);

        return (user, browser, session.ConnectionOf(user));
    }

    /// <summary>Opens the feed of a page of a user, as a page that is written does. It follows once the test says so.</summary>
    public LiveFeed Open(
        ClaimsPrincipal connection,
        StandInPage page,
        Guid? agentId = null,
        LiveSubject[]? follows = null,
        LiveShows shows = LiveShows.None,
        long? since = null,
        bool values = false) =>
        Application.LiveFeeds.Open(
            connection,
            new LiveView(
                agentId, follows ?? [], shows, Market.Spot, TradingMode.Demo, "BTCUSDT", "USDT", since ?? Signals.Mark(), page.Address, values),
            page);

    /// <summary>A cycle that runs, at the steps that a test says, with every role of its team waiting.</summary>
    public static RunningCycle Cycle(params CycleStep[] steps) =>
        new(Guid.NewGuid(), CycleKind.Scheduled, Morning, Morning, [.. steps.Select(step => new ReachedStep(step, Morning))], Roles());

    /// <summary>What the four roles of a team do, in the order of the roles. A role that a test says nothing of waits.</summary>
    public static IReadOnlyList<RoleProgress> Roles(params RoleState[] states) =>
        [.. Enum.GetValues<Role>().Select((role, at) => new RoleProgress(role, at < states.Length ? states[at] : RoleState.Waiting))];

    public void Dispose() => Application.Dispose();
}
