using System.Net;
using AngleSharp.Html.Parser;
using Ecnanib.Accounts;
using Ecnanib.AiProviderGateway;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Performance;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Catalog;
using Ecnanib.Tests.DecisionEngine;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Reliability;
using Ecnanib.Tests.Settings;
using Ecnanib.WebUi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ecnanib.Tests.Operator;

/// <summary>
/// An operator status screen shows scheduler health, queue depth, last catalog sync per
/// provider, Binance rate-limit usage, clock drift, table sizes, and error rates (FR-OPS-2).
/// These tests read the screen as an operator opens it. Who it opens for is in
/// <c>LayoutPageTests</c>, that it shows nothing of any user in <c>GlobalHaltTests</c>, and what
/// each reading is worked out from in the tests of the component that keeps it. With them the
/// screen shows where the calls to the AI providers are paused and where Binance is out
/// (NFR-REL-4).
/// </summary>
public class OperatorStatusPageTests
{
    private const string Ada = "ada@example.com";
    private const string Olive = "olive@example.com";

    // Between two quarter hours, so that no close is due as an application starts.
    private static readonly DateTimeOffset Morning = new(2026, 11, 16, 10, 7, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Quarter = new(2026, 11, 16, 10, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_screen_has_a_part_for_each_reading_and_says_of_when_it_is()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock);
        using var client = await OperatorAsync(application);

        var page = await client.PageAsync(WebUiModule.OperatorStatusPath);

        // The seven readings of the requirement, in its order, each under a heading of its own.
        // With them, where the calls to the AI providers are paused, after the catalog of the
        // providers, and where Binance is out, after its limits and its clock (NFR-REL-4). And
        // before them all, how available the service was (NFR-REL-1), and how long its screens
        // took to answer (NFR-PRF-1): the two that are held against a target.
        Assert.Equal(
            [
                "Availability", "Response times of the screens", "Scheduler", "Queue of the cycles", "Catalog syncs",
                "Calls to the AI providers", "Request limits of Binance", "Clock", "Outages of Binance", "Tables",
                "Errors and warnings",
            ],
            Parsed(page).QuerySelectorAll("main h2").Select(heading => heading.TextContent.Trim()));

        // It follows nothing, so it says of when it is, in the time zone of whoever reads it,
        // and which of its readings are of the one instance that answered.
        Assert.Contains("This is how the service stood on 16 November 2026, 10:07:00 UTC.", page);
        Assert.Contains("are kept in memory by the instance that answered this page", page);
        Assert.DoesNotContain("<!--Blazor:{\"type\":\"server\"", page);

        // And nothing can be done on it: what an operator does is on a page that it leads to.
        Assert.Empty(Parsed(page).QuerySelectorAll("main form, main button, main input"));
        Assert.NotNull(Parsed(page).QuerySelector($"main a[href='{WebUiModule.GlobalHaltPath}']"));
    }

    [Fact]
    public async Task The_availability_says_each_month_with_its_figure_held_against_the_target()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock);
        using var client = await OperatorAsync(application);

        // The service was up from September on, but for four hours on the 10th of October, of
        // which the first hour and a half were maintenance that was announced in advance.
        var october = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        StoredUpSpan.Give(application.Database, october.AddDays(-6), october.AddDays(9));
        StoredUpSpan.Give(application.Database, october.AddDays(9).AddHours(4), Morning);
        StoredMaintenance.Give(application.Database, october.AddDays(9), october.AddDays(9).AddMinutes(90), announcedAt: october);

        var page = await client.PageAsync(WebUiModule.OperatorStatusPath);

        // The target is the one of the configuration file, which is 99.9% in the file of the tests.
        Assert.Contains(SaidIn(page, "status-availability"), said => said.Contains("held against a target of 99.9%.", StringComparison.Ordinal));
        Assert.Equal(
            ["Month", "Availability", "Target", "Down", "Maintenance left out", "Measured"],
            ColumnsOf(page, "status-availability"));

        // The month that runs, and the one before it. October was to be up for 742 and a half
        // hours, and was down for two and a half of them: 99.663%, cut and not rounded.
        Assert.Equal(
            [
                ["November 2026", "100%", "Met", "None", "None", "From 1 November 2026, 00:00 UTC to 16 November 2026, 10:07 UTC"],
                ["October 2026", "99.663%", "Missed", "2 h 30 min", "1 h 30 min", "From 1 October 2026, 00:00 UTC to 1 November 2026, 00:00 UTC"],
            ],
            RowsOf(page, "status-availability"));
    }

    [Fact]
    public async Task A_month_that_nothing_was_measured_of_is_said_as_not_known_and_as_no_figure()
    {
        using var application = Application(new TestClock(Morning));
        using var client = await OperatorAsync(application);

        // No application of a test says by itself that it is up, so nothing was measured.
        var page = await client.PageAsync(WebUiModule.OperatorStatusPath);

        Assert.Equal(
            [
                ["November 2026", "Not known", "Not known", "Not known", "Not known", "Nothing of it was measured"],
                ["October 2026", "Not known", "Not known", "Not known", "Not known", "Nothing of it was measured"],
            ],
            RowsOf(page, "status-availability"));
    }

    [Fact]
    public async Task The_response_times_say_each_screen_with_its_figures_held_against_the_target()
    {
        // What the instance measured, as the test says it: every screen together, three screens
        // by their routes, and the screens that are not told apart.
        var measured = new StandInScreenTimes
        {
            Said = new ScreenTimesReading(
                Morning.AddHours(-30),
                new ScreenTimes(null, Times(1250, 1230, 150), Times(20_480, 19_300, 400)),
                [
                    new ScreenTimes("/agents/{Id:guid}/state", Times(50, 50, 75), Times(900, 840, 500)),
                    new ScreenTimes("/dashboard", Times(1200, 1180, 150), Times(19_500, 18_525, 300)),
                    new ScreenTimes("/usage", Times(0, 0, null), new AnswerTimes(3, 0, null, BeyondLongest: true)),
                    new ScreenTimes(null, Times(0, 0, null), Times(77, 77, 5)),
                ]),
        };

        using var application = Application(new TestClock(Morning), services: services => services.AddSingleton<IScreenTimes>(measured));
        using var client = await OperatorAsync(application);

        var page = await client.PageAsync(WebUiModule.OperatorStatusPath);
        var said = string.Join(" ", SaidIn(page, "status-screens"));

        // What it is held to, where it is measured, what counts, and since when.
        Assert.Contains("held against a target: 95 of 100 answers within 300 milliseconds.", said);
        Assert.Contains("It is measured at the server", said);
        Assert.Contains("This instance measures since it started, on 15 November 2026, 04:07 UTC.", said);

        Assert.Equal(
            [
                "Screen", "Shown, last hour", "95 of 100 within, last hour", "Target, last hour", "Shown, last 24 hours",
                "95 of 100 within, last 24 hours", "Target, last 24 hours",
            ],
            ColumnsOf(page, "status-screens"));

        // All screens first, then each by its route. A span in which nothing was shown is not
        // known, and is neither met nor missed. The target is met by 95 of 100 exactly: 18,525
        // of 19,500.
        Assert.Equal(
            [
                ["All screens", "1,250", "150 milliseconds", "Met", "20,480", "400 milliseconds", "Missed"],
                ["/agents/{Id:guid}/state", "50", "75 milliseconds", "Met", "900", "500 milliseconds", "Missed"],
                ["/dashboard", "1,200", "150 milliseconds", "Met", "19,500", "300 milliseconds", "Met"],
                ["/usage", "0", "Not known", "Not known", "3", "More than a minute", "Missed"],
                ["Other screens", "0", "Not known", "Not known", "77", "5 milliseconds", "Met"],
            ],
            RowsOf(page, "status-screens"));

        static AnswerTimes Times(long count, long within, int? milliseconds) =>
            new(count, within, milliseconds is { } known ? TimeSpan.FromMilliseconds(known) : null, BeyondLongest: false);
    }

    [Fact]
    public async Task The_response_times_are_those_of_the_screens_that_the_instance_showed()
    {
        using var application = Application(new TestClock(Morning));
        using var client = await OperatorAsync(application);

        await client.PageAsync(WebUiModule.OperatorStatusPath);
        var page = await client.PageAsync(WebUiModule.OperatorStatusPath);

        // The screen itself, which was shown once before this answer, and the page that the
        // operator signed in on: each by its route, after all of them together.
        var rows = RowsOf(page, "status-screens");

        Assert.Equal("All screens", rows[0][0]);
        Assert.Equal("1", Assert.Single(rows, row => row[0] == WebUiModule.OperatorStatusPath)[1]);
        Assert.Contains(rows, row => row[0] == SessionAuthentication.SignInPath);
        Assert.All(rows, row => Assert.Matches(@"^(Met|Missed)$", row[3]));
    }

    [Fact]
    public async Task The_scheduler_says_each_job_with_its_schedule_and_its_latest_run()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock);
        using var client = await OperatorAsync(application);
        await Wait.UntilAsync(
            () => StoredJob.Of(application.Database, CatalogApplication.Job)?.Outcome == "Succeeded", "the first run of the job that syncs the catalog");

        var page = await client.PageAsync(WebUiModule.OperatorStatusPath);

        // The two jobs that every application of a test has. The catalog is synced as the
        // work starts, and the keys are checked on the hour, which has not come.
        Assert.Equal(
            [
                ["catalog-sync", "Every 12 hours", "16 November 2026, 10:07:00 UTC", "16 November 2026, 10:07:00 UTC", "Succeeded", "16 November 2026, 22:07:00 UTC"],
                ["key-validation", "Every hour, on the UTC clock", "No run yet", "None", "No run yet", "Due at once"],
            ],
            RowsOf(page, "status-scheduler"));
        Assert.Contains("No job is overdue.", SaidIn(page, "status-scheduler"));
    }

    [Fact]
    public async Task The_scheduler_says_a_job_whose_run_is_overdue_and_one_that_this_version_does_not_have()
    {
        var database = PostgresServer.CreateDatabase();
        database.Migrate();

        // Another instance holds the background work, as far as the database can tell, and runs
        // nothing: so the runs below are the latest that there are.
        using var other = database.Open();
        using (var take = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", other))
        {
            take.Parameters.AddWithValue("key", BackgroundJobs.LockKey);
            take.ExecuteNonQuery();
        }

        database.Execute(
            """
            INSERT INTO background_jobs (name, last_started_at, last_finished_at, last_outcome) VALUES
                ('catalog-sync', '2026-11-15 10:07:00+00', '2026-11-15 10:07:02+00', 'Failed'),
                ('key-validation', '2026-11-16 09:00:00+00', NULL, NULL),
                ('a-job-of-an-earlier-version', '2026-11-01 00:00:00+00', '2026-11-01 00:00:01+00', 'Stopped')
            """);

        using var application = Application(new TestClock(Morning), database);
        using var client = await OperatorAsync(application);

        var page = await client.PageAsync(WebUiModule.OperatorStatusPath);

        Assert.Equal(
            [
                // Due half a day after its start, which is long past.
                ["catalog-sync", "Every 12 hours", "15 November 2026, 10:07:00 UTC", "15 November 2026, 10:07:02 UTC",
                    "Failed. The error is in the log.", "Overdue since 15 November 2026, 22:07:00 UTC"],

                // A run with no end is still going, or was cut off: the screen says both.
                ["key-validation", "Every hour, on the UTC clock", "16 November 2026, 09:00:00 UTC", "No end recorded",
                    "Still going, or cut off", "Overdue since 16 November 2026, 10:00:00 UTC"],

                // Last, and with no schedule to be late for.
                ["a-job-of-an-earlier-version", "Not a job of this version", "1 November 2026, 00:00:00 UTC", "1 November 2026, 00:00:01 UTC",
                    "Stopped by the application", "Not known"],
            ],
            RowsOf(page, "status-scheduler"));

        Assert.Contains(
            "2 jobs are overdue: no instance runs the background work, a run outlasts its schedule, or the work has just started and still settles and reconciles first.",
            SaidIn(page, "status-scheduler"));
    }

    [Fact]
    public async Task The_queue_says_the_cycles_of_the_close_that_is_under_way()
    {
        using var cycles = new CyclesApplication(Quarter, maxPerProvider: 1, settings: Operators);
        using var client = await OperatorAsync(cycles.Application);

        // Between two closes there is no queue.
        var before = await client.PageAsync(WebUiModule.OperatorStatusPath);

        Assert.Contains("No close is under way: the cycles of the last close are over, or this instance stands by.", SaidIn(before, "status-queue"));
        Assert.Empty(RowsOf(before, "status-queue"));

        // Three cycles of one provider, of which one runs at a time, and the first does not end.
        cycles.Agents!.Give(Guid.NewGuid());
        cycles.Agents.Give(Guid.NewGuid());
        cycles.Agents.Give(Guid.NewGuid());
        cycles.Cycles.Holds = true;
        var run = cycles.RunAsync(Quarter);
        await cycles.Cycles.StartedAsync(1);
        await Wait.UntilAsync(() => cycles.Application.CycleLoad.Now() is { Queued: 2, Running: 1 }, "the two cycles that wait for the first");

        var during = await client.PageAsync(WebUiModule.OperatorStatusPath);

        Assert.Equal(
            ["Close", "Waiting for candles", "Waiting for a place", "Running"],
            ColumnsOf(during, "status-queue"));
        Assert.Equal([["16 November 2026, 10:15 UTC", "0", "2", "1"]], RowsOf(during, "status-queue"));

        // The queue names no user and no agent: it is numbers, and the time of the close.
        Assert.All(cycles.Cycles.Started, cycle => Assert.DoesNotContain(cycle.Agent.Id.ToString(), during));

        cycles.Cycles.ReleaseAll();
        await run.Within();

        Assert.Empty(RowsOf(await client.PageAsync(WebUiModule.OperatorStatusPath), "status-queue"));
    }

    [Fact]
    public async Task The_catalog_syncs_say_the_latest_sync_of_each_provider_and_the_latest_that_succeeded()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock, settings: file => Operators(file).WithCatalogKey());
        using var client = await OperatorAsync(application);
        await Wait.UntilAsync(
            () => StoredJob.Of(application.Database, CatalogApplication.Job)?.Outcome == "Succeeded", "the sync of the start");

        var synced = await client.PageAsync(WebUiModule.OperatorStatusPath);

        Assert.Equal(
            ["Provider", "Synced with", "Latest sync", "How it ended", "Models", "Latest sync that succeeded"],
            ColumnsOf(synced, "status-catalog"));

        // The file holds a key of the service for Claude alone: the others are synced when a
        // user's key is checked, and none was.
        Assert.Equal(
            [
                ["Claude", "The key of the service, on the schedule and on demand", "16 November 2026, 10:07 UTC, on the schedule", "Succeeded",
                    "2 added, 0 changed, 0 marked unavailable", "16 November 2026, 10:07 UTC"],
                ["Gemini", "The keys of users, as each is checked", "Not synced yet", "None", "None", "None yet"],
                ["GPT", "The keys of users, as each is checked", "Not synced yet", "None", "None", "None yet"],
                ["Grok", "The keys of users, as each is checked", "Not synced yet", "None", "None", "None yet"],
            ],
            RowsOf(synced, "status-catalog"));

        // A later sync fails. It is the latest, and the one before it is what the catalog holds.
        application.ModelLists.Answer = (_, _) => StandInModelLists.Ended(AiModelListOutcome.AuthenticationFailed);
        clock.Set(Morning.AddMinutes(20));
        await application.Catalog.SyncAsync(TestContext.Current.CancellationToken);

        var failed = await client.PageAsync(WebUiModule.OperatorStatusPath);

        Assert.Equal(
            ["Claude", "The key of the service, on the schedule and on demand", "16 November 2026, 10:27 UTC, on demand",
                "Failed: the provider refused the key", "0 added, 0 changed, 0 marked unavailable", "16 November 2026, 10:07 UTC"],
            RowsOf(failed, "status-catalog")[0]);

        // The key of the service is said to be there, and is not shown (FR-OPS-5).
        Assert.DoesNotContain(SettingsJson.CatalogKey, failed);
    }

    [Fact]
    public async Task The_request_limits_and_the_clock_say_how_the_service_stands_with_Binance()
    {
        using var application = Application(new TestClock(Morning));
        using var client = await OperatorAsync(application);

        var page = await client.PageAsync(WebUiModule.OperatorStatusPath);

        Assert.Equal(
            ["Limit", "Environment", "Used", "Allowed to the service", "Limit of Binance", "Counted by Binance", "Calls waiting", "Held back"],
            ColumnsOf(page, "status-limits"));

        // Each limit of the gateway, on the real exchange and then in the test environment, with
        // the 60 percent of it that the file of the tests allows the service. No test reaches
        // Binance, so nothing is used, and Binance has said nothing.
        Assert.Equal(
            [
                ["Spot", "Live", "0, which is 0 percent of what is allowed", "3,600", "6,000", "Not said yet", "0", "No"],
                ["Spot", "Demo", "0, which is 0 percent of what is allowed", "3,600", "6,000", "Not said yet", "0", "No"],
                ["Futures", "Live", "0, which is 0 percent of what is allowed", "1,440", "2,400", "Not said yet", "0", "No"],
                ["Futures", "Demo", "0, which is 0 percent of what is allowed", "1,440", "2,400", "Not said yet", "0", "No"],
                ["Key permissions", "Live", "0, which is 0 percent of what is allowed", "7,200", "12,000", "Not said yet", "0", "No"],
            ],
            RowsOf(page, "status-limits"));

        Assert.Equal(
            ["Market", "Environment", "State", "Drift", "Round trip", "Read", "Tolerance"],
            ColumnsOf(page, "status-clock"));

        // The application of a test does not read Binance's clocks, so none was read: the screen
        // says that, and what follows from it, and no drift of nothing.
        Assert.Equal(
            [
                ["Spot", "Live", "Not read: new entries are paused", "Not known", "Not known", "Not read yet", "400 milliseconds"],
                ["Spot", "Demo", "Not read: new entries are paused", "Not known", "Not known", "Not read yet", "400 milliseconds"],
                ["Futures", "Live", "Not read: new entries are paused", "Not known", "Not known", "Not read yet", "400 milliseconds"],
                ["Futures", "Demo", "Not read: new entries are paused", "Not known", "Not known", "Not read yet", "400 milliseconds"],
            ],
            RowsOf(page, "status-clock"));
    }

    [Fact]
    public async Task The_calls_to_the_AI_providers_and_the_outages_of_Binance_say_where_something_is_paused()
    {
        using var application = Application(new TestClock(Morning));
        using var client = await OperatorAsync(application);

        var page = await client.PageAsync(WebUiModule.OperatorStatusPath);

        Assert.Equal(
            ["Provider", "Calls", "Failures in a row", "Pauses in a row", "Keys that it refuses", "Of them paused now"],
            ColumnsOf(page, "status-ai-calls"));

        // Each provider of the file, by what it is called. Nothing has failed: the calls are made.
        Assert.Equal(
            [
                ["Claude", "Made", "0", "0", "0", "0"],
                ["Gemini", "Made", "0", "0", "0", "0"],
                ["GPT", "Made", "0", "0", "0", "0"],
                ["Grok", "Made", "0", "0", "0", "0"],
            ],
            RowsOf(page, "status-ai-calls"));

        Assert.Equal(
            ["Market", "Environment", "State", "Calls in a row without an answer", "Last answered"],
            ColumnsOf(page, "status-outages"));

        // Each market in each environment. No test reaches Binance, and none has called it here:
        // it has answered nothing, and has left nothing without an answer either.
        Assert.Equal(
            [
                ["Spot", "Live", "Answers", "0", "Not since the start"],
                ["Spot", "Demo", "Answers", "0", "Not since the start"],
                ["Futures", "Live", "Answers", "0", "Not since the start"],
                ["Futures", "Demo", "Answers", "0", "Not since the start"],
            ],
            RowsOf(page, "status-outages"));
    }

    [Fact]
    public async Task A_provider_whose_calls_are_paused_and_a_market_where_Binance_is_out_are_said_with_what_follows()
    {
        var pauses = new StandInPauses
        {
            Said =
            [
                new CallPause(new AiProviderCode("claude"), Failures: 6, Trips: 2, Morning.AddSeconds(80), KeysInTrouble: 0, KeysPaused: 0),
                new CallPause(new AiProviderCode("gemini"), Failures: 7, Trips: 1, PausedUntil: null, KeysInTrouble: 0, KeysPaused: 0),
                new CallPause(new AiProviderCode("gpt"), Failures: 0, Trips: 0, PausedUntil: null, KeysInTrouble: 3, KeysPaused: 2),
            ],
        };
        var outages = new StandInAnswers
        {
            Said =
            [
                new ExchangeAnswers(Market.Spot, TradingMode.Live, Unanswered: 0, OutageSince: null, AnsweredAt: Morning.AddSeconds(-5)),
                new ExchangeAnswers(Market.Futures, TradingMode.Live, Unanswered: 12, OutageSince: Morning.AddMinutes(-3), AnsweredAt: Morning.AddMinutes(-4)),
            ],
        };

        using var application = Application(
            new TestClock(Morning),
            services: services => services.AddSingleton<ICallPauses>(pauses).AddSingleton<IExchangeOutages>(outages));
        using var client = await OperatorAsync(application);

        var page = await client.PageAsync(WebUiModule.OperatorStatusPath);

        // A state is said in words, with what follows from it, and a key is a number alone.
        Assert.Equal(
            [
                ["Claude", "Paused until 16 November 2026, 10:08:20 UTC: the provider left calls without an answer", "6", "2", "0", "0"],
                ["Gemini", "Made again after a pause: the next failure pauses them again", "7", "1", "0", "0"],
                ["GPT", "Made", "0", "0", "3", "2"],
            ],
            RowsOf(page, "status-ai-calls"));

        Assert.Equal(
            [
                ["Spot", "Live", "Answers", "0", "16 November 2026, 10:06:55 UTC"],
                ["Futures", "Live", "Out since 16 November 2026, 10:04:00 UTC: new entries are paused", "12", "16 November 2026, 10:03:00 UTC"],
            ],
            RowsOf(page, "status-outages"));
    }

    [Fact]
    public async Task The_tables_say_what_each_takes_the_largest_first()
    {
        using var application = Application(new TestClock(Morning));
        using var client = await OperatorAsync(application);

        var page = await client.PageAsync(WebUiModule.OperatorStatusPath);
        var tables = RowsOf(page, "status-tables");

        Assert.Equal(["Table", "Size"], ColumnsOf(page, "status-tables"));

        // Every table of the database, each once, with a size in a unit.
        using var context = application.Database.Context();
        var expected = context.Model.GetEntityTypes().Select(entity => entity.GetTableName()!).Append("__EFMigrationsHistory").Distinct();

        Assert.Equal(expected.Order(StringComparer.Ordinal), tables.Select(table => table[0]).Order(StringComparer.Ordinal));
        Assert.All(tables, table => Assert.Matches(@"^[\d,]+(\.\d)? (bytes|kB|MB|GB)$", table[1]));

        // The largest first: a size in a larger unit never follows one in a smaller.
        string[] units = ["GB", "MB", "kB", "bytes"];
        var order = tables.Select(table => Array.IndexOf(units, table[1].Split(' ')[1])).ToList();
        Assert.Equal(order.Order(), order);

        Assert.Matches(
            $@"The {tables.Count} tables take [\d,]+(\.\d)? (kB|MB|GB) together\.", string.Join(" ", SaidIn(page, "status-tables")));
    }

    [Fact]
    public async Task The_errors_and_warnings_are_counted_by_the_area_that_logged_them()
    {
        using var application = Application(new TestClock(Morning));
        using var client = await OperatorAsync(application);

        var quiet = await client.PageAsync(WebUiModule.OperatorStatusPath);

        // It says since when it counts, and nothing of the application's own was counted yet.
        Assert.Contains("It counts since it started, on 16 November 2026, 10:07 UTC.", string.Join(" ", SaidIn(quiet, "status-problems")));
        Assert.DoesNotContain(RowsOf(quiet, "status-problems"), area => area[0] == "Accounts");

        // A user who is no operator asks for the screen twice, and is refused: Accounts logs a
        // warning each time (NFR-SEC-3).
        await application.RegisterVerifiedAsync(Ada);
        using var user = application.CreateClient();
        await user.SignInAsync(Ada);

        for (var asked = 0; asked < 2; asked++)
        {
            using var refused = await user.GetAsync(WebUiModule.OperatorStatusPath, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        var page = await client.PageAsync(WebUiModule.OperatorStatusPath);

        Assert.Equal(
            ["Area", "Errors, last hour", "Warnings, last hour", "Errors, last 24 hours", "Warnings, last 24 hours"],
            ColumnsOf(page, "status-problems"));
        Assert.Equal(["Accounts", "0", "2", "0", "2"], Assert.Single(RowsOf(page, "status-problems"), area => area[0] == "Accounts"));

        // A count holds nothing of what was logged: not who was refused, and not what.
        Assert.DoesNotContain("was refused", page);
    }

    private static AccountsApplication Application(
        TestClock clock,
        TestDatabase? database = null,
        Func<SettingsJson, SettingsJson>? settings = null,
        Action<IServiceCollection>? services = null) =>
        new(
            clock: clock,
            database: database,
            settings: settings ?? Operators,
            host: services is null ? null : host => host.ConfigureServices(services));

    /// <summary>Stands for what says where the calls to the models are paused: it says what the test says.</summary>
    private sealed class StandInPauses : ICallPauses
    {
        public IReadOnlyList<CallPause> Said { get; init; } = [];

        public IReadOnlyList<CallPause> Now() => Said;
    }

    /// <summary>Stands for the watch on whether the exchange answers: it says what the test says.</summary>
    private sealed class StandInAnswers : IExchangeOutages
    {
        public IReadOnlyList<ExchangeAnswers> Said { get; init; } = [];

        public IReadOnlyList<ExchangeAnswers> Answers() => Said;

        public bool EntriesPaused(Market market, TradingMode mode) =>
            Said.Any(answers => answers.Market == market && answers.Environment == mode && answers.EntriesPaused);
    }

    /// <summary>Stands for what measures the response times of the screens: it says what the test says.</summary>
    private sealed class StandInScreenTimes : IScreenTimes
    {
        public required ScreenTimesReading Said { get; init; }

        public ScreenTimesReading Read() => Said;
    }

    private static SettingsJson Operators(SettingsJson file) => file.WithJson("Application:Operators", $"[ \"{Olive}\" ]");

    /// <summary>A browser that is signed in as the operator of the configuration file.</summary>
    private static async Task<HttpClient> OperatorAsync(AccountsApplication application)
    {
        await application.RegisterVerifiedAsync(Olive);

        var client = application.CreateClient();
        await client.SignInAsync(Olive);

        return client;
    }

    private static AngleSharp.Html.Dom.IHtmlDocument Parsed(string page) => new HtmlParser().ParseDocument(page);

    /// <summary>The part of the screen under a heading, by the id of the heading.</summary>
    private static AngleSharp.Dom.IElement PartOf(string page, string headingId) =>
        Parsed(page).GetElementById(headingId)?.ParentElement ?? throw new Xunit.Sdk.XunitException($"The screen has no part {headingId}.");

    /// <summary>What the paragraphs of a part say.</summary>
    private static IReadOnlyList<string> SaidIn(string page, string headingId) =>
        [.. PartOf(page, headingId).QuerySelectorAll("p").Select(said => said.TextContent.Trim())];

    /// <summary>The headings of the columns of the table of a part.</summary>
    private static IReadOnlyList<string> ColumnsOf(string page, string headingId) =>
        [.. PartOf(page, headingId).QuerySelectorAll("thead th").Select(heading => heading.TextContent.Trim())];

    /// <summary>The rows of the table of a part, each as the texts of its cells. Empty when the part has no table.</summary>
    private static IReadOnlyList<IReadOnlyList<string>> RowsOf(string page, string headingId) =>
    [
        .. PartOf(page, headingId).QuerySelectorAll("tbody tr")
            .Select(row => (IReadOnlyList<string>)[.. row.Children.Select(cell => cell.TextContent.Trim())]),
    ];
}
