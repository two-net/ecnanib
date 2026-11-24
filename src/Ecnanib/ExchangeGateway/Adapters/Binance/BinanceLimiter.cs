using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway.Adapters.Binance;

/// <summary>
/// The one limiter that every call to Binance passes, for all users (FR-BIN-4). Binance counts
/// its limits on requests against the address that the calls come from, so everyone who uses the
/// service spends one budget. The limiter keeps the service under the part of each limit that the
/// configuration file allows it, stops sending when Binance signals that it has had too many,
/// gives the users whose calls wait their turns, and says what is used. See docs/exchange.md.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="BinanceLimit"/> has a budget in each of its environments. A budget is paced:
/// a part of what a minute allows may go at once, and the rest flows evenly, so that any sixty
/// seconds carry at most the whole, and so that there is soon something for a call that comes
/// after a burst. Binance does not document whether its minute is one of the clock or the last
/// sixty seconds, and this holds for both.
/// </para>
/// <para>
/// A call that finds its budget short waits, in the line of its user. The lines are served in
/// turns, one call of each user after the other, so the burst of one user leaves the calls of
/// another one call behind, and not a burst behind. A call waits for <see cref="LongestWait"/>
/// at most, and is then not sent.
/// </para>
/// <para>
/// The gateway makes a call once, and this does not change it. A caller that makes the call
/// again while a budget is held reaches Binance with nothing: that is what keeps a loop of a
/// caller from Binance.
/// </para>
/// <para>
/// It counts in memory, for this process. Another process at the same address is not counted
/// here, and shows in what Binance says it has counted, which the limiter goes by when it is
/// more than its own count.
/// </para>
/// </remarks>
internal sealed class BinanceLimiter(BinanceSettings settings, TimeProvider clock, ILogger<BinanceLimiter> logger)
    : IExchangeLimits, IDisposable
{
    /// <summary>How long a call waits for its turn. One that would wait longer is not sent.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(10);

    /// <summary>For how long nothing is sent after Binance answered that it has had too many, when it does not say for how long.</summary>
    public static readonly TimeSpan HoldAfterThrottling = TimeSpan.FromMinutes(1);

    /// <summary>The same after Binance answered that it has blocked the address. Its shortest block is this long.</summary>
    public static readonly TimeSpan HoldAfterBan = TimeSpan.FromMinutes(2);

    /// <summary>The longest that the limiter holds a budget by itself. Binance may say longer.</summary>
    public static readonly TimeSpan LongestHoldOfItsOwn = TimeSpan.FromHours(1);

    // The part of what a minute allows that may go at once: a fifth.
    private const int PartsOfAMinute = 5;

    // What a sum of fractions may be off by, in request weight.
    private const double Slack = 1e-6;

    // Binance counts by the minute.
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly long _began = clock.GetTimestamp();
    private readonly List<Budget> _budgets =
    [
        .. from limit in BinanceLimit.All
           from mode in limit.Environments
           select Budget.Of(limit, mode, settings.LimitSharePercent),
    ];

    private bool _disposed;

    /// <summary>
    /// Lets a call through when its budget has room for it: at once, or when its turn comes.
    /// </summary>
    /// <param name="endpoint">The call, which says what it costs of which limit.</param>
    /// <param name="mode">The environment, which has budgets of its own.</param>
    /// <param name="userId">Whose call it is, or null for a call of the service itself, which takes turns as one more user.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>
    /// Whether the call may be sent now. It may not when its turn does not come within
    /// <see cref="LongestWait"/>, which is said at once where it is already known.
    /// </returns>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task<bool> EnterAsync(BinanceEndpoint endpoint, TradingMode mode, Guid? userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var user = userId ?? Guid.Empty;
        Budget budget;
        Waiter waiter;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            budget = BudgetOf(endpoint.Limit, mode);
            var now = Now();
            budget.Flow(now);

            // Nobody waits, so there is no turn to take.
            if (budget.Next is null && now >= budget.HeldUntil && budget.Holds(endpoint.Weight))
            {
                budget.Take(endpoint.Weight, now);

                return true;
            }

            // With the whole flow to its user alone, its turn would still come too late. It
            // also keeps the line of a user as short as what the flow can serve in time.
            if (budget.EarliestFor(budget.WaitingOf(user) + endpoint.Weight, now) - now > LongestWait)
            {
                return false;
            }

            waiter = budget.Join(user, endpoint.Weight);
            Wake(budget, now);
        }

        // The wait runs on the application's clock, as the limit of a call does.
        using var patience = new CancellationTokenSource(LongestWait, clock);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, patience.Token);
        using var givenUp = either.Token.Register(() => GiveUp(budget, waiter));

        if (await waiter.Turn.Task)
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();

        return false;
    }

    /// <summary>
    /// Takes what Binance said with an answer that was no signal of too many requests: what it
    /// has counted against the address, where the answer says it.
    /// </summary>
    /// <param name="endpoint">The call that was answered.</param>
    /// <param name="mode">Its environment.</param>
    /// <param name="counted">The request weight that Binance has counted in its minute, or null when the answer does not say.</param>
    public void Answered(BinanceEndpoint endpoint, TradingMode mode, int? counted)
    {
        var said = false;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var budget = BudgetOf(endpoint.Limit, mode);
            budget.Signals = 0;

            if (counted is not { } weight)
            {
                return;
            }

            var now = Now();
            budget.Flow(now);
            budget.Reported = weight;
            budget.ReportedAt = clock.GetUtcNow();

            // What Binance has counted is what counts. The limiter's own count is never below
            // it while the calls of this process are all there is and weigh what Binance
            // documents. When it is, the budget has what Binance has left of it, and no more.
            var left = budget.Allowed - weight;
            if (left < budget.Free - Slack)
            {
                budget.Free = left;
                Wake(budget, now);

                said = !budget.Behind;
                budget.Behind = true;
            }
            else
            {
                budget.Behind = false;
            }
        }

        if (said)
        {
            logger.LogWarning(
                "Binance has counted {Reported} of request weight on {Limit} {Environment}, which is more than the limiter let through. The limiter goes by what Binance counts",
                counted,
                endpoint.Limit.Name,
                mode.ToString());
        }
    }

    /// <summary>
    /// Takes the signal of Binance that it has had too many requests: nothing is sent on the
    /// budget of the call until Binance takes calls again.
    /// </summary>
    /// <param name="endpoint">The call that was answered with the signal.</param>
    /// <param name="mode">Its environment.</param>
    /// <param name="banned">Whether Binance has blocked the address, which it does when its first signal is ignored.</param>
    /// <param name="retryAfter">For how long Binance says to send nothing, or null when it does not say.</param>
    public void Throttled(BinanceEndpoint endpoint, TradingMode mode, bool banned, TimeSpan? retryAfter)
    {
        List<Waiter> sentAway = [];
        TimeSpan held;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var budget = BudgetOf(endpoint.Limit, mode);
            var now = Now();

            // What Binance says is kept. Where it says nothing, each signal that follows another
            // with no other answer between them holds for twice as long.
            budget.Signals++;
            var hold = retryAfter is { } asked && asked > TimeSpan.Zero ? asked : OwnHold(banned, budget.Signals);
            if (now + hold > budget.HeldUntil)
            {
                budget.HeldUntil = now + hold;
            }

            // The flow starts again when the hold is over, from nothing.
            budget.Free = Math.Min(budget.Free, 0);
            budget.AsOf = budget.HeldUntil;
            held = budget.HeldUntil - now;

            // No call that waits now can see the end of it.
            if (held > LongestWait)
            {
                sentAway = budget.Empty();
            }

            Wake(budget, now);
        }

        foreach (var waiter in sentAway)
        {
            waiter.Turn.TrySetResult(false);
        }

        logger.LogWarning(
            "Binance signalled that it has had too many requests on {Limit} {Environment}: {Signal}. No call is sent there for {HoldSeconds} seconds",
            endpoint.Limit.Name,
            mode.ToString(),
            banned ? "Banned" : "Throttled",
            Math.Round(held.TotalSeconds, 1));
    }

    public IReadOnlyList<ExchangeLimitUsage> Usage()
    {
        lock (_gate)
        {
            var now = Now();
            var time = clock.GetUtcNow();

            return
            [
                .. _budgets.Select(budget => new ExchangeLimitUsage(
                    budget.Limit.Name,
                    budget.Mode,
                    budget.Limit.WeightPerMinute,
                    budget.Allowed,
                    budget.UsedAt(now),
                    budget.Reported,
                    budget.ReportedAt,
                    budget.Calls,
                    now < budget.HeldUntil ? time + (budget.HeldUntil - now) : null)),
            ];
        }
    }

    public bool HasRoom(Market market, TradingMode environment)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            var budget = BudgetOf(market == Market.Futures ? BinanceLimit.Futures : BinanceLimit.Spot, environment);
            var now = Now();

            // What Binance itself counted, while that is of this minute, where it is more than
            // what the limiter counted: another process at the same address shows there.
            var reported = budget is { Reported: { } counted, ReportedAt: { } at } && clock.GetUtcNow() - at < Minute ? counted : 0;

            return now >= budget.HeldUntil
                && budget.Calls == 0
                && Math.Max(budget.UsedAt(now), reported) * 2 < budget.Allowed;
        }
    }

    public void Dispose()
    {
        List<Waiter> waiting = [];

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var budget in _budgets)
            {
                waiting.AddRange(budget.Empty());
                budget.Timer?.Dispose();
            }
        }

        foreach (var waiter in waiting)
        {
            waiter.Turn.TrySetResult(false);
        }
    }

    /// <summary>How long the limiter holds a budget when Binance does not say for how long.</summary>
    private static TimeSpan OwnHold(bool banned, int signals)
    {
        var first = banned ? HoldAfterBan : HoldAfterThrottling;
        var doubled = first.Ticks << Math.Min(signals - 1, 16);

        return TimeSpan.FromTicks(Math.Min(doubled, LongestHoldOfItsOwn.Ticks));
    }

    /// <summary>The time since the limiter began, which no change of the time of day moves.</summary>
    private TimeSpan Now() => clock.GetElapsedTime(_began);

    /// <summary>The budget of a limit in an environment. Called with the gate held.</summary>
    private Budget BudgetOf(BinanceLimit limit, TradingMode mode)
    {
        foreach (var budget in _budgets)
        {
            if (budget.Limit == limit && budget.Mode == mode)
            {
                return budget;
            }
        }

        // An environment that the limit does not name: it is counted all the same.
        var added = Budget.Of(limit, mode, settings.LimitSharePercent);
        _budgets.Add(added);

        return added;
    }

    /// <summary>A call stops waiting: it waited long enough, or its caller cancelled.</summary>
    private void GiveUp(Budget budget, Waiter waiter)
    {
        lock (_gate)
        {
            // The others may have waited behind it.
            if (budget.Leave(waiter) && !_disposed)
            {
                Serve(budget);
            }
        }

        waiter.Turn.TrySetResult(false);
    }

    /// <summary>
    /// Lets through the calls whose turn it is, for as long as the budget has room for the next
    /// one, and sets the time to look again. Called with the gate held.
    /// </summary>
    private void Serve(Budget budget)
    {
        var now = Now();
        budget.Flow(now);

        // In the order of the turns: a call that the budget has no room for yet is waited for,
        // and no lighter one passes it.
        while (now >= budget.HeldUntil && budget.Next is { } next && budget.Holds(next.Weight))
        {
            budget.Leave(next);
            budget.ToTheBack(next.User);
            budget.Take(next.Weight, now);
            next.Turn.TrySetResult(true);
        }

        Wake(budget, now);
    }

    /// <summary>Sets the timer of a budget to when its next call can pass. Called with the gate held.</summary>
    private void Wake(Budget budget, TimeSpan now)
    {
        if (budget.Next is not { } next)
        {
            budget.Timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            return;
        }

        var due = budget.EarliestFor(next.Weight, now) - now;
        if (due < TimeSpan.FromMilliseconds(1))
        {
            due = TimeSpan.FromMilliseconds(1);
        }

        budget.Timer ??= clock.CreateTimer(
            _ =>
            {
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        Serve(budget);
                    }
                }
            },
            state: null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        budget.Timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    /// <summary>One call that waits for its turn.</summary>
    private sealed class Waiter(Guid user, int weight)
    {
        public Guid User { get; } = user;

        public int Weight { get; } = weight;

        /// <summary>Ends when the wait does: with whether the call may be sent.</summary>
        public TaskCompletionSource<bool> Turn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Its place in the line of its user, while it waits.</summary>
        public LinkedListNode<Waiter>? Place { get; set; }
    }

    /// <summary>
    /// What one limit allows in one environment, and where it stands. Everything here is read
    /// and written with the gate of the limiter held.
    /// </summary>
    private sealed class Budget
    {
        private readonly Dictionary<Guid, LinkedList<Waiter>> _lines = [];
        private readonly LinkedList<Guid> _turns = new();
        private readonly Queue<(TimeSpan At, int Weight)> _sent = new();
        private int _used;

        private Budget(BinanceLimit limit, TradingMode mode, int allowed, int atOnce)
        {
            Limit = limit;
            Mode = mode;
            Allowed = allowed;
            AtOnce = atOnce;
            PerSecond = (allowed - atOnce) / Minute.TotalSeconds;
            Free = atOnce;
        }

        public BinanceLimit Limit { get; }

        public TradingMode Mode { get; }

        /// <summary>The request weight that the service allows itself in a minute.</summary>
        public int Allowed { get; }

        /// <summary>The most that may go at once: what the budget holds when nothing was sent for a while.</summary>
        public int AtOnce { get; }

        /// <summary>What flows in each second. With <see cref="AtOnce"/>, a minute brings <see cref="Allowed"/>.</summary>
        public double PerSecond { get; }

        /// <summary>The request weight that may be sent now. It is below zero when Binance has counted more than there was.</summary>
        public double Free { get; set; }

        /// <summary>The moment that <see cref="Free"/> is of. It is ahead while the budget is held: nothing flows until then.</summary>
        public TimeSpan AsOf { get; set; }

        /// <summary>Until when nothing is sent, after a signal of Binance.</summary>
        public TimeSpan HeldUntil { get; set; }

        /// <summary>The signals of Binance in a row, with no other answer between them.</summary>
        public int Signals { get; set; }

        /// <summary>Whether the last answer of Binance had counted more than the limiter.</summary>
        public bool Behind { get; set; }

        public int? Reported { get; set; }

        public DateTimeOffset? ReportedAt { get; set; }

        public ITimer? Timer { get; set; }

        /// <summary>The calls that wait.</summary>
        public int Calls { get; private set; }

        /// <summary>The call whose turn is next, or null when none waits.</summary>
        public Waiter? Next => _turns.First is { } turn ? _lines[turn.Value].First!.Value : null;

        public static Budget Of(BinanceLimit limit, TradingMode mode, int percent)
        {
            var allowed = limit.WeightPerMinute * percent / 100;

            // It must hold the heaviest call, or that call would never pass.
            var heaviest = BinanceEndpoint.All.Where(call => call.Limit == limit).Select(call => call.Weight).DefaultIfEmpty(1).Max();
            var atOnce = Math.Max(allowed / PartsOfAMinute, heaviest);

            return allowed > atOnce
                ? new Budget(limit, mode, allowed, atOnce)
                : throw new InvalidOperationException(
                    $"The part of Binance's limit on requests that Binance:LimitSharePercent allows is {allowed} of request weight in a minute for {limit.Name}, which is too little for its heaviest call, of {heaviest}.");
        }

        /// <summary>Brings <see cref="Free"/> to now: what has flowed in since, up to what may go at once.</summary>
        public void Flow(TimeSpan now)
        {
            if (now <= AsOf)
            {
                return;
            }

            Free = Math.Min(AtOnce, Free + ((now - AsOf).TotalSeconds * PerSecond));
            AsOf = now;
        }

        public bool Holds(int weight) => Free >= weight - Slack;

        /// <summary>Counts a call that is let through.</summary>
        public void Take(int weight, TimeSpan now)
        {
            Free -= weight;
            _sent.Enqueue((now, weight));
            _used += weight;
        }

        /// <summary>The request weight that was let through in the last minute.</summary>
        public int UsedAt(TimeSpan now)
        {
            while (_sent.TryPeek(out var sent) && sent.At <= now - Minute)
            {
                _used -= _sent.Dequeue().Weight;
            }

            return _used;
        }

        /// <summary>
        /// When the budget has room for a weight at the earliest, with nothing else sent before:
        /// when it is no longer held, and the flow has brought what is missing.
        /// </summary>
        public TimeSpan EarliestFor(int weight, TimeSpan now)
        {
            var from = now >= HeldUntil ? now : HeldUntil;
            var missing = weight - Free;

            // A moment later rather than a moment early, where the division does not come out even.
            return missing > 0 ? from + TimeSpan.FromSeconds(missing / PerSecond) + TimeSpan.FromTicks(1) : from;
        }

        /// <summary>The request weight of the calls that a user has waiting.</summary>
        public int WaitingOf(Guid user) => _lines.TryGetValue(user, out var line) ? line.Sum(waiter => waiter.Weight) : 0;

        /// <summary>Puts a call at the end of the line of its user. A user without a line gets the last turn.</summary>
        public Waiter Join(Guid user, int weight)
        {
            if (!_lines.TryGetValue(user, out var line))
            {
                line = new LinkedList<Waiter>();
                _lines.Add(user, line);
                _turns.AddLast(user);
            }

            var waiter = new Waiter(user, weight);
            waiter.Place = line.AddLast(waiter);
            Calls++;

            return waiter;
        }

        /// <summary>Takes a call out of the line of its user, and says whether it was still in it.</summary>
        public bool Leave(Waiter waiter)
        {
            if (waiter.Place is not { } place)
            {
                return false;
            }

            var line = _lines[waiter.User];
            line.Remove(place);
            waiter.Place = null;
            Calls--;

            if (line.Count == 0)
            {
                _lines.Remove(waiter.User);
                _turns.Remove(waiter.User);
            }

            return true;
        }

        /// <summary>Gives a user the last turn, when it still has a call waiting.</summary>
        public void ToTheBack(Guid user)
        {
            if (_lines.ContainsKey(user))
            {
                _turns.Remove(user);
                _turns.AddLast(user);
            }
        }

        /// <summary>Takes every call out of its line, and gives them.</summary>
        public List<Waiter> Empty()
        {
            var waiting = _lines.Values.SelectMany(line => line).ToList();

            foreach (var waiter in waiting)
            {
                waiter.Place = null;
            }

            _lines.Clear();
            _turns.Clear();
            Calls = 0;

            return waiting;
        }
    }
}
