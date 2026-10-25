using Ecnanib.Database.Entities;

namespace Ecnanib.Components.Live;

/// <summary>
/// How long each turn of a replayed deliberation is shown (FR-UI-03 b): a role that ran takes a moment proportional to
/// its recorded latency, bounded so a replay never drags or blinks; a role that ran no model passes quickly. The
/// recorded turns have no timestamps of their own, only a sequence and a latency.
/// </summary>
internal static class ReplayTiming
{
    public static readonly TimeSpan Minimum = TimeSpan.FromMilliseconds(400);

    public static readonly TimeSpan Maximum = TimeSpan.FromMilliseconds(1200);

    public static readonly TimeSpan Passing = TimeSpan.FromMilliseconds(250);

    public static readonly TimeSpan Handoff = TimeSpan.FromMilliseconds(150);

    public static readonly TimeSpan Reveal = TimeSpan.FromMilliseconds(400);

    /// <summary>A hundredth of the latency on top of the minimum, within the bounds; disabled and skipped roles pass.</summary>
    public static TimeSpan Duration(RoleOutputStatus status, int latencyMs)
    {
        if (status is RoleOutputStatus.Disabled or RoleOutputStatus.Skipped)
            return Passing;
        var scaled = Minimum.TotalMilliseconds + Math.Max(0, latencyMs) / 10.0;
        return TimeSpan.FromMilliseconds(Math.Clamp(scaled, Minimum.TotalMilliseconds, Maximum.TotalMilliseconds));
    }
}

/// <summary>One recorded turn of a deliberation as the replay plays it.</summary>
public sealed record ReplayTurn(CouncilRole Role, int Sequence, RoleOutputStatus Status, DecisionAction? Proposed, int LatencyMs);
