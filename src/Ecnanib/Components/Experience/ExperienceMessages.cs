using System.Globalization;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Components.Agents;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Experience.Abstractions;
using Ecnanib.Keys.Abstractions;

namespace Ecnanib.Components.Experience;

/// <summary>User-facing texts and number formats of the experience views (FR-EXP-02, FR-EXP-03, FR-EXP-07, FR-EXP-08).</summary>
internal static class ExperienceMessages
{
    public const string LeaderboardIntro = "Every council turn and every closed trade is attributed to the provider, model and role that produced it. Your rows cover your own agents; the system-wide rows add every user's, as counts and ratios only. R is the result over the risk taken at entry: average R is gross, expectancy is net of fees and funding.";

    public const string NoStatistics = "No statistics yet: they are computed a few minutes after the first evaluations and refreshed on the configured interval.";

    public const string NoRows = "Nothing recorded for this selection.";

    public const string NoTrades = "No closed trade yet.";

    public const string AgentIntro = "This agent's council as the statistics see it, per role, provider and model it has used; the current assignment is marked. Trades link to the evaluation that opened them.";

    public const string NotAvailable = "n/a";

    public static string R(decimal? value) => value is { } r ? $"{(r >= 0 ? "+" : "−")}{Math.Abs(r).ToString("0.00", CultureInfo.InvariantCulture)}R" : NotAvailable;

    public static string RClass(decimal? value) => value switch { > 0 => "r-positive", < 0 => "r-negative", _ => "" };

    public static string Percent(decimal? ratio) => ratio is { } r ? Math.Round(r * 100, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%" : NotAvailable;

    public static string Factor(decimal? value) => value is { } f ? f.ToString("0.00", CultureInfo.InvariantCulture) : NotAvailable;

    public static string Latency(int? milliseconds) => milliseconds is { } ms ? (ms / 1000m).ToString("0.0", CultureInfo.InvariantCulture) + " s" : NotAvailable;

    public static string Money(decimal? value) => value is { } m ? m.ToString("0.00", CultureInfo.InvariantCulture) : NotAvailable;

    public static string Price(decimal? value) => value is { } p ? p.ToString("0.############################", CultureInfo.InvariantCulture) : NotAvailable;

    /// <summary>"3d 4h", "5h 20m" or "12m".</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h";
        return duration.TotalHours >= 1 ? $"{duration.Hours}h {duration.Minutes}m" : $"{duration.Minutes}m";
    }

    public static string ExitReason(TradeExitReason? reason) => reason switch
    {
        TradeExitReason.Stop => "stop",
        TradeExitReason.Target => "target",
        TradeExitReason.Council => "council",
        TradeExitReason.KillSwitch => "kill switch",
        TradeExitReason.External => "exchange",
        TradeExitReason.Manual => "manual",
        _ => "",
    };

    public static string Result(ShadowResult result) => result switch
    {
        ShadowResult.Pending => "pending",
        ShadowResult.HitTarget => "would have hit the target",
        ShadowResult.HitStop => "would have hit the stop",
        ShadowResult.Expired => "expired",
        _ => result.ToString().ToLowerInvariant(),
    };

    public static string ResultClass(ShadowResult result) => "status-" + result switch
    {
        ShadowResult.HitTarget => "hittarget",
        ShadowResult.HitStop => "hitstop",
        _ => result.ToString().ToLowerInvariant(),
    };

    public static string Reason(string reason) => reason switch
    {
        "veto" => "the Risk Manager rejected the setup",
        "limits" => "the hard limits rejected the Risk Manager's entry",
        "no-decision" => "the council made no decision",
        _ => "nothing was opened",
    };

    public static string Resolution(string? resolution) => resolution switch
    {
        "stop" => "the stop was touched first",
        "target" => "the target was touched first",
        "not-filled" => "the limit entry was never filled within the entry timeout",
        "horizon" => "neither the stop nor the target was touched within the horizon",
        "stale" => "no evaluation followed it any more (the agent stopped or trades something else)",
        "no-detail" => "its setup was not recorded",
        "invalid" => "its setup could not be followed",
        _ => "",
    };

    /// <summary>One sentence for the deliberation page (FR-EXP-03).</summary>
    public static string Shadow(ShadowOutcomeView shadow)
    {
        ArgumentNullException.ThrowIfNull(shadow);
        var head = $"Shadow outcome: {Reason(shadow.Reason)}, so the strategy's entry, stop and first target are followed instead.";
        return shadow.Result switch
        {
            ShadowResult.Pending => $"{head} Still pending after {Plural(shadow.CandlesSeen, "candle", "candles")}; it expires {Plural(shadow.HorizonCandles, "candle", "candles")} after the fill, or unfilled after {Plural(shadow.EntryTimeoutCandles, "candle", "candles")}.",
            ShadowResult.HitTarget => $"{head} It {Result(shadow.Result)} for {R(shadow.RMultiple)}: a missed win.",
            ShadowResult.HitStop => $"{head} It {Result(shadow.Result)} ({R(shadow.RMultiple)}): a loss avoided.",
            _ => $"{head} It expired: {Resolution(shadow.Resolution)}{(shadow.RMultiple is { } r ? $"; it stood at {R(r)} at the last close" : "")}.",
        };
    }

    /// <summary>"Risk Manager: Claude claude-x (expectancy +0.50R over 12 trades)".</summary>
    public static string Suggestion(CouncilSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        return $"{CouncilRoles.Display(suggestion.Role)}: {ProviderNames.Display(suggestion.Provider)} {suggestion.Model} (expectancy {R(suggestion.Expectancy)} over {Plural(suggestion.Trades, "trade", "trades")})";
    }

    /// <summary>The council hint on the agent page (FR-EXP-08) for the roles whose suggestion differs from the assignment.</summary>
    public static string Suggestions(IReadOnlyList<CouncilSuggestion> suggestions, CandleInterval timeframe, MarketType market)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        return $"From your experience at {AgentMessages.Timeframe(timeframe)} {AgentMessages.Market(market)}, the best expectancy so far: "
               + string.Join("; ", suggestions.Select(Suggestion)) + ".";
    }

    public static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";
}

/// <summary>The leaderboard filter in the query string (FR-EXP-07): lenient parsing, so a hand-edited value means "all" rather than an error.</summary>
internal static class ExperienceFilters
{
    public const string RoleName = "role";

    public const string TimeframeName = "timeframe";

    public const string MarketName = "market";

    public static readonly IReadOnlyList<(string Value, string Label, MarketType Market)> Markets =
    [
        ("spot", "Spot", MarketType.Spot), ("futures", "USDⓈ-M Futures", MarketType.UsdFutures),
    ];

    public static ExperienceFilter Parse(string? role, string? timeframe, string? market) => new(ParseRole(role), ParseTimeframe(timeframe), ParseMarket(market));

    public static CouncilRole? ParseRole(string? value) =>
        Enum.TryParse<CouncilRole>(value, ignoreCase: true, out var role) && Enum.IsDefined(role) ? role : null;

    public static CandleInterval? ParseTimeframe(string? value) =>
        AgentsOptions.Timeframes.Where(t => string.Equals(AgentsOptions.KeyOf(t), value, StringComparison.OrdinalIgnoreCase)).Select(t => (CandleInterval?)t).FirstOrDefault();

    public static MarketType? ParseMarket(string? value) =>
        Markets.Where(m => string.Equals(m.Value, value, StringComparison.OrdinalIgnoreCase)).Select(m => (MarketType?)m.Market).FirstOrDefault();

    public static string RoleValue(CouncilRole role) => CouncilRoles.CamelCase(role);

    public static string TimeframeValue(CandleInterval timeframe) => AgentsOptions.KeyOf(timeframe);

    public static string MarketValue(MarketType market) => Markets.First(m => m.Market == market).Value;

    /// <summary>The filter as query-string pairs, empty when nothing is filtered; appended to a page's own query.</summary>
    public static string Query(ExperienceFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var parts = new List<string>();
        if (filter.Role is { } role)
            parts.Add($"{RoleName}={RoleValue(role)}");
        if (filter.Timeframe is { } timeframe)
            parts.Add($"{TimeframeName}={TimeframeValue(timeframe)}");
        if (filter.MarketType is { } market)
            parts.Add($"{MarketName}={MarketValue(market)}");
        return string.Join("&", parts);
    }

    /// <summary>"Risk Manager, 1H, Spot" or "all roles, all timeframes, all markets".</summary>
    public static string Describe(ExperienceFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return string.Join(", ",
            filter.Role is { } role ? CouncilRoles.Display(role) : "all roles",
            filter.Timeframe is { } timeframe ? AgentMessages.Timeframe(timeframe) : "all timeframes",
            filter.MarketType is { } market ? AgentMessages.Market(market) : "all markets");
    }
}
