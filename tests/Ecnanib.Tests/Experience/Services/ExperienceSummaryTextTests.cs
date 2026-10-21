using Ecnanib.Ai.Abstractions;
using Ecnanib.Council.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Experience.Services;

namespace Ecnanib.Tests.Experience.Services;

/// <summary>The compact summary a role prompt carries (FR-EXP-06).</summary>
public class ExperienceSummaryTextTests
{
    private static readonly ExperienceQuery Query = new(Guid.NewGuid(), Guid.NewGuid(), AiProvider.Claude, "claude-x", CouncilRole.RiskManager, CandleInterval.OneHour, MarketType.UsdFutures);

    private static readonly DateTimeOffset Day = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NothingRecordedIsNull() =>
        Assert.Null(ExperienceSummaryText.Compose(Query, null, null, null, [], [], 5));

    [Fact]
    public void HeadlinesCoverTheThreeScopesAndTheRecentOutcomesComeNewestFirst()
    {
        var agent = new ExperienceMetrics { Decisions = 3, Approvals = 1, Rejections = 2, RiskManagerTurns = 3, Clamped = 1, TotalLatencyMs = 12_600 }.Derive();
        var user = new ExperienceMetrics { Decisions = 10, Approvals = 4, Rejections = 5, Failures = 1, Trades = 4, Wins = 3, Losses = 1, SumR = 5m, GrossProfitR = 6m, GrossLossR = 1m, NetTrades = 4, SumNetR = 4.4m, RejectionsResolved = 4, RejectionsMissedWins = 1, TotalLatencyMs = 40_000 }.Derive();
        var system = new ExperienceMetrics { Decisions = 100, Approvals = 40, Rejections = 50, Trades = 30, Wins = 15, Losses = 15, SumR = 3m, GrossProfitR = 20m, GrossLossR = 17m, TotalLatencyMs = 300_000 }.Derive();
        var trades = new[]
        {
            new RecentTrade(Day.AddDays(-2), 1.8m, TradeExitReason.Target, false),
            new RecentTrade(Day.AddDays(-5), -1m, TradeExitReason.Stop, false),
            new RecentTrade(Day.AddDays(-1), 0.4m, TradeExitReason.Council, true),
        };
        var rejections = new[]
        {
            new RecentRejection(Day.AddDays(-3), ShadowResult.HitStop, -1m, ShadowDetail.ByStop),
            new RecentRejection(Day.AddDays(-4), ShadowResult.HitTarget, 2.1m, ShadowDetail.ByTarget),
            new RecentRejection(Day.AddDays(-6), ShadowResult.Expired, null, ShadowDetail.NotFilled),
        };

        var text = ExperienceSummaryText.Compose(Query, agent, user, system, trades, rejections, 4);

        Assert.NotNull(text);
        Assert.StartsWith("As Risk Manager on Claude claude-x at 1h USDT-M Futures:", text);
        Assert.Contains("- This agent: 3 turns (1 approval, 2 rejections, 0 failed, 4.2 s average); no closed trade yet; outputs clamped by the hard limits 33% of the time.", text);
        Assert.Contains("- All your agents: 10 turns (4 approvals, 5 rejections, 1 failed, 4.0 s average); 4 trades: win rate 75%, average +1.25R, expectancy +1.10R net of fees, profit factor 6.00; 4 rejections resolved, 75% avoided a loss.", text);
        Assert.Contains("- All users (anonymous counts and ratios): 100 turns", text);
        Assert.Contains("profit factor 1.18", text);
        var recent = text.Split('\n').Single(l => l.StartsWith("- Recent outcomes", StringComparison.Ordinal));
        Assert.Equal("- Recent outcomes, newest first: 2026-01-04: you said no_trade, the trade was opened anyway and made +0.40R (closed by the council); 2026-01-03: +1.80R (target); 2026-01-02: rejected, would have hit the stop (right call); 2026-01-01: rejected, would have hit the target for +2.10R (missed).", recent);
        Assert.DoesNotContain("2025-12-31", text);
        Assert.DoesNotContain(Query.UserId.ToString(), text);
        Assert.DoesNotContain(Query.AgentId.ToString(), text);
        Assert.True(text.Length <= ExperienceSummaryText.MaxLength);
    }

    [Fact]
    public void AnUnfilledRejectionAndAnExpiredOneAreSaidAsSuch()
    {
        Assert.Equal("2026-01-05: rejected, the entry would not have filled", ExperienceSummaryText.Describe(new RecentRejection(Day, ShadowResult.Expired, null, ShadowDetail.NotFilled)));
        Assert.Equal("2026-01-05: rejected, expired unresolved", ExperienceSummaryText.Describe(new RecentRejection(Day, ShadowResult.Expired, 0.3m, ShadowDetail.Horizon)));
        Assert.Equal("2026-01-05: −1.00R (kill switch)".Replace('−', '-'), ExperienceSummaryText.Describe(new RecentTrade(Day, -1m, TradeExitReason.KillSwitch, false)));
    }

    [Fact]
    public void OnlyTheRecentOutcomesCanBeSaidWhenNoStatisticIsStoredYet()
    {
        var text = ExperienceSummaryText.Compose(Query, null, null, null, [new RecentTrade(Day, 2m, TradeExitReason.Target, false)], [], 5);

        Assert.NotNull(text);
        Assert.DoesNotContain("- This agent", text);
        Assert.Contains("- Recent outcomes, newest first: 2026-01-05: +2.00R (target).", text);
    }
}
