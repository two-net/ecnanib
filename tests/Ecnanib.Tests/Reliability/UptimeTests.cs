using Ecnanib.Platform.Availability;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Reliability;

/// <summary>
/// Availability is measured against a target of 99.5% per month, excluding announced maintenance
/// (NFR-REL-1). The service says every minute that it is up, in a row that its instance
/// lengthens, and the time that nothing said so for counts as down unless maintenance was
/// announced for it in advance. Each test here has the service say so at moments of its
/// choosing, and reads the rows and the figures that came of it. See "Availability" in
/// docs/reliability.md.
/// </summary>
public class UptimeTests
{
    private const string FirstTemplate = "The service says for the first time that it is up. Its availability is measured from {From}";
    private const string DownTemplate =
        "Nothing said that the service was up from {From} to {Until}. {DownSeconds} s of that count as down: no maintenance was announced in advance for them";
    private const string AnnouncedTemplate =
        "Nothing said that the service was up from {From} to {Until}, during maintenance that was announced in advance. It does not count as down";

    private static readonly DateTimeOffset Start = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly November = new(2026, 11, 1);
    private static readonly DateOnly October = new(2026, 10, 1);

    [Fact]
    public void The_job_says_every_minute_that_the_service_is_up()
    {
        using var application = new AvailabilityApplication(new TestClock(Start));

        Assert.Equal("uptime", application.Job.Name);
        Assert.Equal(TimeSpan.FromMinutes(1), application.Job.Schedule.Period);
        Assert.False(application.Job.Schedule.OnUtcBoundaries);
    }

    [Fact]
    public async Task The_first_beat_of_an_instance_writes_a_row_that_reaches_back_to_its_start()
    {
        using var application = new AvailabilityApplication(new TestClock(Start)).Start();

        Assert.Empty(StoredUpSpan.All(application.Database));

        // The jobs that run first held the beat back for two minutes: the instance was up meanwhile.
        await application.BeatAsync(Start.AddMinutes(2));

        Assert.Equal([new StoredUpSpan(Start, Start.AddMinutes(2))], StoredUpSpan.All(application.Database));

        var said = Assert.Single(application.Events());

        Assert.Equal((FirstTemplate, "Information"), (said.Template, said.Level));
    }

    [Fact]
    public async Task Each_beat_after_it_lengthens_that_row_and_writes_no_other()
    {
        using var application = new AvailabilityApplication(new TestClock(Start)).Start();

        foreach (var minute in new[] { 1, 2, 3, 4 })
        {
            await application.BeatAsync(Start.AddMinutes(minute));
        }

        // A beat that the scheduler started late, within what is tolerated, lengthens it as well.
        await application.BeatAsync(Start.AddMinutes(7));

        Assert.Equal([new StoredUpSpan(Start, Start.AddMinutes(7))], StoredUpSpan.All(application.Database));
        Assert.Single(application.Events());
    }

    [Fact]
    public async Task A_beat_that_comes_too_late_begins_a_new_row_and_the_time_between_counts_as_down()
    {
        using var application = new AvailabilityApplication(new TestClock(Start)).Start();

        await application.BeatAsync(Start.AddMinutes(1));

        // The database did not answer for a while, or nothing ran the work: the next beat is
        // ten minutes after the one before it, which is more than is tolerated.
        await application.BeatAsync(Start.AddMinutes(11));
        await application.BeatAsync(Start.AddMinutes(12));

        Assert.Equal(
            [new StoredUpSpan(Start, Start.AddMinutes(1)), new StoredUpSpan(Start.AddMinutes(11), Start.AddMinutes(12))],
            StoredUpSpan.All(application.Database));

        var said = Assert.Single(application.Events(), written => written.Template == DownTemplate);

        Assert.Equal(("Warning", 600), (said.Level, said["DownSeconds"].GetInt64()));

        var november = (await application.ReadAsync()).Months[0];

        Assert.Equal(November, november.Month);
        Assert.Equal((Start, Start.AddMinutes(12)), (november.MeasuredFrom, november.MeasuredUntil));
        Assert.Equal((TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(10)), (november.Measured, november.Down));
    }

    [Fact]
    public async Task An_instance_that_starts_after_another_has_stopped_begins_a_new_row_and_the_time_between_counts_as_down()
    {
        var database = PostgresServer.CreateDatabase();

        using (var first = new AvailabilityApplication(new TestClock(Start), database).Start())
        {
            await first.BeatAsync(Start.AddMinutes(1));
            await first.BeatAsync(Start.AddMinutes(2));
        }

        // The application was stopped, and starts again a quarter of an hour after its last beat.
        using var second = new AvailabilityApplication(new TestClock(Start.AddMinutes(17)), database).Start();

        await second.BeatAsync(Start.AddMinutes(18));

        Assert.Equal(
            [new StoredUpSpan(Start, Start.AddMinutes(2)), new StoredUpSpan(Start.AddMinutes(17), Start.AddMinutes(18))],
            StoredUpSpan.All(database));

        Assert.Equal(900, Assert.Single(second.Events(), written => written.Template == DownTemplate)["DownSeconds"].GetInt64());
        Assert.Equal(TimeSpan.FromMinutes(15), (await second.ReadAsync()).Months[0].Down);
    }

    [Fact]
    public async Task An_instance_that_takes_the_work_over_goes_on_where_the_other_left_off()
    {
        var database = PostgresServer.CreateDatabase();

        using var first = new AvailabilityApplication(new TestClock(Start), database).Start();

        // A second instance starts while the first runs the work, and stands by.
        using var second = new AvailabilityApplication(new TestClock(Start.AddMinutes(3)), database).Start();

        await first.BeatAsync(Start.AddMinutes(4));
        await first.BeatAsync(Start.AddMinutes(5));

        // The first is gone, and the second runs the work from then on. Its row begins where
        // the row of the first ends, and not at its own start, which that row covers.
        await second.BeatAsync(Start.AddMinutes(6));

        Assert.Equal(
            [new StoredUpSpan(Start, Start.AddMinutes(5)), new StoredUpSpan(Start.AddMinutes(5), Start.AddMinutes(6))],
            StoredUpSpan.All(database));
        Assert.DoesNotContain(second.Events(), written => written.Template == DownTemplate);
        Assert.Equal(TimeSpan.Zero, (await second.ReadAsync()).Months[0].Down);
    }

    [Fact]
    public async Task The_service_counts_as_up_until_now_while_its_last_beat_is_recent_and_no_longer()
    {
        var clock = new TestClock(Start);
        using var application = new AvailabilityApplication(clock).Start();

        await application.BeatAsync(Start.AddMinutes(1));

        // The next beat is still to come: nothing is down.
        clock.Set(Start.AddMinutes(3));

        var recent = (await application.ReadAsync()).Months[0];

        Assert.Equal((Start.AddMinutes(3), TimeSpan.FromMinutes(3), TimeSpan.Zero), (recent.MeasuredUntil, recent.Measured, recent.Down));
        Assert.Equal((100m, true), (recent.Percent, recent.MeetsTarget));

        // No beat for half an hour: the service is down since its last one, also before
        // anything has said that it is up again.
        clock.Set(Start.AddMinutes(31));

        var stale = (await application.ReadAsync()).Months[0];

        Assert.Equal((TimeSpan.FromMinutes(31), TimeSpan.FromMinutes(30)), (stale.Measured, stale.Down));
        Assert.False(stale.MeetsTarget);
    }

    [Fact]
    public async Task A_break_during_maintenance_that_was_announced_in_advance_does_not_count_as_down()
    {
        var database = Migrated();

        // Announced the day before, for 09:00 to 10:00.
        StoredMaintenance.Give(database, Start.AddHours(1), Start.AddHours(2), announcedAt: Start.AddDays(-1));

        using (var first = new AvailabilityApplication(new TestClock(Start), database).Start())
        {
            await first.BeatAsync(Start.AddHours(1).AddMinutes(1));
        }

        // Stopped a minute into the maintenance, and up again ten minutes before its end.
        using var second = new AvailabilityApplication(new TestClock(Start.AddMinutes(110)), database).Start();

        await second.BeatAsync(Start.AddMinutes(111));

        var said = Assert.Single(second.Events(), written => written.Template == AnnouncedTemplate);

        Assert.Equal("Information", said.Level);
        Assert.DoesNotContain(second.Events(), written => written.Template == DownTemplate);

        // Measured from 08:00 to 09:51, of which 09:00 to 09:51 is maintenance.
        var november = (await second.ReadAsync()).Months[0];

        Assert.Equal(TimeSpan.FromMinutes(51), november.Maintenance);
        Assert.Equal((TimeSpan.FromMinutes(60), TimeSpan.Zero, 100m), (november.Measured, november.Down, november.Percent));
    }

    [Fact]
    public async Task A_break_that_outlasts_the_maintenance_counts_as_down_for_the_time_after_it()
    {
        var database = Migrated();

        StoredMaintenance.Give(database, Start.AddHours(1), Start.AddHours(2), announcedAt: Start.AddDays(-1));

        using (var first = new AvailabilityApplication(new TestClock(Start), database).Start())
        {
            await first.BeatAsync(Start.AddHours(1));
        }

        // Up again twenty minutes after the end that was announced.
        using var second = new AvailabilityApplication(new TestClock(Start.AddMinutes(140)), database).Start();

        await second.BeatAsync(Start.AddMinutes(141));

        Assert.Equal(1200, Assert.Single(second.Events(), written => written.Template == DownTemplate)["DownSeconds"].GetInt64());

        var november = (await second.ReadAsync()).Months[0];

        Assert.Equal((TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(20)), (november.Maintenance, november.Down));
        Assert.Equal(TimeSpan.FromMinutes(81), november.Measured);
    }

    [Fact]
    public async Task Maintenance_that_was_announced_late_is_not_left_out()
    {
        var database = Migrated();

        // Announced half an hour before it starts, where the file asks for more: it is told
        // of, and what the service is down for during it counts.
        StoredMaintenance.Give(database, Start.AddHours(1), Start.AddHours(2), announcedAt: Start.AddMinutes(30), inAdvance: false);

        using (var first = new AvailabilityApplication(new TestClock(Start), database).Start())
        {
            await first.BeatAsync(Start.AddHours(1));
        }

        using var second = new AvailabilityApplication(new TestClock(Start.AddHours(2)), database).Start();

        await second.BeatAsync(Start.AddHours(2).AddMinutes(1));

        Assert.Equal(3600, Assert.Single(second.Events(), written => written.Template == DownTemplate)["DownSeconds"].GetInt64());

        var november = (await second.ReadAsync()).Months[0];

        Assert.Equal((TimeSpan.Zero, TimeSpan.FromHours(1)), (november.Maintenance, november.Down));
    }

    [Fact]
    public async Task The_month_that_runs_and_the_one_before_it_are_read_and_held_against_the_target_of_the_file()
    {
        var database = Migrated();
        var october = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        // Up from before October to its 10th, down for four hours, of which an hour and a half
        // were announced maintenance, and up from then into November. October has 744 hours.
        StoredUpSpan.Give(database, october.AddDays(-9), october.AddDays(9));
        StoredUpSpan.Give(database, october.AddDays(9).AddHours(4), Start);
        StoredMaintenance.Give(database, october.AddDays(9), october.AddDays(9).AddMinutes(90), announcedAt: october);

        using var application = new AvailabilityApplication(new TestClock(Start), database);

        var reading = await application.ReadAsync();

        // The file of the tests asks for 99.9%, which leaves October some 44 minutes.
        Assert.Equal(99.9m, reading.TargetPercent);
        Assert.Equal([November, October], reading.Months.Select(month => month.Month));

        var last = reading.Months[1];

        Assert.Equal((october, october.AddMonths(1)), (last.MeasuredFrom, last.MeasuredUntil));
        Assert.Equal(TimeSpan.FromMinutes(90), last.Maintenance);
        Assert.Equal(TimeSpan.FromMinutes((744 * 60) - 90), last.Measured);
        Assert.Equal(TimeSpan.FromMinutes(150), last.Down);
        Assert.False(last.MeetsTarget);

        // November was up from its beginning until the last beat, which is now.
        var current = reading.Months[0];

        Assert.Equal(Start, current.MeasuredUntil);
        Assert.Equal((TimeSpan.Zero, 100m, true), (current.Down, current.Percent, current.MeetsTarget));
    }

    [Fact]
    public async Task A_service_that_never_said_that_it_was_up_has_no_figure()
    {
        using var application = new AvailabilityApplication(new TestClock(Start));

        var reading = await application.ReadAsync();

        Assert.All(reading.Months, month =>
        {
            Assert.Null(month.Percent);
            Assert.Null(month.MeetsTarget);
            Assert.Null(month.MeasuredFrom);
        });
    }

    /// <summary>A database of the test's own that has its tables, for a test that writes rows before an application starts on it.</summary>
    private static TestDatabase Migrated()
    {
        var database = PostgresServer.CreateDatabase();
        database.Migrate();

        return database;
    }
}
