using Ecnanib.Agents;
using Ecnanib.Catalog;
using Ecnanib.Experience;
using Ecnanib.Shared;
using Ecnanib.Tests.Experience;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// The strongest candidate of each role is the model with the highest strength score in it
/// (FR-TEAM-4). A model is scored from its Live data where there is enough, and from its Demo
/// data otherwise, and says which (FR-EXP-9). A slice without enough data recommends nothing
/// (FR-EXP-4). A role is recommended its strongest candidate only when that scores above the
/// model that the role has. See "Role by strength" in docs/agents.md.
/// </summary>
/// <remarks>
/// The rule reads nothing, so these tests store nothing: they hand it slices. A slice of these
/// tests is damped by 4 and has enough samples from 4 on, so one of 4 samples has half of its
/// measure as its score, and one of 12 three quarters.
/// </remarks>
public class RoleRecommendationTests
{
    private const int MinSamples = 4;
    private const int Damping = 4;

    private static readonly CatalogModel Claude = SampleModels.Of("claude", "Claude", "claude-a");
    private static readonly CatalogModel Gemini = SampleModels.Of("gemini", "Gemini", "gemini-a");
    private static readonly CatalogModel Grok = SampleModels.Of("grok", "Grok", "grok-a");

    [Fact]
    public void A_model_is_scored_by_its_Live_slice_where_that_has_enough_samples_and_by_its_Demo_slice_otherwise()
    {
        // Claude has enough in Live, so its Live slice counts, whatever its larger Demo slice
        // says. Gemini has 3 samples in Live, which are not enough, so its Demo slice counts.
        var recommendations = Of(
            live: [Slice(Claude, Role.Strategist, 0.2m, 4), Slice(Gemini, Role.Strategist, 2m, 3)],
            demo: [Slice(Claude, Role.Strategist, 0.8m, 12), Slice(Gemini, Role.Strategist, 0.4m, 12)],
            Claude,
            Gemini);

        Assert.Equal(
            [("gemini-a", TradingMode.Demo, 0.3m, 12), ("claude-a", TradingMode.Live, 0.1m, 4)],
            Told(recommendations, Role.Strategist));

        // The rule says what it went by, for a screen to show (FR-EXP-4).
        Assert.Equal(
            (Market.Spot, Timeframe.OneHour, MinSamples, Damping),
            (recommendations.Market, recommendations.Timeframe, recommendations.MinSamples, recommendations.Damping));
    }

    [Fact]
    public void A_model_with_enough_Live_samples_and_no_measure_has_no_score_whatever_its_Demo_slice_says()
    {
        // A Risk Manager that vetoed nothing in Live has a sample there, and no measure. Live
        // has enough, so Live is gone by: nothing is mixed, and the model has no score.
        var recommendations = Of(
            live: [Slice(Claude, Role.RiskManager, measure: null, 6)],
            demo: [Slice(Claude, Role.RiskManager, 0.5m, 12)],
            Claude);

        Assert.Empty(Told(recommendations, Role.RiskManager));
        Assert.Null(recommendations.StrongestFor(Role.RiskManager));
    }

    [Fact]
    public void A_slice_below_the_minimum_sample_recommends_nothing()
    {
        // Three samples in each mode: not enough in either (FR-EXP-4).
        var recommendations = Of(
            live: [Slice(Claude, Role.MarketAnalyst, 0.5m, 3)],
            demo: [Slice(Claude, Role.MarketAnalyst, 0.5m, 3)],
            Claude);

        Assert.Empty(Told(recommendations, Role.MarketAnalyst));
        Assert.Null(recommendations.StrongestFor(Role.MarketAnalyst));
        Assert.Null(recommendations.RecommendedFor(Role.MarketAnalyst, currentModelId: null));
        Assert.Null(recommendations.RecommendedFor(Role.MarketAnalyst, Gemini.Id));
    }

    [Fact]
    public void Only_a_model_that_the_user_can_select_is_a_candidate_and_one_that_is_not_keeps_its_score()
    {
        // Claude scores highest, and the user's keys cannot use it now. Gemini is the strongest
        // of what a role can be given.
        var recommendations = Of(
            live: [],
            demo: [Slice(Claude, Role.Strategist, 0.8m, 4), Slice(Gemini, Role.Strategist, 0.4m, 4)],
            Gemini);

        Assert.Equal("gemini-a", recommendations.StrongestFor(Role.Strategist)?.Slice.Model.Identifier);
        Assert.Equal((0.4m, false), (recommendations.Of(Role.Strategist, Claude.Id)!.Score, recommendations.Of(Role.Strategist, Claude.Id)!.Candidate));

        // A role that has the model which cannot be selected is compared by that model's score:
        // the candidate does not score above it, so the role stays as it is.
        Assert.Null(recommendations.RecommendedFor(Role.Strategist, Claude.Id));
    }

    [Fact]
    public void Only_a_model_that_searches_live_is_a_candidate_for_the_Sentiment_Analyst()
    {
        // The report of a Sentiment Analyst that cannot search is given no weight (FR-CYC-3),
        // so its model is no candidate for that role, however its calls were scored. For the
        // Market Analyst a search counts for nothing.
        var recommendations = RoleRecommendations.Of(
            Market.Spot,
            Timeframe.OneHour,
            View(TradingMode.Live),
            View(
                TradingMode.Demo,
                Slice(Claude, Role.SentimentAnalyst, 0.4m, 4),
                Slice(Grok, Role.SentimentAnalyst, 0.2m, 4),
                Slice(Claude, Role.MarketAnalyst, 0.4m, 4),
                Slice(Grok, Role.MarketAnalyst, 0.2m, 4)),
            [Selectable(Claude, liveSearch: false), Selectable(Grok, liveSearch: true)]);

        Assert.Equal("grok-a", recommendations.StrongestFor(Role.SentimentAnalyst)?.Slice.Model.Identifier);
        Assert.Equal("claude-a", recommendations.StrongestFor(Role.MarketAnalyst)?.Slice.Model.Identifier);

        // The model that cannot search keeps its score, which the model that the role has is compared by.
        Assert.Null(recommendations.RecommendedFor(Role.SentimentAnalyst, Claude.Id));
        Assert.Equal("grok-a", recommendations.RecommendedFor(Role.SentimentAnalyst, currentModelId: null)?.Slice.Model.Identifier);
    }

    [Fact]
    public void The_scored_models_are_in_the_order_of_their_scores_then_their_samples_then_their_names()
    {
        var other = SampleModels.Of("claude", "Claude", "claude-b");

        // Gemini scores 0.3. Grok and both models of Claude score 0.15: Grok from 12 samples,
        // and the two of Claude from 4 each, which their identifiers tell apart.
        var recommendations = Of(
            live: [],
            demo:
            [
                Slice(other, Role.Strategist, 0.3m, 4),
                Slice(Grok, Role.Strategist, 0.2m, 12),
                Slice(Claude, Role.Strategist, 0.3m, 4),
                Slice(Gemini, Role.Strategist, 0.6m, 4),
            ],
            Claude,
            other,
            Gemini,
            Grok);

        Assert.Equal(
            ["gemini-a", "grok-a", "claude-a", "claude-b"],
            recommendations.Scored[Role.Strategist].Select(model => model.Slice.Model.Identifier));
    }

    // The score of the candidate, the score of the model that the role has, and whether the
    // role is recommended the candidate. A slice of 4 samples has half of its measure as its score.
    [Theory]
    [InlineData(0.4, 0.2, true)]
    [InlineData(0.2, 0.2, false)]
    [InlineData(0.2, 0.4, false)]
    [InlineData(-0.2, -0.4, true)]
    [InlineData(-0.4, -0.2, false)]
    public void A_role_is_given_its_strongest_candidate_only_when_it_scores_above_the_model_that_the_role_has(
        double candidate, double current, bool recommended)
    {
        // The model that the role has cannot be selected any more, so it is no candidate, and
        // is compared by its score all the same.
        var recommendations = Of(
            live: [],
            demo: [Slice(Gemini, Role.Strategist, (decimal)candidate, 4), Slice(Claude, Role.Strategist, (decimal)current, 4)],
            Gemini);

        Assert.Equal(recommended, recommendations.RecommendedFor(Role.Strategist, Claude.Id) is not null);
    }

    [Theory]
    [InlineData(0.2, true)]
    [InlineData(0, false)]
    [InlineData(-0.2, false)]
    public void A_model_with_no_score_counts_as_zero(double candidate, bool recommended)
    {
        // The model that the role has has 3 samples: not enough, so it has no score. Neither
        // has a role that has no model. A candidate is recommended when it scores above 0.
        var recommendations = Of(
            live: [],
            demo: [Slice(Gemini, Role.Strategist, (decimal)candidate, 4), Slice(Claude, Role.Strategist, 5m, 3)],
            Gemini,
            Claude);

        Assert.Equal(recommended, recommendations.RecommendedFor(Role.Strategist, Claude.Id) is not null);
        Assert.Equal(recommended, recommendations.RecommendedFor(Role.Strategist, currentModelId: null) is not null);
    }

    [Fact]
    public void A_role_that_has_its_strongest_candidate_is_recommended_nothing()
    {
        var recommendations = Of(
            live: [],
            demo: [Slice(Gemini, Role.Strategist, 0.4m, 4), Slice(Claude, Role.Strategist, 0.2m, 4)],
            Gemini,
            Claude);

        Assert.Null(recommendations.RecommendedFor(Role.Strategist, Gemini.Id));
        Assert.Equal("gemini-a", recommendations.RecommendedFor(Role.Strategist, Claude.Id)?.Slice.Model.Identifier);
    }

    [Fact]
    public void Equal_scores_keep_the_model_that_the_role_has()
    {
        // Both score 0.1, and Claude comes first by its name. The role has Gemini, and keeps it.
        var recommendations = Of(
            live: [],
            demo: [Slice(Gemini, Role.Strategist, 0.2m, 4), Slice(Claude, Role.Strategist, 0.2m, 4)],
            Gemini,
            Claude);

        Assert.Equal("claude-a", recommendations.StrongestFor(Role.Strategist)?.Slice.Model.Identifier);
        Assert.Null(recommendations.RecommendedFor(Role.Strategist, Gemini.Id));
    }

    [Fact]
    public void A_slice_of_another_market_or_timeframe_or_role_recommends_nothing()
    {
        // A score is of one model in one role, on one market and one timeframe (FR-EXP-4):
        // nothing is recommended from a slice of another.
        var recommendations = RoleRecommendations.Of(
            Market.Spot,
            Timeframe.OneHour,
            View(TradingMode.Live),
            View(
                TradingMode.Demo,
                RoleStrength.Of(Claude, Role.Strategist, Market.Futures, Timeframe.OneHour, new RoleMeasure(0.4m, 12), MinSamples, Damping),
                RoleStrength.Of(Claude, Role.Strategist, Market.Spot, Timeframe.FourHours, new RoleMeasure(0.4m, 12), MinSamples, Damping),
                Slice(Claude, Role.RiskManager, 0.4m, 12)),
            [Selectable(Claude)]);

        Assert.Null(recommendations.StrongestFor(Role.Strategist));
        Assert.Equal("claude-a", recommendations.StrongestFor(Role.RiskManager)?.Slice.Model.Identifier);
    }

    [Fact]
    public void The_slices_of_each_mode_are_handed_over_as_the_slices_of_that_mode()
    {
        // Demo and Live are never mixed up (FR-BIN-11): slices that are handed over as those of
        // the other mode are refused.
        Assert.Throws<ArgumentException>(() =>
            RoleRecommendations.Of(Market.Spot, Timeframe.OneHour, View(TradingMode.Demo), View(TradingMode.Live), []));
    }

    /// <summary>The scored models of a market and a timeframe, from slices of each mode, for a user who can select the models named.</summary>
    private static RoleRecommendations Of(RoleStrength[] live, RoleStrength[] demo, params CatalogModel[] selectable) =>
        RoleRecommendations.Of(
            Market.Spot,
            Timeframe.OneHour,
            View(TradingMode.Live, live),
            View(TradingMode.Demo, demo),
            [.. selectable.Select(model => Selectable(model))]);

    private static RoleStrengthView View(TradingMode mode, params RoleStrength[] slices) =>
        new(mode, MinSamples, Damping, slices, CallsAwaiting: 0, CallsWithoutCandles: 0, ShadowTradesOpen: 0, ShadowTradesWithoutResult: 0);

    private static RoleStrength Slice(CatalogModel model, Role role, decimal? measure, int samples) =>
        RoleStrength.Of(model, role, Market.Spot, Timeframe.OneHour, new RoleMeasure(measure, samples), MinSamples, Damping);

    private static SelectableModel Selectable(CatalogModel model, bool liveSearch = false) =>
        new(model.Id, model.Provider, model.Identifier, model.DisplayName, liveSearch);

    /// <summary>The scored models of a role, in their order: each with its identifier, the mode it is gone by, its score, and its sample.</summary>
    private static IReadOnlyList<(string Model, TradingMode Basis, decimal Score, int Samples)> Told(RoleRecommendations recommendations, Role role) =>
    [
        .. recommendations.Scored.GetValueOrDefault(role, [])
            .Select(model => (model.Slice.Model.Identifier, model.Basis, model.Score, model.Slice.Measure.Samples)),
    ];
}
