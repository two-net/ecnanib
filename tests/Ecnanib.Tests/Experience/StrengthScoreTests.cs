using Ecnanib.Catalog;
using Ecnanib.Experience;
using Ecnanib.Shared;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// A strength score per model, role, market, and timeframe is the role's measure damped toward
/// zero for small samples: measure × n ÷ (n + 20), where n is the sample size. Slices with fewer
/// than 20 samples show "not enough data" and are excluded from recommendations (FR-EXP-4). See
/// "The strength score" in docs/experience.md.
/// </summary>
public class StrengthScoreTests
{
    // The values of the specification, which the tracked file carries.
    private const int Damping = 20;
    private const int MinSamples = 20;

    [Theory]

    // As many samples as the damping: half of the measure.
    [InlineData("0.5", 20, "0.25")]
    [InlineData("1.2", 20, "0.6")]

    // More samples, and more of the measure: 30 of 50, 60 of 80, and 180 of 200.
    [InlineData("0.5", 30, "0.3")]
    [InlineData("2", 60, "1.5")]
    [InlineData("1", 180, "0.9")]

    // Fewer, and less of it: 5 of 25.
    [InlineData("1", 5, "0.2")]

    // A measure of nothing has a score of nothing, whatever its sample.
    [InlineData("0", 500, "0")]
    public void The_strength_score_is_the_measure_times_n_over_n_plus_the_damping(string measure, int samples, string score)
    {
        Assert.Equal(decimal.Parse(score), StrengthScore.Of(decimal.Parse(measure), samples, Damping));
    }

    [Fact]
    public void The_formula_is_measure_times_n_over_n_plus_the_damping_for_every_sample_size()
    {
        // Held against the formula as the specification writes it, for a measure that is no
        // round number and for every sample size up to a few hundred.
        const decimal measure = 0.137m;

        Assert.All(
            Enumerable.Range(1, 400),
            samples => Assert.Equal(measure * samples / (samples + Damping), StrengthScore.Of(measure, samples, Damping)));
    }

    [Fact]
    public void A_small_sample_is_damped_toward_zero_and_keeps_its_sign()
    {
        // The same measure, of more and more samples: the score rises toward the measure, and
        // never reaches it.
        var scores = new[] { 1, 5, 20, 100, 1000, 100000 }.Select(samples => StrengthScore.Of(0.4m, samples, Damping)).ToList();

        Assert.Equal(scores.Order(), scores);
        Assert.All(scores, score => Assert.InRange(score, 0m, 0.4m));
        Assert.True(scores[0] < 0.02m);
        Assert.True(scores[^1] > 0.3999m && scores[^1] < 0.4m);

        // A measure below zero is damped toward zero from below: its score is the same amount, with its sign.
        Assert.Equal(-StrengthScore.Of(0.4m, 5, Damping), StrengthScore.Of(-0.4m, 5, Damping));
        Assert.Equal(-0.08m, StrengthScore.Of(-0.4m, 5, Damping));
    }

    [Fact]
    public void The_damping_is_the_one_that_it_is_given()
    {
        // Another constant of the configuration file gives another score: 20 of 45, and 20 of 20.
        Assert.Equal(0.4m, StrengthScore.Of(0.9m, 20, damping: 25));
        Assert.Equal(0.9m, StrengthScore.Of(0.9m, 20, damping: 0));
    }

    [Fact]
    public void No_sample_and_no_damping_divide_nothing()
    {
        Assert.Equal(0m, StrengthScore.Of(1.5m, samples: 0, damping: 0));

        // No sample with a damping is nothing of the measure.
        Assert.Equal(0m, StrengthScore.Of(1.5m, samples: 0, Damping));
    }

    [Fact]
    public void A_measure_at_the_end_of_the_range_of_a_number_has_a_score_and_fails_nothing()
    {
        // Such a measure cannot be multiplied by its sample. The sample over itself and the
        // damping is 1 at the most, so the score is in the range all the same: half of the measure.
        Assert.Equal(decimal.MaxValue / 2m, StrengthScore.Of(decimal.MaxValue, samples: 20, Damping));
        Assert.Equal(decimal.MinValue / 2m, StrengthScore.Of(decimal.MinValue, samples: 20, Damping));
    }

    [Fact]
    public void A_sample_size_or_a_damping_below_zero_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StrengthScore.Of(1m, samples: -1, Damping));
        Assert.Throws<ArgumentOutOfRangeException>(() => StrengthScore.Of(1m, samples: 1, damping: -1));
    }

    [Fact]
    public void A_slice_below_the_minimum_sample_has_no_score()
    {
        // One sample short: the slice says its measure and its sample, and no score, so nothing
        // can recommend from it.
        var short_ = Slice(new RoleMeasure(0.3m, MinSamples - 1));

        Assert.False(short_.Enough);
        Assert.Null(short_.Score);
        Assert.Equal((0.3m, 19), (short_.Measure.Value, short_.Measure.Samples));

        // With the minimum sample it has one.
        var enough = Slice(new RoleMeasure(0.3m, MinSamples));

        Assert.True(enough.Enough);
        Assert.Equal(0.15m, enough.Score);

        Assert.False(StrengthScore.IsEnough(19, MinSamples));
        Assert.True(StrengthScore.IsEnough(20, MinSamples));
    }

    [Fact]
    public void A_slice_whose_measure_cannot_be_said_has_no_score_whatever_its_sample()
    {
        // A Risk Manager that vetoed nothing has a sample, and no measure: there is nothing to score.
        var slice = Slice(new RoleMeasure(null, 60, new ReviewedPlans(Approved: 60, Vetoed: 0)));

        Assert.True(slice.Enough);
        Assert.Null(slice.Score);
    }

    private static RoleStrength Slice(RoleMeasure measure) =>
        RoleStrength.Of(SampleModels.Claude, Role.Strategist, Market.Spot, Timeframe.OneHour, measure, MinSamples, Damping);
}

/// <summary>Models of the catalog, for a test that needs a slice to name one and stores none.</summary>
internal static class SampleModels
{
    public static CatalogModel Claude { get; } = Of("claude", "Claude", "claude-test-model");

    public static CatalogModel Of(string provider, string providerName, string identifier) =>
        new(
            Guid.CreateVersion7(),
            new AiProviderCode(provider),
            providerName,
            identifier,
            DisplayName: null,
            ContextTokens: null,
            OutputTokens: null,
            ReleasedAt: null,
            DeprecatedAt: null,
            RetiresAt: null,
            AiModelStatus.Active,
            LastSeenAt: new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero),
            Selectable: true,
            LiveSearch: false);
}
