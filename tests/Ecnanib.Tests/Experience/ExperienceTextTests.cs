using Ecnanib.Experience;
using Ecnanib.Shared;
using Ecnanib.WebUi.Pages.Experience;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// How the experience screens write a track record, and how they name its filter in an address
/// and read it from one (FR-EXP-2). See "The screens" in docs/experience.md.
/// </summary>
public class ExperienceTextTests
{
    [Theory]
    [InlineData("1.254", "+1.25 R")]
    [InlineData("1.255", "+1.26 R")]
    [InlineData("2", "+2 R")]
    [InlineData("0.5", "+0.5 R")]
    [InlineData("-0.5", "-0.5 R")]
    [InlineData("-1.119", "-1.12 R")]
    [InlineData("0", "0 R")]

    // What rounds to nothing is written as nothing, without a sign.
    [InlineData("0.004", "0 R")]
    [InlineData("-0.004", "0 R")]
    public void A_result_in_R_says_its_sign_and_two_decimal_places_at_the_most(string result, string written)
    {
        Assert.Equal(written, ExperiencePages.InR(decimal.Parse(result, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void A_measure_that_a_record_cannot_say_is_written_as_not_available()
    {
        Assert.Equal("Not available", ExperiencePages.InR(null));
        Assert.Equal("Not available", ExperiencePages.Held(null));

        var none = TrackRecord.Of([], minSamples: 1);

        Assert.Equal("Not available", ExperiencePages.WinRate(none));
        Assert.Equal("Not available", ExperiencePages.ProfitFactor(none));

        // A record with no losing trade has nothing to divide by, and says that.
        Assert.Equal("No losing trade", ExperiencePages.ProfitFactor(TrackRecord.Of([Trades.Closed(1, 1m)], minSamples: 1)));

        // Sums that are out of the range of a number are not said, and are not "no losing trade".
        var beyond = TrackRecord.Of([Trades.Closed(1, decimal.MaxValue), Trades.Closed(2, decimal.MaxValue)], minSamples: 1);

        Assert.Equal("Not available", ExperiencePages.ProfitFactor(beyond));
        Assert.Equal("Not available", ExperiencePages.Drawdown(beyond));
        Assert.Equal("Not available", ExperiencePages.InR(beyond.TotalR));
    }

    [Theory]
    [InlineData(0, "0 seconds")]
    [InlineData(1, "1 second")]
    [InlineData(59, "59 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(2700, "45 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(4200, "1 hour 10 minutes")]
    [InlineData(7260, "2 hours 1 minute")]
    [InlineData(86_400, "1 day")]
    [InlineData(90_000, "1 day 1 hour")]

    // The two largest units: minutes are not said beside days.
    [InlineData(88_200, "1 day")]
    [InlineData(266_400, "3 days 2 hours")]
    public void A_holding_time_is_written_in_its_two_largest_units(int seconds, string written)
    {
        Assert.Equal(written, ExperiencePages.Held(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void A_filter_is_named_in_an_address_as_it_is_read_from_one()
    {
        var model = Guid.CreateVersion7();

        // A filter that chooses nothing adds nothing to an address.
        Assert.Equal("/experience", ExperiencePages.Path(TrackRecordFilter.None));
        Assert.Equal($"/experience/models/{model}", ExperiencePages.ModelPath(model, TrackRecordFilter.None));
        Assert.Equal(TrackRecordFilter.None, ExperiencePages.FilterOf(new QueryCollection()));

        // Every filter that can be chosen is read back from the address that names it.
        foreach (var role in (Role?[])[null, .. Enum.GetValues<Role>()])
        {
            foreach (var market in (Market?[])[null, .. Enum.GetValues<Market>()])
            {
                foreach (var timeframe in (Timeframe?[])[null, .. Timeframes.All])
                {
                    foreach (var mode in (TradingMode?[])[null, .. Enum.GetValues<TradingMode>()])
                    {
                        foreach (var period in TrackPeriods.All)
                        {
                            var filter = new TrackRecordFilter(role, market, timeframe, mode, period);

                            Assert.Equal(filter, ExperiencePages.FilterOf(QueryOf(ExperiencePages.Path(filter))));

                            // The page of a model says every role, so its address names none.
                            Assert.Equal(
                                filter with { Role = null },
                                ExperiencePages.FilterOf(QueryOf(ExperiencePages.ModelPath(model, filter))));
                        }
                    }
                }
            }
        }

        Assert.Equal(
            "/experience?role=risk-manager&market=futures&timeframe=15m&mode=live&period=365",
            ExperiencePages.Path(new TrackRecordFilter(Role.RiskManager, Market.Futures, Timeframe.FifteenMinutes, TradingMode.Live, TrackPeriod.Last365Days)));
    }

    [Theory]
    [InlineData("0.25", "+25 percentage points")]
    [InlineData("0.125", "+12.5 percentage points")]
    [InlineData("-0.5", "-50 percentage points")]
    [InlineData("-0.0345", "-3.5 percentage points")]
    [InlineData("0.0344", "+3.4 percentage points")]
    [InlineData("0", "0 percentage points")]

    // What rounds to nothing is written as nothing, without a sign.
    [InlineData("0.0004", "0 percentage points")]
    [InlineData("-0.0004", "0 percentage points")]
    public void A_measure_of_an_analyst_is_written_in_percentage_points_with_its_sign_and_one_decimal_place_at_the_most(
        string fraction, string written)
    {
        Assert.Equal(written, ExperiencePages.InPoints(decimal.Parse(fraction, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void A_measure_and_a_score_are_written_in_the_unit_of_their_role()
    {
        RoleStrength Slice(Role role, RoleMeasure measure) =>
            RoleStrength.Of(SampleModels.Claude, role, Market.Spot, Timeframe.OneHour, measure, minSamples: 20, damping: 20);

        // An analyst is measured in a share above or below a half, and the two others in R.
        foreach (var analyst in new[] { Role.MarketAnalyst, Role.SentimentAnalyst })
        {
            var called = Slice(analyst, RoleMeasures.OfAnalyst(matched: 13, missed: 7));

            Assert.Equal(
                ("20", "+15 percentage points", "+7.5 percentage points"),
                (ExperiencePages.Samples(called), ExperiencePages.Measure(called), ExperiencePages.Score(called)));
        }

        var planned = Slice(Role.Strategist, new RoleMeasure(-0.42m, 60));

        Assert.Equal(("60", "-0.42 R", "-0.32 R"), (ExperiencePages.Samples(planned), ExperiencePages.Measure(planned), ExperiencePages.Score(planned)));

        // The sample of a Risk Manager says what it is made of.
        var reviewed = Slice(Role.RiskManager, new RoleMeasure(1.5m, 30, new ReviewedPlans(Approved: 21, Vetoed: 9)));

        Assert.Equal(
            ("30 (21 approved, 9 vetoed)", "+1.5 R", "+0.9 R"),
            (ExperiencePages.Samples(reviewed), ExperiencePages.Measure(reviewed), ExperiencePages.Score(reviewed)));

        // What cannot be said is "Not available", in either unit.
        var oneSided = Slice(Role.RiskManager, new RoleMeasure(null, 30, new ReviewedPlans(Approved: 30, Vetoed: 0)));

        Assert.Equal(("Not available", "Not available"), (ExperiencePages.Measure(oneSided), ExperiencePages.Score(oneSided)));
        Assert.Equal("Not available", ExperiencePages.InPoints(null));
    }

    [Fact]
    public void The_formula_is_written_with_the_damping_and_the_minimum_sample_of_the_configuration_file()
    {
        // The values of the specification, and others.
        Assert.Equal("Strength score = measure × n ÷ (n + 20), where n is the sample size.", ExperiencePages.StrengthFormula(damping: 20));
        Assert.Equal("Strength score = measure × n ÷ (n + 35), where n is the sample size.", ExperiencePages.StrengthFormula(damping: 35));
        Assert.Equal("A slice of fewer than 20 samples says \"Not enough data\", and has no score.", ExperiencePages.EnoughExplained(minSamples: 20));
        Assert.Equal("Not enough data", ExperiencePages.NotEnoughData);
    }

    [Fact]
    public void What_the_scores_leave_out_is_said_in_words_and_nothing_is_said_of_nothing()
    {
        RoleStrengthView View(int awaiting = 0, int withoutCandles = 0, int open = 0, int withoutResult = 0) =>
            new(TradingMode.Demo, MinSamples: 20, Damping: 20, [], awaiting, withoutCandles, open, withoutResult);

        var nothing = View();

        Assert.Null(ExperiencePages.CallsAwaiting(nothing));
        Assert.Null(ExperiencePages.CallsWithoutCandles(nothing));
        Assert.Null(ExperiencePages.ShadowTradesOpen(nothing));
        Assert.Null(ExperiencePages.ShadowTradesWithoutResult(nothing));

        // One is written as one, and more as how many.
        Assert.StartsWith("1 call of an analyst is not counted yet:", ExperiencePages.CallsAwaiting(View(awaiting: 1)));
        Assert.StartsWith("12 calls of analysts are not counted yet:", ExperiencePages.CallsAwaiting(View(awaiting: 12)));
        Assert.StartsWith("1 call of an analyst is not counted:", ExperiencePages.CallsWithoutCandles(View(withoutCandles: 1)));
        Assert.StartsWith("3 calls of analysts are not counted:", ExperiencePages.CallsWithoutCandles(View(withoutCandles: 3)));
        Assert.StartsWith("1 vetoed plan is not counted yet:", ExperiencePages.ShadowTradesOpen(View(open: 1)));
        Assert.StartsWith("4 vetoed plans are not counted yet:", ExperiencePages.ShadowTradesOpen(View(open: 4)));
        Assert.StartsWith("1 vetoed plan is not counted, because", ExperiencePages.ShadowTradesWithoutResult(View(withoutResult: 1)));
        Assert.StartsWith("5 vetoed plans are not counted, because", ExperiencePages.ShadowTradesWithoutResult(View(withoutResult: 5)));
    }

    /// <summary>What an address says after its path, as a request brings it.</summary>
    private static QueryCollection QueryOf(string address) =>
        new(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri("https://ecnanib.example" + address).Query)
            .ToDictionary(choice => choice.Key, choice => choice.Value));
}
