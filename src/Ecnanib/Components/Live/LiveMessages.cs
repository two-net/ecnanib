using System.Globalization;
using Ecnanib.Components.Council;
using Ecnanib.Components.Experience;
using Ecnanib.Database.Entities;
using Ecnanib.Execution.Abstractions;

namespace Ecnanib.Components.Live;

/// <summary>User-facing texts of the live islands (FR-UI-03, FR-UI-04). Everything an animation shows is also said here in words.</summary>
internal static class LiveMessages
{
    public const string Evaluating = "Evaluating…";

    public const string Loading = "Loading…";

    public const string TodayUtc = "Today (UTC)";

    public const string AwaitingVerdict = "Awaiting the Risk Manager's verdict…";

    public const string Skip = "Skip";

    public const string Replay = "Replay";

    public const string LiveEquityNote = "A live agent's equity is the exchange account's, shared by every agent on the key; see your Binance balance.";

    public const string PriceWaiting = "waiting for a price";

    public const string NoTrades = "No trade yet. The timeline of each trade appears here: evaluation, decision, entry, protection, stop moves, exits and the close.";

    public const string NoUsage = "No council call yet. Token usage per role and for today appears here once the council has deliberated.";

    public const string CostsAreYours = "Provider and exchange costs are yours under BYOK.";

    /// <summary>The output-token cap new council calls are made under (NFR-COST-01): the configuration's current value, so it is said of new calls, not of the totals above it.</summary>
    public static string OutputTokenCap(int cap) => $"New council calls are capped at {Count(cap)} output tokens each.";

    /// <summary>"in 12 min 05 s", "in 1 h 02 min", "in 3 d 04 h" or "now" once the close is due.</summary>
    public static string NextEvaluation(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return "now";
        if (remaining.TotalDays >= 1)
            return $"in {(int)remaining.TotalDays} d {remaining.Hours:00} h";
        if (remaining.TotalHours >= 1)
            return $"in {(int)remaining.TotalHours} h {remaining.Minutes:00} min";
        return $"in {(int)remaining.TotalMinutes} min {remaining.Seconds:00} s";
    }

    /// <summary>A price with up to eight decimals and no trailing zeros, or the waiting text.</summary>
    public static string Price(decimal? price) =>
        price is { } p ? p.ToString("0.########", CultureInfo.InvariantCulture) : PriceWaiting;

    /// <summary>A whole number with thousands separators, or a dash.</summary>
    public static string Count(decimal? value) => value is { } v ? ((long)v).ToString("N0", CultureInfo.InvariantCulture) : "—";

    public static string Verdict(DecisionAction? action) => "Verdict: " + CouncilMessages.Action(action);

    /// <summary>The badge over the chart when a marker appears (FR-UI-03 c); canvas markers cannot announce themselves.</summary>
    public static string NewMarker(string markerText) => "New on the chart: " + markerText;

    /// <summary>What a role slot says: disabled, waiting, thinking, or its recorded status.</summary>
    public static string RoleState(RoleSlot slot, RoleStep? step)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (!slot.Enabled && step?.Status is null)
            return "disabled";
        return step switch
        {
            null => "waiting",
            { Stage: RoleStage.Thinking } => "thinking…",
            { Status: { } status } => CouncilMessages.Status(status),
            _ => "waiting",
        };
    }

    public static string Timeline(TimelineEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.Kind switch
        {
            TimelineKind.Evaluated => "Evaluated",
            TimelineKind.Decided => "Council decided: " + CouncilMessages.Action(e.Action),
            TimelineKind.EntryPlaced => "Entry placed",
            TimelineKind.EntryFilled => "Entry filled",
            TimelineKind.EntryEnded => "Entry ended without a fill",
            TimelineKind.Protected => "Protected",
            TimelineKind.StopMoved => "Stop moved",
            TimelineKind.Managed => "Council: " + CouncilMessages.Action(e.Action),
            TimelineKind.TargetsChanged => "Targets changed",
            TimelineKind.PartialExit => "Partial exit",
            TimelineKind.Exit => "Exit filled",
            TimelineKind.Closed => "Closed: " + ExperienceMessages.ExitReason(e.ExitReason),
            _ => e.Kind.ToString(),
        };
    }

    public static string TimelineDetail(TimelineEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var price = e.Price is { } p ? Price(p) : null;
        var quantity = e.Quantity is { } q ? Price(q) : null;
        var pnl = e.RealizedPnl is { } r ? $"P&L {ExperienceMessages.Money(r)}" : null;
        return e.Kind switch
        {
            TimelineKind.Decided => price is null ? "market entry" : $"limit at {price}",
            TimelineKind.EntryPlaced => Join(quantity, price is null ? "at market" : $"at {price}"),
            TimelineKind.EntryFilled => Join(quantity, price is null ? null : $"at {price}"),
            TimelineKind.EntryEnded => e.Status?.ToString().ToLowerInvariant() ?? "",
            TimelineKind.Protected => Join(price is null ? null : $"stop {price}", e.Targets is { Count: > 0 } targets ? "targets " + string.Join(", ", targets.Select(t => Price(t))) : "no target"),
            TimelineKind.StopMoved => Join(price is null ? null : $"to {price}", string.IsNullOrEmpty(e.Reason) ? null : $"({e.Reason})"),
            TimelineKind.Managed => Truncate(e.Reason, 160),
            TimelineKind.TargetsChanged => e.Targets is { Count: > 0 } changed ? "to " + string.Join(", ", changed.Select(t => Price(t))) : "",
            TimelineKind.PartialExit or TimelineKind.Exit => Join(Join(quantity, price is null ? null : $"at {price}"), pnl),
            TimelineKind.Closed => Join(price is null ? null : $"at {price}", pnl),
            _ => "",
        };
    }

    private static string Join(string? first, string? second) =>
        string.IsNullOrEmpty(first) ? second ?? "" : string.IsNullOrEmpty(second) ? first : $"{first}, {second}";

    private static string Truncate(string? text, int length) =>
        text is null ? "" : text.Length <= length ? text : text[..length].TrimEnd() + "…";
}
