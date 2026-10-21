using Ecnanib.Agents.Abstractions;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Components.Experience;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Experience.Abstractions;

namespace Ecnanib.Tests.Experience.Pages;

/// <summary>The texts and formats of the experience pages (FR-EXP-02, FR-EXP-03, FR-EXP-07, FR-EXP-08).</summary>
public class ExperienceMessagesTests
{
    [Fact]
    public void NumbersAreFormattedWithTheirUnitOrNa()
    {
        Assert.Equal("+1.80R", ExperienceMessages.R(1.8m));
        Assert.Equal("−1.00R", ExperienceMessages.R(-1m));
        Assert.Equal("+0.00R", ExperienceMessages.R(0m));
        Assert.Equal("n/a", ExperienceMessages.R(null));
        Assert.Equal("r-positive", ExperienceMessages.RClass(0.1m));
        Assert.Equal("r-negative", ExperienceMessages.RClass(-0.1m));
        Assert.Equal("", ExperienceMessages.RClass(null));
        Assert.Equal("67%", ExperienceMessages.Percent(0.6667m));
        Assert.Equal("n/a", ExperienceMessages.Percent(null));
        Assert.Equal("1.80", ExperienceMessages.Factor(1.8m));
        Assert.Equal("4.2 s", ExperienceMessages.Latency(4200));
        Assert.Equal("3d 4h", ExperienceMessages.Duration(TimeSpan.FromHours(76)));
        Assert.Equal("5h 20m", ExperienceMessages.Duration(TimeSpan.FromMinutes(320)));
        Assert.Equal("12m", ExperienceMessages.Duration(TimeSpan.FromMinutes(12)));
        Assert.Equal("1.5", ExperienceMessages.Price(1.5m));
    }

    [Fact]
    public void ShadowOutcomesAreOneSentenceEach()
    {
        Assert.Equal("Shadow outcome: the Risk Manager rejected the setup, so the strategy's entry, stop and first target are followed instead. Still pending after 3 candles; it expires 30 candles after the fill, or unfilled after 1 candle.",
            ExperienceMessages.Shadow(new ShadowOutcomeView(ShadowResult.Pending, "veto", null, null, 3, 1, 30, null, null)));
        Assert.Equal("Shadow outcome: the hard limits rejected the Risk Manager's entry, so the strategy's entry, stop and first target are followed instead. It would have hit the target for +2.00R: a missed win.",
            ExperienceMessages.Shadow(new ShadowOutcomeView(ShadowResult.HitTarget, "limits", "target", 2m, 5, 1, 30, null, null)));
        Assert.Equal("Shadow outcome: the council made no decision, so the strategy's entry, stop and first target are followed instead. It would have hit the stop (−1.00R): a loss avoided.",
            ExperienceMessages.Shadow(new ShadowOutcomeView(ShadowResult.HitStop, "no-decision", "stop", -1m, 2, 1, 30, null, null)));
        Assert.Equal("Shadow outcome: the Risk Manager rejected the setup, so the strategy's entry, stop and first target are followed instead. It expired: the limit entry was never filled within the entry timeout.",
            ExperienceMessages.Shadow(new ShadowOutcomeView(ShadowResult.Expired, "veto", "not-filled", null, 1, 1, 30, null, null)));
        Assert.Equal("Shadow outcome: the Risk Manager rejected the setup, so the strategy's entry, stop and first target are followed instead. It expired: neither the stop nor the target was touched within the horizon; it stood at +0.30R at the last close.",
            ExperienceMessages.Shadow(new ShadowOutcomeView(ShadowResult.Expired, "veto", "horizon", 0.3m, 31, 1, 30, null, null)));
        Assert.Equal("status-hittarget", ExperienceMessages.ResultClass(ShadowResult.HitTarget));
        Assert.Equal("status-pending", ExperienceMessages.ResultClass(ShadowResult.Pending));
    }

    [Fact]
    public void SuggestionsNameTheRoleProviderModelAndSample()
    {
        var suggestions = new[] { new CouncilSuggestion(CouncilRole.RiskManager, AiProvider.Claude, "claude-x", 0.5m, 12), new CouncilSuggestion(CouncilRole.MarketAnalyst, AiProvider.Gpt, "gpt-y", 0.25m, 1) };

        Assert.Equal("Risk Manager: Claude claude-x (expectancy +0.50R over 12 trades)", ExperienceMessages.Suggestion(suggestions[0]));
        Assert.Equal("From your experience at 1H Spot, the best expectancy so far: Risk Manager: Claude claude-x (expectancy +0.50R over 12 trades); Market Analyst: GPT gpt-y (expectancy +0.25R over 1 trade).",
            ExperienceMessages.Suggestions(suggestions, CandleInterval.OneHour, MarketType.Spot));
    }

    [Fact]
    public void TheFilterRoundTripsThroughTheQueryStringAndTakesHandEditedValuesAsAll()
    {
        var filter = new ExperienceFilter(CouncilRole.RiskManager, CandleInterval.FourHours, MarketType.UsdFutures);

        Assert.Equal("role=riskManager&timeframe=4H&market=futures", ExperienceFilters.Query(filter));
        Assert.Equal(filter, ExperienceFilters.Parse("riskManager", "4h", "FUTURES"));
        Assert.Equal(ExperienceFilter.None, ExperienceFilters.Parse("boss", "1w", "options"));
        Assert.Equal(ExperienceFilter.None, ExperienceFilters.Parse(null, null, null));
        Assert.Equal("", ExperienceFilters.Query(ExperienceFilter.None));
        Assert.Equal("Risk Manager, 4H, USDⓈ-M Futures", ExperienceFilters.Describe(filter));
        Assert.Equal("all roles, all timeframes, all markets", ExperienceFilters.Describe(ExperienceFilter.None));
    }
}
