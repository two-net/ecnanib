using Ecnanib.WebUi.Pages;

namespace Ecnanib.Tests.Times;

/// <summary>
/// Times are shown in the user's local time zone, with UTC available (NFR-USA-3). These tests
/// hold how a page writes a time, with no application: in the time zone of the page, with how
/// far that is from UTC at the moment itself, and in UTC as before where the page is in UTC.
/// See docs/times.md.
/// </summary>
public class PageTimesTests
{
    // A moment in winter and one in summer, each with a second to it.
    private static readonly DateTimeOffset Winter = new(2026, 11, 15, 9, 20, 7, TimeSpan.Zero);
    private static readonly DateTimeOffset Summer = new(2026, 7, 15, 9, 20, 7, TimeSpan.Zero);

    [Fact]
    public void A_page_in_utc_writes_a_time_as_it_always_did()
    {
        var times = PageTimes.Utc;

        Assert.Equal("15 November 2026, 09:20 UTC", times.At(Winter));
        Assert.Equal("15 November 2026, 09:20:07 UTC", times.AtSecond(Winter));
        Assert.Equal("09:20 UTC", times.TimeOfDay(Winter));
        Assert.Equal("15 November 2026", times.Day(Winter));
        Assert.Equal("UTC", times.OffsetAt(Winter));
        Assert.Null(times.ZoneId);

        // Whatever the moment was written with: it is the moment that counts.
        Assert.Equal("15 November 2026, 09:20 UTC", times.At(Winter.ToOffset(TimeSpan.FromHours(-8))));
    }

    [Theory]
    [InlineData("Europe/Berlin", "15 November 2026, 10:20 UTC+1", "15 July 2026, 11:20 UTC+2")]
    [InlineData("America/New_York", "15 November 2026, 04:20 UTC-5", "15 July 2026, 05:20 UTC-4")]
    [InlineData("Asia/Kolkata", "15 November 2026, 14:50 UTC+5:30", "15 July 2026, 14:50 UTC+5:30")]
    [InlineData("Asia/Kathmandu", "15 November 2026, 15:05 UTC+5:45", "15 July 2026, 15:05 UTC+5:45")]
    [InlineData("Pacific/Marquesas", "14 November 2026, 23:50 UTC-9:30", "14 July 2026, 23:50 UTC-9:30")]
    [InlineData("Asia/Tokyo", "15 November 2026, 18:20 UTC+9", "15 July 2026, 18:20 UTC+9")]
    public void A_time_is_written_in_the_time_zone_of_the_page_with_how_far_that_is_from_utc(string zone, string winter, string summer)
    {
        var times = PageTimes.In(zone);

        // How far it is from UTC is that of the moment itself: summer time in summer.
        Assert.Equal(winter, times.At(Winter));
        Assert.Equal(summer, times.At(Summer));
        Assert.Equal(zone, times.ZoneId);
    }

    [Fact]
    public void Every_form_of_a_time_says_the_same_moment()
    {
        var times = PageTimes.In("Asia/Kolkata");

        Assert.Equal("15 November 2026, 14:50 UTC+5:30", times.At(Winter));
        Assert.Equal("15 November 2026, 14:50:07 UTC+5:30", times.AtSecond(Winter));
        Assert.Equal("14:50 UTC+5:30", times.TimeOfDay(Winter));
        Assert.Equal("15 November 2026", times.Day(Winter));
        Assert.Equal("UTC+5:30", times.OffsetAt(Winter));
    }

    [Fact]
    public void A_time_zone_that_is_at_utc_at_a_moment_says_utc_then()
    {
        var london = PageTimes.In("Europe/London");

        // In winter London is at UTC, and a time of it reads as one of a page in UTC does.
        Assert.Equal("15 November 2026, 09:20 UTC", london.At(Winter));
        Assert.Equal("15 July 2026, 10:20 UTC+1", london.At(Summer));
    }

    [Fact]
    public void The_day_of_a_moment_is_the_day_of_the_time_zone()
    {
        var lateEvening = new DateTimeOffset(2026, 11, 12, 22, 30, 0, TimeSpan.Zero);

        // Half past ten in the evening in UTC is the next day in Tokyo, and still the day in New York.
        Assert.Equal("12 November 2026", PageTimes.Utc.Day(lateEvening));
        Assert.Equal("13 November 2026", PageTimes.In("Asia/Tokyo").Day(lateEvening));
        Assert.Equal("12 November 2026", PageTimes.In("America/New_York").Day(lateEvening));

        var earlyMorning = new DateTimeOffset(2026, 11, 12, 1, 30, 0, TimeSpan.Zero);

        Assert.Equal("11 November 2026", PageTimes.In("America/New_York").Day(earlyMorning));
    }

    [Fact]
    public void A_day_that_is_a_utc_day_is_not_moved()
    {
        // The start of a UTC day, which is the evening before in Los Angeles: the day of the AI
        // usage and the day that a provider says of a model are written as they are.
        var day = new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal("23 November 2026", PageTimes.UtcDay(day));
        Assert.Equal("22 November 2026", PageTimes.In("America/Los_Angeles").Day(day));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("No/Such")]
    [InlineData("../etc/passwd")]
    [InlineData("/etc/localtime")]
    [InlineData("Europe/Berlin/../../../etc/passwd")]
    [InlineData("Europe/Berlin ")]
    [InlineData("<script>")]
    [InlineData("Europe/Berlin\r\nSet-Cookie: x=y")]
    [InlineData("AaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaAaaaaaaaaaaaa")]
    public void What_names_no_time_zone_is_utc(string? name)
    {
        var times = PageTimes.In(name);

        Assert.Same(PageTimes.Utc, times);
        Assert.Equal("15 November 2026, 09:20 UTC", times.At(Winter));
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("Etc/UTC")]
    public void A_name_of_utc_is_utc(string name)
    {
        var times = PageTimes.In(name);

        Assert.Equal("15 November 2026, 09:20 UTC", times.At(Winter));
    }

    [Theory]
    [InlineData("Asia/Calcutta", "Asia/Kolkata", "15 November 2026, 14:50 UTC+5:30")]
    [InlineData("Asia/Saigon", "Asia/Ho_Chi_Minh", "15 November 2026, 16:20 UTC+7")]
    [InlineData("Asia/Katmandu", "Asia/Kathmandu", "15 November 2026, 15:05 UTC+5:45")]
    [InlineData("Europe/Kiev", "Europe/Kyiv", "15 November 2026, 11:20 UTC+2")]
    [InlineData("America/Buenos_Aires", "America/Argentina/Buenos_Aires", "15 November 2026, 06:20 UTC-3")]
    [InlineData("America/Indianapolis", "America/Indiana/Indianapolis", "15 November 2026, 04:20 UTC-5")]
    public void An_older_name_that_a_browser_still_says_is_the_time_zone_of_its_newer_name(string older, string newer, string written)
    {
        // A browser goes by a list of its own, which has older names than the time zone
        // database: Chromium says Asia/Calcutta for a device in India. Whether the machine has
        // the older name or not, the time zone is the same one.
        Assert.Equal(written, PageTimes.In(older).At(Winter));
        Assert.Equal(written, PageTimes.In(newer).At(Winter));
    }

    [Fact]
    public void The_name_of_a_time_zone_is_read_again_as_the_same_time_zone()
    {
        // It travels with a page to the connection that the page makes, as text.
        var times = PageTimes.In("America/Argentina/Buenos_Aires");
        var again = PageTimes.In(times.ZoneId);

        Assert.Equal(times.At(Summer), again.At(Summer));
        Assert.Equal("15 July 2026, 06:20 UTC-3", again.At(Summer));
    }
}
