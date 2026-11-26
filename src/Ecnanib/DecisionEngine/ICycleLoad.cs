namespace Ecnanib.DecisionEngine;

/// <summary>
/// How deep the queue of the cycles is now: the cycles of the close that is under way, by how
/// far each has come (FR-CYC-11). It is the queue depth that an operator status screen shows
/// (FR-OPS-2). See "The depth of the queue" in docs/cycles.md.
/// </summary>
/// <remarks>
/// <para>
/// It is of every user, and names none: it holds numbers and the time of a close. A user follows
/// the cycle of an agent of theirs through <see cref="ICycleProgress"/>.
/// </para>
/// <para>
/// The queue of a close is in memory, in the instance that runs the background work, and
/// nothing of it is stored (FR-CYC-8). So this is what that instance says of itself: an instance
/// that stands by has no close under way. A test cycle runs outside the queue, and is not in it.
/// </para>
/// </remarks>
public interface ICycleLoad
{
    /// <summary>
    /// The cycles of the close that this instance runs now, or null when it runs none: between
    /// two closes, when no agent ran on a timeframe of the close, and on an instance that
    /// stands by.
    /// </summary>
    CycleLoad? Now();
}

/// <summary>The cycles of one close, by how far each has come. A cycle that is over is in none of the numbers.</summary>
/// <param name="ClosedAt">The close that the cycles are of.</param>
/// <param name="AwaitingCandles">The cycles whose candles the exchange has not given yet.</param>
/// <param name="Queued">The cycles that have their candles and wait for a place with their providers and keys.</param>
/// <param name="Running">The cycles that run.</param>
public sealed record CycleLoad(DateTimeOffset ClosedAt, int AwaitingCandles, int Queued, int Running);

/// <summary>
/// Keeps what the run of a close last said of its cycles, for whoever asks. See
/// <see cref="ICycleLoad"/>.
/// </summary>
/// <remarks>
/// One run says it, from its one loop, and anyone reads it: what is kept is replaced whole, so
/// a reader has the numbers of one moment and never a part of two.
/// </remarks>
internal sealed class CycleLoads : ICycleLoad
{
    private CycleLoad? _now;

    public CycleLoad? Now() => Volatile.Read(ref _now);

    /// <summary>Says how the cycles of the close stand now, or with null that the run of the close is over.</summary>
    public void Say(CycleLoad? load) => Volatile.Write(ref _now, load);
}
