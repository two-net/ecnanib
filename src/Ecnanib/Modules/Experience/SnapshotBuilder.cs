using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;

namespace Ecnanib.Modules.Experience;

/// <summary>A pair's candles in one market, environment and timeframe, which an analyst's call is scored against.</summary>
internal sealed record SeriesKey(Market Market, TradingEnvironment Environment, string Symbol, Timeframe Timeframe);

/// <summary>What one build of a user's days makes.</summary>
/// <param name="DaysMissingPrices">Days with an analyst's call whose prices should have been there and weren't.</param>
internal sealed record BuiltDays(
    IReadOnlyList<PerformanceSnapshotRow> Snapshots,
    IReadOnlyList<CycleAttributionRow> Attributions,
    IReadOnlySet<DateTimeOffset> DaysMissingPrices);

/// <summary>
/// Turns a user's council calls into a snapshot per agent, dimensions, role, model and day, and the attributions of the
/// cycles whose Strategist proposed an entry. It reads nothing itself, so it's the same however it's called. See
/// Trading experience in docs/architecture.md.
/// </summary>
internal static class SnapshotBuilder
{
    /// <summary>How far the Sentiment Analyst's score must lean, either way, to count as a call.</summary>
    public const decimal SentimentThreshold = 0.2m;

    /// <param name="closes">
    /// Each series' closes by the time each candle closed, or null when they couldn't be read. Only asked for a series
    /// with an analyst's call.
    /// </param>
    /// <param name="horizon">How many candles after the cycle's a call is scored against.</param>
    public static BuiltDays Build(
        Guid userId,
        IReadOnlyList<CouncilCallRecord> calls,
        Func<SeriesKey, IReadOnlyDictionary<DateTimeOffset, decimal>?> closes,
        int horizon,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(calls);
        ArgumentNullException.ThrowIfNull(closes);

        var groups = new Dictionary<GroupKey, Group>();
        var missing = new HashSet<DateTimeOffset>();

        foreach (var call in calls)
        {
            var key = new GroupKey(
                call.AgentId,
                call.Environment,
                call.Market,
                call.Symbol,
                call.Timeframe,
                call.Role,
                call.Provider,
                call.ModelId,
                ExperienceDays.Start(call.CycleStartedAt));

            if (!groups.TryGetValue(key, out var group))
            {
                group = new Group();
                groups.Add(key, group);
            }

            Count(group, call);

            if (call.Status == CouncilCallStatus.Answered && DirectionOf(call) is { } bullish)
            {
                Score(group, call, bullish, closes, horizon, now, missing, key.Day);
            }
        }

        return new BuiltDays(
            [.. groups.Select(pair => pair.Value.ToRow(userId, pair.Key, now))],
            Attribute(userId, calls),
            missing);
    }

    /// <summary>True for a bullish call and false for a bearish one, or null when the analyst made none.</summary>
    public static bool? DirectionOf(CouncilCallRecord call) => call.Role switch
    {
        AiRole.TechnicalAnalyst => call.Trend switch
        {
            Trend.Up => true,
            Trend.Down => false,
            _ => null,
        },
        AiRole.SentimentAnalyst => call.Score switch
        {
            >= SentimentThreshold => true,
            <= -SentimentThreshold => false,
            _ => null,
        },
        _ => null,
    };

    private static void Count(Group group, CouncilCallRecord call)
    {
        var row = group.Counters;
        group.Cycles.Add(call.CycleId);

        switch (call.Status)
        {
            case CouncilCallStatus.NotSent:
                row.NotSent++;
                return;
            case CouncilCallStatus.Answered:
                row.Answered++;
                group.AnsweredCycles.Add(call.CycleId);

                if (call.Fallback)
                {
                    group.FallbackCycles.Add(call.CycleId);
                }

                CountDecision(row, call);
                break;
            case CouncilCallStatus.BrokeContract:
                row.ContractViolations++;
                break;
            case CouncilCallStatus.Failed when call.Failure == CouncilFailure.Timeout:
                row.Timeouts++;
                break;
            case CouncilCallStatus.Failed when call.Failure == CouncilFailure.Refusal:
                row.Refusals++;
                break;
            case CouncilCallStatus.Failed:
                row.Errors++;
                break;
        }

        row.Sent++;
        group.Latency[LatencyBands.Of(call.Latency)]++;

        if (call.Truncated)
        {
            row.Truncated++;
        }

        row.InputTokens += call.InputTokens;
        row.OutputTokens += call.OutputTokens;

        if (call.Cost is { } cost)
        {
            row.Cost += cost;
        }
        else
        {
            row.UnpricedTokens += call.InputTokens + call.OutputTokens;
        }
    }

    private static void CountDecision(Counters row, CouncilCallRecord call)
    {
        switch (call.Action)
        {
            case TradeAction.Hold:
                row.Holds++;
                break;
            case { } action when TradeActions.IsEntry(action):
                row.Entries++;
                break;
            case { } action when TradeActions.IsExit(action):
                row.Exits++;
                break;
            case not null:
                row.Adjustments++;
                break;
        }

        switch (call.Verdict)
        {
            case VerdictKind.Approve:
                row.Approvals++;
                break;
            case VerdictKind.Modify:
                row.Modifications++;
                break;
            case VerdictKind.Reject:
                row.Rejections++;
                break;
        }
    }

    /// <summary>
    /// Scores a call against the close <paramref name="horizon"/> candles after the cycle's candle. One that can't be
    /// scored yet stays unresolved, and so does one whose prices should be there and aren't, which marks its day.
    /// </summary>
    private static void Score(
        Group group,
        CouncilCallRecord call,
        bool bullish,
        Func<SeriesKey, IReadOnlyDictionary<DateTimeOffset, decimal>?> closes,
        int horizon,
        DateTimeOffset now,
        HashSet<DateTimeOffset> missing,
        DateTimeOffset day)
    {
        var row = group.Counters;

        if (bullish)
        {
            row.BullishCalls++;
        }
        else
        {
            row.BearishCalls++;
        }

        var target = call.CandleClose + (ExperienceDays.Interval(call.Timeframe) * horizon);

        if (target > now)
        {
            return;
        }

        var series = closes(new SeriesKey(call.Market, call.Environment, call.Symbol, call.Timeframe));

        if (series is null || !series.TryGetValue(call.CandleClose, out var before) || !series.TryGetValue(target, out var after))
        {
            missing.Add(day);
            return;
        }

        if (bullish ? after > before : after < before)
        {
            row.Hits++;
        }
        else
        {
            row.Misses++;
        }
    }

    /// <summary>
    /// Each role's model in every cycle whose Strategist answered with an entry, from the call that answered for the
    /// role. A trade can only come from such a cycle.
    /// </summary>
    private static List<CycleAttributionRow> Attribute(Guid userId, IReadOnlyList<CouncilCallRecord> calls)
    {
        var answered = calls.Where(call => call.Status == CouncilCallStatus.Answered).ToList();
        var entries = answered
            .Where(call => call.Role == AiRole.Strategist && call.Action is { } action && TradeActions.IsEntry(action))
            .Select(call => call.CycleId)
            .ToHashSet();

        return
        [
            .. answered
                .Where(call => entries.Contains(call.CycleId))
                .GroupBy(call => (call.CycleId, call.Role))
                .Select(roles => roles.MaxBy(call => call.Id)!)
                .Select(call => new CycleAttributionRow
                {
                    CycleId = call.CycleId,
                    Role = call.Role,
                    UserId = userId,
                    AgentId = call.AgentId,
                    Environment = call.Environment,
                    Market = call.Market,
                    Symbol = call.Symbol,
                    Timeframe = call.Timeframe,
                    Provider = call.Provider,
                    ModelId = call.ModelId,
                }),
        ];
    }

    private sealed record GroupKey(
        Guid AgentId,
        TradingEnvironment Environment,
        Market Market,
        string Symbol,
        Timeframe Timeframe,
        AiRole Role,
        string Provider,
        string ModelId,
        DateTimeOffset Day);

    private sealed class Counters
    {
        public long Sent;
        public long NotSent;
        public long Answered;
        public long ContractViolations;
        public long Timeouts;
        public long Refusals;
        public long Errors;
        public long Truncated;
        public long InputTokens;
        public long OutputTokens;
        public decimal Cost;
        public long UnpricedTokens;
        public long Holds;
        public long Entries;
        public long Exits;
        public long Adjustments;
        public long Approvals;
        public long Modifications;
        public long Rejections;
        public long BullishCalls;
        public long BearishCalls;
        public long Hits;
        public long Misses;
    }

    private sealed class Group
    {
        public Counters Counters { get; } = new();

        public HashSet<Guid> Cycles { get; } = [];

        public HashSet<Guid> AnsweredCycles { get; } = [];

        public HashSet<Guid> FallbackCycles { get; } = [];

        public long[] Latency { get; } = LatencyBands.Empty();

        public PerformanceSnapshotRow ToRow(Guid userId, GroupKey key, DateTimeOffset now) => new()
        {
            UserId = userId,
            AgentId = key.AgentId,
            Environment = key.Environment,
            Market = key.Market,
            Symbol = key.Symbol,
            Timeframe = key.Timeframe,
            Role = key.Role,
            Provider = key.Provider,
            ModelId = key.ModelId,
            PeriodStart = key.Day,
            Cycles = Cycles.Count,
            AnsweredCycles = AnsweredCycles.Count,
            FallbackCycles = FallbackCycles.Count,
            Sent = Counters.Sent,
            NotSent = Counters.NotSent,
            Answered = Counters.Answered,
            ContractViolations = Counters.ContractViolations,
            Timeouts = Counters.Timeouts,
            Refusals = Counters.Refusals,
            Errors = Counters.Errors,
            Truncated = Counters.Truncated,
            InputTokens = Counters.InputTokens,
            OutputTokens = Counters.OutputTokens,
            Cost = Counters.Cost,
            UnpricedTokens = Counters.UnpricedTokens,
            LatencyBuckets = Latency,
            Holds = Counters.Holds,
            Entries = Counters.Entries,
            Exits = Counters.Exits,
            Adjustments = Counters.Adjustments,
            Approvals = Counters.Approvals,
            Modifications = Counters.Modifications,
            Rejections = Counters.Rejections,
            BullishCalls = Counters.BullishCalls,
            BearishCalls = Counters.BearishCalls,
            Hits = Counters.Hits,
            Misses = Counters.Misses,
            ComputedAt = now,
        };
    }
}
