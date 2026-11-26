using Ecnanib.Platform.Logging;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging;

/// <summary>
/// The rates of errors, as an operator status screen shows them (FR-OPS-2): how many errors and
/// warnings the application logged in the last hour and in the last 24 hours, by the area that
/// logged them. They are counted in memory as they are logged, and nothing of them is written
/// to the database (NFR-DAT-7).
/// </summary>
/// <remarks>
/// A test logs under an area of its own, <c>Ecnanib.Planted</c>, so that what the application
/// logs meanwhile is not in its counts.
/// </remarks>
public class LoggedProblemsTests
{
    private const string Planted = "Planted";

    private static readonly DateTimeOffset Start = new(2026, 11, 11, 12, 7, 30, TimeSpan.Zero);

    [Fact]
    public void Errors_and_warnings_are_counted_by_the_area_that_logged_them()
    {
        using var application = Application(new TestClock(Start));
        var logger = application.Logger("Ecnanib.Planted.Somewhere.Deep");

        logger.LogError("Planted error {Number}", 1);
        logger.LogError(new InvalidOperationException("Planted."), "Planted error {Number}", 2);
        logger.LogCritical("Planted error {Number}", 3);
        logger.LogWarning("Planted warning {Number}", 1);
        logger.LogWarning("Planted warning {Number}", 2);

        // Neither an error nor a warning.
        logger.LogInformation("Planted information");

        // The area is the first namespace under that of the application, whatever is below it.
        application.Logger("Ecnanib.Planted").LogWarning("Planted warning {Number}", 3);

        // An error or above is an error, and a warning is a warning, in both spans.
        Assert.Equal(new AreaProblems(Planted, ErrorsLastHour: 3, WarningsLastHour: 3, ErrorsLastDay: 3, WarningsLastDay: 3), Of(application));
    }

    [Fact]
    public void A_count_is_of_the_last_hour_and_of_the_last_24_hours_to_the_minute()
    {
        var clock = new TestClock(Start);
        using var application = Application(clock);
        var logger = application.Logger("Ecnanib.Planted");

        logger.LogError("Planted error");
        logger.LogWarning("Planted warning");

        // 59 minutes on, the minute in which they were logged is one of the last 60.
        clock.Set(Start.AddMinutes(59));
        logger.LogError("Planted error, later");

        Assert.Equal(new AreaProblems(Planted, 2, 1, 2, 1), Of(application));

        // A minute later it is not, and it is still one of the last 24 hours.
        clock.Set(Start.AddMinutes(60));

        Assert.Equal(new AreaProblems(Planted, 1, 0, 2, 1), Of(application));

        // A day after the first, only the later one is left.
        clock.Set(Start.AddHours(24));

        Assert.Equal(new AreaProblems(Planted, 0, 0, 1, 0), Of(application));

        // And then nothing is, and an area that logged neither is not listed.
        clock.Set(Start.AddHours(24).AddMinutes(59));

        Assert.DoesNotContain(Read(application).Areas, area => area.Area == Planted);
    }

    [Fact]
    public void What_is_logged_outside_the_application_is_counted_apart_and_last()
    {
        using var application = Application(new TestClock(Start));

        var before = Read(application).Areas.SingleOrDefault(area => area.Area is null)?.ErrorsLastHour ?? 0;

        // A library, by the name that it logs under.
        application.Logger("Planted.Library.Client").LogError("Planted error");
        application.Logger("EcnanibLookalike.Planted").LogError("Planted error");
        application.Logger("Ecnanib.Planted").LogError("Planted error");

        var counts = Read(application);

        // What the host itself logs meanwhile is counted there too, so it is at least these two.
        Assert.Null(counts.Areas[^1].Area);
        Assert.True(counts.Areas[^1].ErrorsLastHour >= before + 2);

        // The areas of the application come first, by name.
        var named = counts.Areas.Where(area => area.Area is not null).Select(area => area.Area!).ToList();
        Assert.Equal(named.Order(StringComparer.Ordinal), named);
        Assert.Equal(1, Of(application).ErrorsLastHour);
        Assert.DoesNotContain(named, area => area is "Library" or "EcnanibLookalike");
    }

    [Fact]
    public void An_event_that_the_levels_keep_out_of_the_log_is_not_counted()
    {
        using var application = Application(new TestClock(Start), SettingsJson.Valid().With("Logging:MinimumLevel", "Error"));
        var logger = application.Logger("Ecnanib.Planted");

        logger.LogWarning("Planted warning");
        logger.LogError("Planted error");

        Assert.Equal(new AreaProblems(Planted, 1, 0, 1, 0), Of(application));
        Assert.DoesNotContain("Planted warning", application.LogText());
    }

    [Fact]
    public void The_counts_say_since_when_the_instance_counts()
    {
        var clock = new TestClock(Start);
        using var application = Application(clock);

        // It counts from when its log began, which is as the application starts.
        application.Logger("Ecnanib.Planted").LogWarning("Planted warning");
        clock.Set(Start.AddHours(5));

        Assert.Equal(Start, Read(application).CountedSince);
    }

    [Fact]
    public void A_clock_that_was_set_back_counts_nothing_twice_and_loses_nothing()
    {
        var clock = new TestClock(Start);
        using var application = Application(clock);
        var logger = application.Logger("Ecnanib.Planted");

        logger.LogError("Planted error");

        // An hour back: the minute of the error lies ahead, and is no minute of the last hour.
        clock.Set(Start.AddHours(-1));
        logger.LogError("Planted error, earlier");

        Assert.Equal(new AreaProblems(Planted, 1, 0, 1, 0), Of(application));

        // When its time has come again, it counts as it did.
        clock.Set(Start);

        Assert.Equal(new AreaProblems(Planted, 1, 0, 2, 0), Of(application));
    }

    private static LoggedApplication Application(TestClock clock, SettingsJson? settings = null) =>
        new(
            settings ?? LoggedApplication.Settings(),
            host => host.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)));

    private static LoggedProblemCounts Read(LoggedApplication application) =>
        application.Services.GetRequiredService<ILoggedProblems>().Read();

    private static AreaProblems Of(LoggedApplication application) =>
        Assert.Single(Read(application).Areas, area => area.Area == Planted);
}
