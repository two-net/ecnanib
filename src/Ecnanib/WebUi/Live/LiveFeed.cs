using System.Security.Claims;
using System.Threading.Channels;
using Ecnanib.Accounts;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.LiveUpdates;
using Ecnanib.Shared;

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
    string Address)
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
    public static LiveView Of(
        HttpRequest request,
        Guid? agentId,
        IReadOnlyList<LiveSubject> follows,
        LiveShows shows,
        Market? market,
        TradingMode? mode,
        string? symbol,
        string? quoteAsset,
        long since)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(follows);

        return new LiveView(
            agentId,
            HttpMethods.IsGet(request.Method) ? follows : [],
            shows,
            market,
            mode,
            symbol,
            quoteAsset,
            since,
            request.Path.ToUriComponent() + request.QueryString.ToUriComponent());
    }
}

/// <summary>A price that a page follows: which one, of which symbol, where.</summary>
public sealed record LivePrice(Market Market, TradingMode Mode, string Symbol, ExchangePriceKind Kind);

/// <summary>What the parts of a page show now.</summary>
/// <param name="Cycles">The cycles of the agent that run.</param>
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

            case PriceRead read when read.Of == State.Followed:
                Volatile.Write(ref _state, State with { Price = read.Price });
                await _page.ShowAsync();
                break;

            case AskDue:
                await AskAsync();
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

        if (_view.Shows.HasFlag(LiveShows.Position))
        {
            var (position, followed) = await PositionAsync(userId, _stop.Token);

            Volatile.Write(ref _state, State with { Position = position });
            FollowPrice(followed);
            await _page.ShowAsync();
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

        if (missed ? _view.Follows.Count > 0 : _view.Follows.Contains(signal.Subject))
        {
            AskSoon();
        }
    }

    /// <summary>The cycles of the agent of the page that run, where the page shows them.</summary>
    private IReadOnlyList<RunningCycle> Cycles(Guid userId) =>
        _view.Shows.HasFlag(LiveShows.Cycle) && _view.AgentId is { } agentId ? _progress.OfAgent(userId, agentId) : [];

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
            _pricing = _prices.Follow(
                followed.Market, followed.Mode, followed.Symbol, followed.Kind, price => Post(new PriceRead(followed, price)));
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
        _sessionTimer?.Dispose();
        _sessionTimer = null;
        _askTimer?.Dispose();
        _askTimer = null;
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

    private sealed record SessionDue : Work;

    private sealed record ConnectedAgain : Work;

    private sealed record Leave : Work;
}
