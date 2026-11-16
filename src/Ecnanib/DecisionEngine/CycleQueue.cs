using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// What the candles of a cycle are of: a symbol and a timeframe, on a market in an environment.
/// The exchange is asked once for all the agents that share the four (FR-BIN-3).
/// </summary>
internal readonly record struct CandleGroup(Market Market, TradingMode Mode, string Symbol, Timeframe Timeframe);

/// <summary>A cycle from the close of its candle until its turn comes.</summary>
/// <param name="cycleId">The id of the cycle.</param>
/// <param name="agentId">The agent.</param>
/// <param name="userId">The user whose agent it is, and whose keys its roles are called with.</param>
/// <param name="group">The candles that it starts with.</param>
/// <param name="turn">
/// Which of its user's agents of that timeframe it is, from 0. The first agents of all users
/// come before the second of any, so that the users take turns.
/// </param>
/// <param name="providers">The AI providers that the roles of its team use, each once.</param>
internal sealed class QueuedCycle(
    Guid cycleId, Guid agentId, Guid userId, CandleGroup group, int turn, IReadOnlySet<AiProviderCode> providers)
{
    public Guid CycleId { get; } = cycleId;

    public Guid AgentId { get; } = agentId;

    public Guid UserId { get; } = userId;

    public CandleGroup Group { get; } = group;

    public int Turn { get; } = turn;

    public IReadOnlySet<AiProviderCode> Providers { get; } = providers;
}

/// <summary>
/// The queue of the cycles that share a candle boundary: the order they start in, and how many
/// run at once for each AI provider and for each key of a user (FR-CYC-11). See docs/cycles.md.
/// </summary>
/// <remarks>
/// <para>
/// A cycle takes a place with every provider that a role of its team uses, and with the key of
/// its user for that provider, which is one key (FR-KEY-2). A provider that holds two roles of a
/// team counts once. It keeps its places until it ends, however it ends.
/// </para>
/// <para>
/// The shortest timeframe comes first, and within a timeframe the users take turns. A cycle that
/// finds no place waits, and reserves none: a later cycle that needs other providers starts
/// before it. So a provider that is slow or full holds back the cycles that need it and no
/// others (NFR-REL-4), and the order is strict among cycles that need the same places.
/// </para>
/// <para>
/// It keeps no time and asks nobody. One run of the job has one queue, and only that run touches
/// it, so it takes no lock.
/// </para>
/// </remarks>
/// <param name="maxPerProvider">The most cycles that use one provider at once.</param>
/// <param name="maxPerKey">The most cycles that use the key of one user for one provider at once.</param>
internal sealed class CycleQueue(int maxPerProvider, int maxPerKey)
{
    private static readonly Comparer<QueuedCycle> Order = Comparer<QueuedCycle>.Create(static (one, other) =>
    {
        var byTimeframe = one.Group.Timeframe.Length().CompareTo(other.Group.Timeframe.Length());
        if (byTimeframe != 0)
        {
            return byTimeframe;
        }

        var byTurn = one.Turn.CompareTo(other.Turn);

        return byTurn != 0 ? byTurn : one.AgentId.CompareTo(other.AgentId);
    });

    // In the order that the cycles start in.
    private readonly List<QueuedCycle> _waiting = [];

    // The places that are taken.
    private readonly Dictionary<AiProviderCode, int> _perProvider = [];
    private readonly Dictionary<(Guid UserId, AiProviderCode Provider), int> _perKey = [];

    /// <summary>How many cycles wait.</summary>
    public int Count => _waiting.Count;

    /// <summary>Puts a cycle in the queue, at its place in the order.</summary>
    public void Add(QueuedCycle cycle)
    {
        var at = _waiting.BinarySearch(cycle, Order);

        _waiting.Insert(at < 0 ? ~at : at, cycle);
    }

    /// <summary>
    /// Takes every waiting cycle that finds all its places free, in the order of the queue, and
    /// gives each its places. The cycles that are taken start now.
    /// </summary>
    public IReadOnlyList<QueuedCycle> TakeWhatFits()
    {
        var taken = new List<QueuedCycle>();
        var kept = 0;

        for (var index = 0; index < _waiting.Count; index++)
        {
            var cycle = _waiting[index];

            if (Fits(cycle))
            {
                Take(cycle);
                taken.Add(cycle);
            }
            else
            {
                _waiting[kept++] = cycle;
            }
        }

        _waiting.RemoveRange(kept, _waiting.Count - kept);

        return taken;
    }

    /// <summary>Gives back the places of a cycle that was taken, once, when it has ended.</summary>
    public void Release(QueuedCycle cycle)
    {
        foreach (var provider in cycle.Providers)
        {
            GiveBack(_perProvider, provider);
            GiveBack(_perKey, (cycle.UserId, provider));
        }
    }

    /// <summary>Takes every cycle that still waits out of the queue: none of them starts.</summary>
    public IReadOnlyList<QueuedCycle> Drain()
    {
        QueuedCycle[] waiting = [.. _waiting];
        _waiting.Clear();

        return waiting;
    }

    private bool Fits(QueuedCycle cycle)
    {
        foreach (var provider in cycle.Providers)
        {
            if (_perProvider.GetValueOrDefault(provider) >= maxPerProvider
                || _perKey.GetValueOrDefault((cycle.UserId, provider)) >= maxPerKey)
            {
                return false;
            }
        }

        return true;
    }

    private void Take(QueuedCycle cycle)
    {
        foreach (var provider in cycle.Providers)
        {
            _perProvider[provider] = _perProvider.GetValueOrDefault(provider) + 1;
            _perKey[(cycle.UserId, provider)] = _perKey.GetValueOrDefault((cycle.UserId, provider)) + 1;
        }
    }

    private static void GiveBack<TKey>(Dictionary<TKey, int> places, TKey key)
        where TKey : notnull
    {
        var left = places[key] - 1;

        if (left == 0)
        {
            // Nothing is kept of a user or a provider that no cycle uses.
            places.Remove(key);
        }
        else
        {
            places[key] = left;
        }
    }
}
