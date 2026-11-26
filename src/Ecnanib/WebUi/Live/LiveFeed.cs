using System.Security.Claims;
using System.Threading.Channels;
using Ecnanib.Accounts;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.LiveUpdates;
using Ecnanib.Shared;
using Ecnanib.WebUi.Pages;

namespace Ecnanib.WebUi.Live;

/// <summary>The parts of a page that are written to it over its connection.</summary>
[Flags]
public enum LiveShows
{
    /// <summary>Nothing: the page asks for itself again when something changes, and shows it then.</summary>
    None = 0,

    /// <summary>The steps of the cycles of the agent that run.</summary>
    Cycle = 1,

    /// <summary>The open position of the agent, with the price that it is valued at.</summary>
    Position = 2,

    /// <summary>
    /// What the team of the agent does now, also when it does nothing: with
    /// <see cref="Cycle"/>, the page shows the team at work as a pipeline (FR-ANI-1), says so
    /// when no cycle of the agent runs, and shows a cycle that ended a moment ago with how
    /// each role ended, as the screen of the agent does (FR-RPT-2).
    /// </summary>
    Team = 4,
}

/// <summary>What a page follows, as the page says it when it is written.</summary>
/// <param name="AgentId">The agent that the page is of, or null for a page of every agent of its user.</param>
/// <param name="Follows">The subjects that the page asks for itself again on. None for a page that does not.</param>
/// <param name="Shows">The parts that are written to the page over its connection.</param>
/// <param name="Market">The market of the agent, whose price the page shows while the agent holds nothing.</param>
/// <param name="Mode">The mode of the agent.</param>
/// <param name="Symbol">The symbol of the agent.</param>
/// <param name="QuoteAsset">The asset that the symbol of the agent is priced in, which a price is said in.</param>
/// <param name="Since">What <see cref="ILiveSignals.Mark"/> said before the page read what it shows.</param>
/// <param name="Address">The path and the query that the page was asked for at, as the browser sent them.</param>
/// <param name="Values">
/// Whether the page values the open positions of its user at their prices, as the dashboard
/// does (FR-RPT-1): the prices of what its user holds are followed while the page is open, so
/// that the watch has them when the page is written, and the page asks for itself again when
/// one of them is first read. A page of one agent values what that agent holds, as its screen
/// does (FR-RPT-2): the price that the page follows for its position is the one that it asks
/// for itself again on, and no price of another agent is followed for it.
/// </param>
/// <param name="Team">
/// The roles of the team of the agent, each with the model that it was given, for a page that
/// shows the team at work (FR-ANI-1). What each role does is asked of the cycles that run.
/// </param>
/// <param name="TimeZone">
/// The time zone that the page writes its times in, by its name, or null for UTC (NFR-USA-3):
/// the parts that are written over the connection say a time as the page does. See
/// <see cref="PageTimes.ZoneId"/>.
/// </param>
/// <remarks>
/// It travels with the page, sealed by the framework, to the connection that the page makes,
/// so it holds what can be written as text and read again.
/// </remarks>
public sealed record LiveView(
    Guid? AgentId,
    IReadOnlyList<LiveSubject> Follows,
    LiveShows Shows,
    Market? Market,
    TradingMode? Mode,
    string? Symbol,
    string? QuoteAsset,
    long Since,
    string Address,
    bool Values = false,
    IReadOnlyList<LiveRole>? Team = null,
    string? TimeZone = null)
{
    /// <summary>
    /// What a page follows, as the request that writes the page makes it. A page that answers
    /// a form asks for itself again on nothing: what the form answered would be gone from it.
    /// Only a page that was asked for, and changed nothing, does.
    /// </summary>
    /// <param name="request">The request that writes the page.</param>
    /// <param name="agentId">The agent that the page is of, or null.</param>
    /// <param name="follows">The subjects that the page asks for itself again on, when it was asked for.</param>
    /// <param name="shows">The parts that are written to the page over its connection.</param>
    /// <param name="market">The market of the agent.</param>
    /// <param name="mode">The mode of the agent.</param>
    /// <param name="symbol">The symbol of the agent.</param>
    /// <param name="quoteAsset">The asset that the symbol of the agent is priced in.</param>
    /// <param name="since">What <see cref="ILiveSignals.Mark"/> said before the page read what it shows.</param>
    /// <param name="values">
    /// Whether the page values the open positions of its user at their prices, when it was
    /// asked for: a price that is read has the page ask for itself again, which a page that
    /// answers a form does on nothing.
    /// </param>
    /// <param name="team">The roles of the team of the agent, each with its model, for a page that shows the team at work.</param>
    public static LiveView Of(
        HttpRequest request,
        Guid? agentId,
        IReadOnlyList<LiveSubject> follows,
        LiveShows shows,
        Market? market,
        TradingMode? mode,
        string? symbol,
        string? quoteAsset,
        long since,
        bool values = false,
        IReadOnlyList<LiveRole>? team = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(follows);

        var asked = HttpMethods.IsGet(request.Method);

        return new LiveView(
            agentId,
            asked ? follows : [],
            shows,
            market,
            mode,
            symbol,
            quoteAsset,
            since,
            request.Path.ToUriComponent() + request.QueryString.ToUriComponent(),
            asked && values,
            team,
            PageTimes.Of(request.HttpContext).ZoneId);
    }
}

/// <summary>
/// A role of the team of an agent as a page names it: the role, and the model that it was
/// given, with its provider, in the words of the page.
/// </summary>
/// <param name="Role">The role.</param>
/// <param name="Model">The model that the role was given, with its provider, or null when it was given none.</param>
public sealed record LiveRole(Role Role, string? Model);

/// <summary>A price that a page follows: which one, of which symbol, where.</summary>
public sealed record LivePrice(Market Market, TradingMode Mode, string Symbol, ExchangePriceKind Kind);

/// <summary>What the parts of a page show now.</summary>
/// <param name="Cycles">
/// The cycles of the agent that run, and for a page that shows the team at work those that
/// ended a moment ago.
/// </param>
/// <param name="Position">The open position of the agent, or null when it holds none.</param>
/// <param name="Followed">The price that is followed, or null when none is.</param>
/// <param name="Price">That price as it was last read, or null while none was.</param>
public sealed record LiveState(IReadOnlyList<RunningCycle> Cycles, OpenPosition? Position, LivePrice? Followed, WatchedPrice? Price)
{
    /// <summary>What a page shows before anything was read.</summary>
    public static LiveState Nothing { get; } = new([], Position: null, Followed: null, Price: null);
}

/// <summary>The page that a feed writes to: what the feed asks of it.</summary>
public interface ILivePage
{
    /// <summary>
    /// Whether the connection of the page is up. A browser that sleeps loses it for a while,
    /// and nothing can be asked of the page until it is back.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>The path and the query that the page is asked for at, as the browser sends them.</summary>
    string Address { get; }

    /// <summary>Writes the parts of the page again, as <see cref="LiveFeed.State"/> says them now.</summary>
    Task ShowAsync();

    /// <summary>Has the page ask for itself again, without leaving the page that it is.</summary>
    Task AskAgainAsync();

    /// <summary>Has the browser load the page anew: its session has ended, so that leads to the page that signs in.</summary>
    Task LeaveAsync();

    /// <summary>Says that the feed failed, with the error. The feed has stopped.</summary>
    Task FailAsync(Exception error);
}

/// <summary>Opens the feed of a page. See <see cref="LiveFeed"/>.</summary>
public sealed class LiveFeeds(
    ILiveSignals signals,
    IConnectedSession session,
    ICycleProgress progress,
    IOpenPositions positions,
    IExchangePriceWatch prices,
    TimeProvider clock)
{
    /// <summary>Opens the feed of a page of a user. It reads and follows nothing until it is told to.</summary>
    /// <param name="user">The user of the connection of the page, or of the request that writes it.</param>
    /// <param name="view">What the page follows.</param>
    /// <param name="page">The page.</param>
    public LiveFeed Open(ClaimsPrincipal user, LiveView view, ILivePage page)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(page);

        return new LiveFeed(signals, session, progress, positions, prices, clock, user, view, page);
    }
}

/// <summary>
/// What keeps one page up to date over its connection (FR-NTF-2): it follows the signals of the
/// user of the page, writes the parts of the page that tick, and has the page ask for itself
/// again when something that it shows has changed. See docs/live-updates.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is of one user,</b> the one of the connection, and follows the signals of that user
/// alone. What it shows it asks for by owner, as a page does, so a signal shows nobody anything
/// of anyone else (NFR-SEC-3).
/// </para>
/// <para>
/// <b>It asks whether the session of the connection still stands,</b> when a session of its
/// user ended and once a minute, because the connection is not asked for again by itself. When
/// the session does not stand, nothing more is written, and the browser loads the page anew,
/// which leads to the page that signs in.
/// </para>
/// <para>
/// <b>The page asks for itself again at most once a second,</b> and a burst of signals is one
/// request. It says to Accounts that it does, so that the request is no activity of its user:
/// a page that follows does not keep the session of a browser alive that nobody sits at
/// (NFR-SEC-1).
/// </para>
/// <para>
/// <b>Nothing is asked of a page whose connection is down.</b> What it owes then, it does when
/// the connection is back.
/// </para>
/// <para>
/// <b>For a page that values what its user holds, it follows the prices of that,</b> which is
/// the dashboard (FR-RPT-1): the symbols that the agents of its user hold a position in, asked
/// for by owner, when it begins and each time that what they hold changes. A page is written
/// with the price that the watch has, and never waits for the exchange, so someone has to
/// follow a price for the watch to have it: the feed of the page does. The first time that a
/// price which it began to follow is read, the page asks for itself again, and is written with
/// it. A price that is read again asks for nothing: the figures of such a page say the time of
/// their prices, and are worked out anew when something that the page shows changes.
/// </para>
/// <para>
/// <b>For a page of one agent that values what the agent holds, it follows no other price</b>
/// than the one that the page shows with the position, which is the screen of the agent
/// (FR-RPT-2). The first time that this price is read for a position that the agent holds, the
/// page asks for itself again, and is written with the equity of the agent at it. The prices of
/// what the other agents of its user hold are not followed: a price that is followed is read
/// from the exchange once a second.
/// </para>
/// <para>
/// <b>For a page that shows the team at work, it shows a cycle that has ended for a moment,</b>
/// with how each role ended, which is the screen of the agent (FR-ANI-1). Nothing says when
/// that moment is over, so the feed reads the cycles again then, by its own clock, and the
/// page shows that the team is at rest. The other pages are told the cycles that run alone.
/// </para>
/// <para>
/// Everything that it does, it does one thing after the other, on a line of its own: a signal
/// is told to it by whoever raised it, who is not kept waiting.
/// </para>
/// </remarks>
public sealed class LiveFeed : IAsyncDisposable
{
    /// <summary>How long the feed waits for more signals before the page asks for itself again, so that a burst is one request.</summary>
    public static readonly TimeSpan AsksAfter = TimeSpan.FromMilliseconds(150);

    /// <summary>The least time between two requests of a page for itself.</summary>
    public static readonly TimeSpan AsksAtMostEvery = TimeSpan.FromSeconds(1);

    /// <summary>How often the feed asks whether the session of its connection still stands, when nothing says that one ended.</summary>
    public static readonly TimeSpan AsksAboutSessionEvery = TimeSpan.FromMinutes(1);

    private readonly ILiveSignals _signals;
    private readonly IConnectedSession _session;
    private readonly ICycleProgress _progress;
    private readonly IOpenPositions _positions;
    private readonly IExchangePriceWatch _prices;
    private readonly TimeProvider _clock;
    private readonly ClaimsPrincipal _user;
    private readonly LiveView _view;
    private readonly ILivePage _page;

    private readonly Channel<Work> _work = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();

    private LiveState _state = LiveState.Nothing;
    private Task? _working;
    private IDisposable? _following;
    private IDisposable? _pricing;
    private ITimer? _sessionTimer;
    private ITimer? _askTimer;
    private ITimer? _restTimer;

    // The prices that are followed for a page that values what its user holds, and those of
    // them that were not read yet since they were first followed. Only the line of the feed
    // touches them.
    private readonly Dictionary<LivePrice, IDisposable> _valuing = [];
    private readonly HashSet<LivePrice> _awaited = [];

    // Whether the page is to ask for itself again, and when it last did.
    private bool _asks;
    private long? _askedAt;

    // What the page owes while its connection is down.
    private bool _owesAsking;
    private bool _left;
    private bool _disposed;

    internal LiveFeed(
        ILiveSignals signals,
        IConnectedSession session,
        ICycleProgress progress,
        IOpenPositions positions,
        IExchangePriceWatch prices,
        TimeProvider clock,
        ClaimsPrincipal user,
        LiveView view,
        ILivePage page)
    {
        _signals = signals;
        _session = session;
        _progress = progress;
        _positions = positions;
        _prices = prices;
        _clock = clock;
        _user = user;
        _view = view;
        _page = page;
    }

    /// <summary>What the parts of the page show now.</summary>
    public LiveState State => Volatile.Read(ref _state);

    /// <summary>
    /// Reads what the parts of the page show, once, and follows nothing: for a page as it is
    /// written, and as its connection is made. It asks the exchange nothing: the price is the
    /// one that is known, when one is.
    /// </summary>
    public async Task ReadAsync(CancellationToken cancellationToken)
    {
        if (SignedInUser.IdOf(_user) is not { } userId)
        {
            return;
        }

        var cycles = Cycles(userId);
        var (position, followed) = await PositionAsync(userId, cancellationToken);

        Volatile.Write(
            ref _state,
            new LiveState(cycles, position, followed, followed is null ? null : _prices.Latest(followed.Market, followed.Mode, followed.Symbol, followed.Kind)));
    }

    /// <summary>
    /// Begins to follow: the signals of the user of the connection, from where the page read,
    /// the price of what it shows, and the session of the connection. It returns at once.
    /// </summary>
    public void Follow()
    {
        lock (_gate)
        {
            if (_working is not null || _disposed)
            {
                return;
            }

            _working = Task.Run(WorkAsync);
        }

        if (SignedInUser.IdOf(_user) is not { } userId || SignedInUser.SessionIdOf(_user) is null)
        {
            // Nobody is signed in on the connection: its session ended before it was made.
            Post(new Leave());

            return;
        }

        Post(new Begin(userId));
    }

    /// <summary>Says who the user of the connection is now: a connection that is made again is signed in again.</summary>
    public void UserIs(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (SignedInUser.IdOf(user) != SignedInUser.IdOf(_user) || SignedInUser.SessionIdOf(user) != SignedInUser.SessionIdOf(_user))
        {
            // Another user, or another session: this feed is of the one that it was opened for.
            Post(new Leave());
        }
    }

    /// <summary>Says that the connection of the page is up again.</summary>
    public void Connected() => Post(new ConnectedAgain());

    private void Post(Work work) => _work.Writer.TryWrite(work);

    private async Task WorkAsync()
    {
        try
        {
            await foreach (var work in _work.Reader.ReadAllAsync(_stop.Token))
            {
                await DoAsync(work);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // The page is gone.
        }
        catch (Exception error)
        {
            Unfollow();

            try
            {
                await _page.FailAsync(error);
            }
            catch (Exception)
            {
                // The page is gone, and nobody is left to tell.
            }
        }
    }

    private async Task DoAsync(Work work)
    {
        if (_left)
        {
            // Nothing more is written. The browser is still to load the page anew.
            if (work is ConnectedAgain)
            {
                await _page.LeaveAsync();
            }

            return;
        }

        switch (work)
        {
            case Begin begin:
                await BeginAsync(begin.UserId);
                break;

            case Heard heard:
                await HearAsync(heard.Signal);
                break;

            case PriceRead read:
                if (read.Of == State.Followed)
                {
                    Volatile.Write(ref _state, State with { Price = read.Price });
                    await _page.ShowAsync();
                }

                // The first read of a price that the page values at: the page was written
                // without it, or with an older one, and is written again with it. What is read
                // after that is on the page the next time that it is written.
                if (_awaited.Remove(read.Of))
                {
                    AskSoon();
                }

                break;

            case AskDue:
                await AskAsync();
                break;

            case RestDue:
                // The moment that a cycle which ended is shown for is over: nothing says so,
                // so the cycles are read again, and the page shows what is left of them.
                if (SignedInUser.IdOf(_user) is { } resting)
                {
                    Volatile.Write(ref _state, State with { Cycles = Cycles(resting) });
                    AwaitRest();
                    await _page.ShowAsync();
                }

                break;

            case SessionDue:
                if (!await _session.StandsAsync(_user, _stop.Token))
                {
                    await LeaveAsync();
                }

                break;

            case ConnectedAgain when _owesAsking:
                _owesAsking = false;
                AskSoon();
                break;

            case Leave:
                await LeaveAsync();
                break;
        }
    }

    private async Task BeginAsync(Guid userId)
    {
        // What the user of the page is told from now on, and what was heard since the page read.
        _following = _signals.Follow(userId, _view.Since, signal => Post(new Heard(signal)));

        _sessionTimer = _clock.CreateTimer(
            _ => Post(new SessionDue()), state: null, AsksAboutSessionEvery, AsksAboutSessionEvery);

        // A cycle that had ended a moment before the page read: the page shows it until its
        // moment is over.
        AwaitRest();

        if (_view.Shows.HasFlag(LiveShows.Position))
        {
            var (position, followed) = await PositionAsync(userId, _stop.Token);

            Volatile.Write(ref _state, State with { Position = position });
            FollowPrice(followed);
            await _page.ShowAsync();
        }

        // A page of every agent values what they all hold. A page of one agent values what
        // that agent holds, at the price that it follows already: see FollowPrice.
        if (_view.Values && _view.AgentId is null)
        {
            await ValueAsync(userId);
        }
    }

    private async Task HearAsync(LiveSignal signal)
    {
        if (SignedInUser.IdOf(_user) is not { } userId)
        {
            return;
        }

        var missed = signal.Subject == LiveSubject.Missed;

        // A session of the user ended, or something was missed, which may be that.
        if ((missed || signal.Subject == LiveSubject.Session) && !await _session.StandsAsync(_user, _stop.Token))
        {
            await LeaveAsync();

            return;
        }

        // A page of one agent is told nothing of another. A signal that names no agent is
        // about none, or about all of them.
        if (signal.AgentId is { } about && _view.AgentId is { } agentId && about != agentId)
        {
            return;
        }

        var shows = false;

        if (_view.Shows.HasFlag(LiveShows.Cycle) && (missed || signal.Subject == LiveSubject.CycleStep))
        {
            Volatile.Write(ref _state, State with { Cycles = Cycles(userId) });
            AwaitRest();
            shows = true;
        }

        if (_view.Shows.HasFlag(LiveShows.Position) && (missed || signal.Subject is LiveSubject.Holdings or LiveSubject.Agent))
        {
            var (position, followed) = await PositionAsync(userId, _stop.Token);

            Volatile.Write(ref _state, State with { Position = position });
            FollowPrice(followed);
            shows = true;
        }

        if (shows)
        {
            await _page.ShowAsync();
        }

        // What the agents of the user hold has changed, or may have: the prices that the page
        // values at are those of what they hold now.
        if (_view.Values && _view.AgentId is null && (missed || signal.Subject == LiveSubject.Holdings))
        {
            await ValueAsync(userId);
        }

        if (missed ? _view.Follows.Count > 0 : _view.Follows.Contains(signal.Subject))
        {
            AskSoon();
        }
    }

    /// <summary>
    /// The cycles of the agent of the page that run, where the page shows them. A page that
    /// shows the team at work is told a cycle that ended a moment ago as well, until that
    /// moment is over by the clock of the feed. The other pages are told none that has ended.
    /// </summary>
    private IReadOnlyList<RunningCycle> Cycles(Guid userId)
    {
        if (!_view.Shows.HasFlag(LiveShows.Cycle) || _view.AgentId is not { } agentId)
        {
            return [];
        }

        var cycles = _progress.OfAgent(userId, agentId);

        if (!_view.Shows.HasFlag(LiveShows.Team))
        {
            return [.. cycles.Where(cycle => cycle.EndedAt is null)];
        }

        var now = _clock.GetUtcNow();

        return [.. cycles.Where(cycle => !cycle.IsGoneAt(now))];
    }

    /// <summary>
    /// Has the cycles read again when the moment of a cycle that has ended is over: of the one
    /// whose moment is over first, when the page shows several. Nothing says that moment, and
    /// the page is to show then that the team is at rest.
    /// </summary>
    private void AwaitRest()
    {
        _restTimer?.Dispose();
        _restTimer = null;

        DateTimeOffset? first = null;

        foreach (var cycle in State.Cycles)
        {
            if (cycle.EndedAt + RunningCycle.KeptAfterEnd is { } over && (first is null || over < first))
            {
                first = over;
            }
        }

        if (first is null)
        {
            return;
        }

        var wait = first.Value - _clock.GetUtcNow();

        _restTimer = _clock.CreateTimer(
            _ => Post(new RestDue()), state: null, wait > TimeSpan.Zero ? wait : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The open position of the agent of the page, where the page shows it, and the price that
    /// the page follows with it: the price that the position is valued at, or the same of the
    /// symbol of the agent while it holds nothing.
    /// </summary>
    private async Task<(OpenPosition? Position, LivePrice? Followed)> PositionAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!_view.Shows.HasFlag(LiveShows.Position) || _view.AgentId is not { } agentId)
        {
            return (null, null);
        }

        if (await _positions.OfAgentAsync(userId, agentId, cancellationToken) is { } position)
        {
            return (position, new LivePrice(position.Market, position.Mode, position.Symbol, PositionValues.PriceOf(position.Market)));
        }

        return (
            null,
            _view is { Market: { } market, Mode: { } mode, Symbol: { Length: > 0 } symbol }
                ? new LivePrice(market, mode, symbol, PositionValues.PriceOf(market))
                : null);
    }

    /// <summary>Follows a price in place of the one that was followed, when it is another one.</summary>
    private void FollowPrice(LivePrice? followed)
    {
        if (followed == State.Followed && (_pricing is not null || followed is null))
        {
            return;
        }

        _pricing?.Dispose();
        _pricing = null;

        Volatile.Write(
            ref _state,
            State with
            {
                Followed = followed,
                Price = followed is null ? null : _prices.Latest(followed.Market, followed.Mode, followed.Symbol, followed.Kind),
            });

        if (followed is not null)
        {
            if (_view.Values && _view.AgentId is not null)
            {
                // A page of one agent that values what the agent holds: the first read of the
                // price of its position has the page ask for itself again, whether or not the
                // watch had the price when the page was written. Awaited before it is
                // followed: the watch tells the price that it has at once. A price that no
                // position is valued at changes no figure, and is awaited by nothing.
                _awaited.Clear();

                if (State.Position is not null)
                {
                    _awaited.Add(followed);
                }
            }

            _pricing = _prices.Follow(
                followed.Market, followed.Mode, followed.Symbol, followed.Kind, price => Post(new PriceRead(followed, price)));
        }
    }

    /// <summary>
    /// Follows the prices that the open positions of the agents of the user are valued at, in
    /// place of those that were followed: for a page that values what its user holds. A price
    /// of something that is no longer held is let go of, and one that is followed already stays
    /// as it is. Each that is followed anew is awaited: its first read has the page ask for
    /// itself again, whether or not the watch had it when the page was written, because another
    /// page may have begun to follow it in between.
    /// </summary>
    private async Task ValueAsync(Guid userId)
    {
        // Asked for by owner, as a page asks: what the agents of another user hold is not found.
        var held = await _positions.HeldByUserAsync(userId, _stop.Token);

        var wanted = held
            .Select(symbol => new LivePrice(symbol.Market, symbol.Mode, symbol.Symbol, PositionValues.PriceOf(symbol.Market)))
            .ToHashSet();

        foreach (var gone in _valuing.Keys.Where(followed => !wanted.Contains(followed)).ToList())
        {
            _valuing.Remove(gone, out var following);
            following?.Dispose();
            _awaited.Remove(gone);
        }

        foreach (var price in wanted.Where(price => !_valuing.ContainsKey(price)))
        {
            // Awaited before it is followed: the watch tells the price that it has at once.
            _awaited.Add(price);
            _valuing[price] = _prices.Follow(price.Market, price.Mode, price.Symbol, price.Kind, read => Post(new PriceRead(price, read)));
        }
    }

    /// <summary>Has the page ask for itself again soon: after a moment, and no sooner than a second after it last did.</summary>
    private void AskSoon()
    {
        if (_asks)
        {
            // It is about to. What changed until then is on the page that it asks for.
            return;
        }

        _asks = true;

        var wait = AsksAfter;

        if (_askedAt is { } askedAt && AsksAtMostEvery - _clock.GetElapsedTime(askedAt) is var rest && rest > wait)
        {
            wait = rest;
        }

        _askTimer?.Dispose();
        _askTimer = _clock.CreateTimer(_ => Post(new AskDue()), state: null, wait, Timeout.InfiniteTimeSpan);
    }

    private async Task AskAsync()
    {
        _asks = false;

        if (!_page.IsConnected)
        {
            // The browser sleeps. The page asks when its connection is back.
            _owesAsking = true;

            return;
        }

        _askedAt = _clock.GetTimestamp();

        // The request that follows is the page's own, and no activity of its user.
        _session.ExpectsPageAgain(_user, _page.Address);

        await _page.AskAgainAsync();
    }

    /// <summary>The session of the connection has ended: nothing more is written, and the browser loads the page anew.</summary>
    private async Task LeaveAsync()
    {
        _left = true;
        Unfollow();

        Volatile.Write(ref _state, LiveState.Nothing);

        if (_page.IsConnected)
        {
            await _page.LeaveAsync();
        }
    }

    private void Unfollow()
    {
        _following?.Dispose();
        _following = null;
        _pricing?.Dispose();
        _pricing = null;

        foreach (var following in _valuing.Values)
        {
            following.Dispose();
        }

        _valuing.Clear();
        _awaited.Clear();

        _sessionTimer?.Dispose();
        _sessionTimer = null;
        _askTimer?.Dispose();
        _askTimer = null;
        _restTimer?.Dispose();
        _restTimer = null;
    }

    public async ValueTask DisposeAsync()
    {
        Task? working;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            working = _working;
        }

        _stop.Cancel();
        _work.Writer.TryComplete();

        if (working is not null)
        {
            try
            {
                await working;
            }
            catch (Exception)
            {
                // It ends by itself, and says what went wrong where it went wrong.
            }
        }

        Unfollow();
        _stop.Dispose();
    }

    /// <summary>Something for the feed to do, in its turn.</summary>
    private abstract record Work;

    private sealed record Begin(Guid UserId) : Work;

    private sealed record Heard(LiveSignal Signal) : Work;

    private sealed record PriceRead(LivePrice Of, WatchedPrice Price) : Work;

    private sealed record AskDue : Work;

    private sealed record RestDue : Work;

    private sealed record SessionDue : Work;

    private sealed record ConnectedAgain : Work;

    private sealed record Leave : Work;
}
