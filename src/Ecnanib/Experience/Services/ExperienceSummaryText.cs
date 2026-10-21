using System.Globalization;
using System.Text;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Council.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;

namespace Ecnanib.Experience.Services;

/// <summary>A closed trade the role took part in: its gross R and how it ended; <see cref="Overruled"/> when the role had said no_trade.</summary>
internal sealed record RecentTrade(DateTimeOffset ClosedAt, decimal R, TradeExitReason? Reason, bool Overruled);

/// <summary>A rejection the role made whose shadow outcome is resolved (FR-EXP-03).</summary>
internal sealed record RecentRejection(DateTimeOffset At, ShadowResult Result, decimal? R, string? Resolution);

/// <summary>
/// The compact experience summary of FR-EXP-06: the headline statistics for the provider, model and role at the
/// agent's timeframe and market (this agent, all the user's agents, all users) and the most recent outcomes. Names
/// no agent, user or symbol. Null when there is nothing to tell.
/// </summary>
internal static class ExperienceSummaryText
{
    /// <summary>A role prompt carries many sections; the summary stays well under this.</summary>
    public const int MaxLength = 1500;

    public static string? Compose(ExperienceQuery query, ExperienceMetrics? agent, ExperienceMetrics? user, ExperienceMetrics? system,
        IReadOnlyList<RecentTrade> trades, IReadOnlyList<RecentRejection> rejections, int recent)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(trades);
        ArgumentNullException.ThrowIfNull(rejections);
        if (agent is null && user is null && system is null && trades.Count == 0 && rejections.Count == 0)
            return null;

        var where = $"{query.Timeframe.ToLabel()} {Market(query.MarketType)}";
        var text = new StringBuilder();
        text.AppendLine($"As {CouncilRoles.Display(query.Role)} on {ProviderNames.Display(query.Provider)} {query.Model} at {where}:");
        if (agent is not null)
            text.AppendLine($"- This agent: {Headline(agent)}");
        if (user is not null)
            text.AppendLine($"- All your agents: {Headline(user)}");
        if (system is not null)
            text.AppendLine($"- All users (anonymous counts and ratios): {Headline(system)}");

        var outcomes = trades.Select(t => (t.ClosedAt, Text: Describe(t)))
            .Concat(rejections.Select(r => (At: r.At, Text: Describe(r))))
            .OrderByDescending(o => o.Item1)
            .Take(recent)
            .Select(o => o.Text)
            .ToList();
        if (outcomes.Count > 0)
            text.AppendLine($"- Recent outcomes, newest first: {string.Join("; ", outcomes)}.");

        var result = text.ToString().TrimEnd();
        return result.Length <= MaxLength ? result : result[..MaxLength];
    }

    /// <summary>One line of counts and ratios; a figure without a denominator is left out.</summary>
    internal static string Headline(ExperienceMetrics m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var parts = new List<string>();
        var turns = $"{m.Decisions} turn{Plural(m.Decisions)} ({m.Approvals} approval{Plural(m.Approvals)}, {m.Rejections} rejection{Plural(m.Rejections)}, {m.Failures} failed";
        if (m.AverageLatencyMs is { } latency)
            turns += $", {Seconds(latency)} s average";
        parts.Add(turns + ")");
        if (m.Trades > 0)
        {
            var trade = $"{m.Trades} trade{Plural(m.Trades)}: win rate {Percent(m.WinRate)}, average {R(m.AverageR)}";
            if (m.Expectancy is not null)
                trade += $", expectancy {R(m.Expectancy)} net of fees";
            if (m.ProfitFactor is { } pf)
                trade += $", profit factor {pf.ToString("0.00", CultureInfo.InvariantCulture)}";
            parts.Add(trade);
        }
        else
        {
            parts.Add("no closed trade yet");
        }

        if (m.RejectionsResolved > 0)
            parts.Add($"{m.RejectionsResolved} rejection{Plural(m.RejectionsResolved)} resolved, {Percent(m.RejectionAccuracy)} avoided a loss");
        if (m.ClampRate is not null)
            parts.Add($"outputs clamped by the hard limits {Percent(m.ClampRate)} of the time");
        return string.Join("; ", parts) + ".";
    }

    internal static string Describe(RecentTrade trade)
    {
        ArgumentNullException.ThrowIfNull(trade);
        var how = trade.Reason switch
        {
            TradeExitReason.Stop => "stop",
            TradeExitReason.Target => "target",
            TradeExitReason.Council => "closed by the council",
            TradeExitReason.KillSwitch => "kill switch",
            TradeExitReason.External => "closed on the exchange",
            TradeExitReason.Manual => "closed by hand",
            _ => "closed",
        };
        return trade.Overruled
            ? $"{Date(trade.ClosedAt)}: you said no_trade, the trade was opened anyway and made {R(trade.R)} ({how})"
            : $"{Date(trade.ClosedAt)}: {R(trade.R)} ({how})";
    }

    internal static string Describe(RecentRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        var what = rejection.Result switch
        {
            ShadowResult.HitStop => "would have hit the stop (right call)",
            ShadowResult.HitTarget => $"would have hit the target for {R(rejection.R)} (missed)",
            _ => rejection.Resolution == ShadowDetail.NotFilled ? "the entry would not have filled" : "expired unresolved",
        };
        return $"{Date(rejection.At)}: rejected, {what}";
    }

    internal static string Market(MarketType market) => market == MarketType.Spot ? "Spot" : "USDT-M Futures";

    internal static string R(decimal? value) => value is { } r ? $"{(r >= 0 ? "+" : "")}{r.ToString("0.00", CultureInfo.InvariantCulture)}R" : "n/a";

    internal static string Percent(decimal? ratio) => ratio is { } r ? $"{Math.Round(r * 100, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}%" : "n/a";

    private static string Seconds(int milliseconds) => (milliseconds / 1000m).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Date(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Plural(int count) => count == 1 ? "" : "s";
}
