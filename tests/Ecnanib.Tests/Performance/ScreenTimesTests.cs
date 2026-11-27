using AngleSharp.Html.Parser;
using Ecnanib.Accounts;
using Ecnanib.Platform.Performance;
using Ecnanib.Platform.Persistence;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Settings;
using Ecnanib.WebUi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Performance;

/// <summary>
/// Typical screens respond within 300 ms at the 95th percentile, measured at the server
/// (NFR-PRF-1). These tests hold what measures it to its rules: a screen that was shown is
/// counted under its route with how long the server took, for the last hour and the last 24
/// hours, and nothing else is counted. That the screens of the application are within the target
/// is in <see cref="ScreenResponseTests"/>. See "Screen response times" in docs/performance.md.
/// </summary>
public class ScreenTimesTests
{
    private const string Ada = "ada@example.com";
    private const string Olive = "olive@example.com";

    private const string TimingArea = "Ecnanib.Platform.Performance.ScreenTiming";
    private const string Answered = "The screen {Route} was answered in {DurationMs} ms";

    // Half a minute into a minute, and between two quarter hours.
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 10, 7, 30, TimeSpan.Zero);

    [Theory]
    [InlineData(20, 19, true)]
    [InlineData(20, 18, false)]
    [InlineData(100, 95, true)]
    [InlineData(100, 94, false)]
    [InlineData(1, 1, true)]
    [InlineData(1, 0, false)]
    public void The_target_is_met_when_95_of_100_answers_or_more_were_within_it(int count, int within, bool met)
    {
        Assert.Equal(met, new AnswerTimes(count, within, ScreenTarget.Within, BeyondLongest: false).MeetsTarget);
    }

    [Fact]
    public void Where_nothing_was_shown_the_target_is_neither_met_nor_missed()
    {
        Assert.Null(new AnswerTimes(0, 0, null, BeyondLongest: false).MeetsTarget);
        Assert.Equal((95, TimeSpan.FromMilliseconds(300)), (ScreenTarget.Percent, ScreenTarget.Within));
    }

    [Fact]
    public async Task A_screen_that_was_shown_is_counted_under_its_route_with_how_long_the_server_took()
    {
        // The operator status screen reads the sizes of the tables, which here take as long as
        // the test says.
        var sizes = new SlowSizes();
        using var application = Application(new TestClock(Start), services => services.AddSingleton<IDatabaseSizes>(sizes));
        using var client = await OperatorAsync(application);

        // The first answer of a page is slower than any after it: its queries are new to the
        // application. It is counted as every other.
        await client.PageAsync(WebUiModule.OperatorStatusPath);
        var first = Of(application, WebUiModule.OperatorStatusPath).LastHour;

        await client.PageAsync(WebUiModule.OperatorStatusPath);
        var quick = Of(application, WebUiModule.OperatorStatusPath).LastHour;

        // One answer more, within the target.
        Assert.Equal((first.Count + 1, first.WithinTarget + 1), (quick.Count, quick.WithinTarget));

        // The same screen, which now takes longer than the target allows.
        sizes.Takes = ScreenTarget.Within + TimeSpan.FromMilliseconds(50);
        await client.PageAsync(WebUiModule.OperatorStatusPath);
        var slow = Of(application, WebUiModule.OperatorStatusPath).LastHour;

        // It is counted, and not as within the target. Of so few answers one is more than 5 of
        // 100, so the target is missed, and the percentile is the time of the slow one, to the
        // end of its step.
        Assert.Equal((quick.Count + 1, quick.WithinTarget), (slow.Count, slow.WithinTarget));
        Assert.Equal(false, slow.MeetsTarget);
        Assert.True(slow.Percentile > ScreenTarget.Within, $"The percentile is {slow.Percentile}.");
        Assert.False(slow.BeyondLongest);

        // The same is said of the last 24 hours, and of all screens together.
        var read = application.ScreenTimes.Read();

        Assert.Equal(slow, Of(application, WebUiModule.OperatorStatusPath).LastDay);
        Assert.Null(read.All.Route);
        Assert.Equal(read.Screens.Sum(screen => screen.LastHour.Count), read.All.LastHour.Count);
        Assert.Equal(read.Screens.Sum(screen => screen.LastHour.WithinTarget), read.All.LastHour.WithinTarget);
        Assert.Equal(Start, read.MeasuredSince);
    }

    [Fact]
    public async Task The_times_are_of_the_last_hour_and_of_the_last_24_hours_to_the_minute()
    {
        var clock = new TestClock(Start);
        using var application = Application(clock);
        using var client = application.CreateClient();

        await client.PageAsync(WebUiModule.HomePath);

        // 59 minutes on, the minute in which it was shown is one of the last 60.
        clock.Set(Start.AddMinutes(59));
        await client.PageAsync(WebUiModule.HomePath);

        Assert.Equal((2, 2), CountsOf(application, WebUiModule.HomePath));

        // A minute later it is not, and it is still one of the last 24 hours.
        clock.Set(Start.AddMinutes(60));

        Assert.Equal((1, 2), CountsOf(application, WebUiModule.HomePath));

        // A day after the first, only the later one is left, and the hour has nothing: its
        // figures are not known, and are no zero.
        clock.Set(Start.AddHours(24));

        var day = Of(application, WebUiModule.HomePath);

        Assert.Equal((0, 1), (day.LastHour.Count, day.LastDay.Count));
        Assert.Equal((null, null), (day.LastHour.Percentile, day.LastHour.MeetsTarget));

        // And then nothing is, and a screen that was not shown is not listed.
        clock.Set(Start.AddHours(24).AddMinutes(59));

        var read = application.ScreenTimes.Read();

        Assert.Empty(read.Screens);
        Assert.Equal((0, null), (read.All.LastDay.Count, read.All.LastDay.MeetsTarget));
    }

    [Fact]
    public async Task Only_a_page_that_was_asked_for_and_shown_is_counted()
    {
        using var application = Application(new TestClock(Start));
        var ada = await application.RegisterVerifiedAsync(Ada);

        // A visitor asks for a page of signed-in users, and is sent on to sign in: the page that
        // was asked for was not shown, and the page that the browser was sent to was.
        using var visitor = application.CreateClient();
        var (address, landed) = await visitor.OpenAsync(WebUiModule.DashboardPath);

        Assert.StartsWith(SessionAuthentication.SignInPath, address, StringComparison.Ordinal);
        Assert.Equal(1, CountsOf(application, SessionAuthentication.SignInPath).LastDay);
        Assert.DoesNotContain(Routes(application), route => route == WebUiModule.DashboardPath);

        // What is no page is not counted: a file of the application, and the API.
        var sheet = new HtmlParser().ParseDocument(landed).QuerySelector("link[rel=stylesheet]")?.GetAttribute("href");
        Assert.NotNull(sheet);

        using (var file = await visitor.GetAsync(sheet, TestContext.Current.CancellationToken))
        using (var health = await visitor.GetAsync("/api/health", TestContext.Current.CancellationToken))
        {
            Assert.True(file.IsSuccessStatusCode && health.IsSuccessStatusCode);
        }

        // The operator signs in: the page of the form is shown once more, and what the form did
        // is not counted, which hashed a password.
        using var client = await OperatorAsync(application);

        Assert.Equal(2, CountsOf(application, SessionAuthentication.SignInPath).LastDay);

        // A screen is told by its route, with the name of what it is of and never the value:
        // the account that the operator opens is in no route.
        await client.PageAsync(WebUiModule.OperatorAccountPath(ada.Id));

        var routes = Routes(application);

        Assert.Contains(routes, route => route.StartsWith(WebUiModule.OperatorAccountsPath + "/{", StringComparison.Ordinal));
        Assert.DoesNotContain(routes, route => route.Contains(ada.Id.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.All(routes, route => Assert.False(
            route.StartsWith("/api", StringComparison.Ordinal) || route.Contains('.', StringComparison.Ordinal),
            $"{route} is no page."));
    }

    [Fact]
    public async Task The_time_of_a_screen_is_logged_where_the_levels_of_the_file_let_it()
    {
        // The tracked file logs from Information on, so it leaves these out.
        using var application = Application(new TestClock(Start), settings: file => file.With("Logging:MinimumLevel", "Debug"));
        using var client = application.CreateClient();

        await client.PageAsync(WebUiModule.HomePath);

        var written = Assert.Single(application.Events(), written => written.Area == TimingArea && written.Template == Answered);

        Assert.Equal(("Debug", WebUiModule.HomePath), (written.Level, written.Text("Route")));
        Assert.InRange(written["DurationMs"].GetDouble(), 0, 60_000);
    }

    private static AccountsApplication Application(
        TestClock clock, Action<IServiceCollection>? services = null, Func<SettingsJson, SettingsJson>? settings = null) =>
        new(
            clock: clock,
            settings: file => (settings?.Invoke(file) ?? file).WithJson("Application:Operators", $"[ \"{Olive}\" ]"),
            host: services is null ? null : host => host.ConfigureServices(services));

    /// <summary>A browser that is signed in as the operator of the configuration file.</summary>
    private static async Task<HttpClient> OperatorAsync(AccountsApplication application)
    {
        await application.RegisterVerifiedAsync(Olive);

        var client = application.CreateClient();
        await client.SignInAsync(Olive);

        return client;
    }

    /// <summary>The times of one screen, by its route, which must have been shown.</summary>
    private static ScreenTimes Of(AccountsApplication application, string route) =>
        Assert.Single(application.ScreenTimes.Read().Screens, screen => screen.Route == route);

    /// <summary>How often a screen was shown in the last hour and in the last 24 hours.</summary>
    private static (long LastHour, long LastDay) CountsOf(AccountsApplication application, string route)
    {
        var times = Of(application, route);

        return (times.LastHour.Count, times.LastDay.Count);
    }

    /// <summary>The routes of the screens that were shown.</summary>
    private static IReadOnlyList<string> Routes(AccountsApplication application) =>
        [.. application.ScreenTimes.Read().Screens.Select(screen => screen.Route!)];

    /// <summary>Stands for what reads the sizes of the tables: it takes as long as the test says, and has none.</summary>
    private sealed class SlowSizes : IDatabaseSizes
    {
        public TimeSpan Takes { get; set; }

        public async Task<IReadOnlyList<TableSize>> ReadAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Takes, cancellationToken);

            return [];
        }
    }
}
