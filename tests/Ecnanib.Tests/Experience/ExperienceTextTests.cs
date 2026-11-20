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

    /// <summary>What an address says after its path, as a request brings it.</summary>
    private static QueryCollection QueryOf(string address) =>
        new(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri("https://ecnanib.example" + address).Query)
            .ToDictionary(choice => choice.Key, choice => choice.Value));
}
