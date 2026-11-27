namespace Ecnanib.Tests.Performance;

/// <summary>
/// A single instance supports at least 500 running agents, with every cycle at a shared candle
/// boundary started within 30 seconds (NFR-PRF-3). The load test runs one instance with that
/// many agents, whose cycles are the application's own from the close of the candle to the
/// record of the cycle, and says whether every one of them started in time. With it, it says
/// how the screens answered meanwhile (NFR-PRF-1). See "The load test" in docs/performance.md.
/// </summary>
/// <remarks>
/// <para>
/// The run of 500 agents takes minutes, and wants the machine to itself: what it measures is
/// time. So it runs only where it is asked for, with <c>ECNANIB_LOAD</c>, as the tests that call
/// a provider or drive a browser do:
/// <c>ECNANIB_LOAD=1 dotnet test --solution Ecnanib.slnx --filter-class "*Performance.LoadTests"</c>.
/// </para>
/// <para>
/// A close of a few agents is run with every run of the tests, by the same steps and with the
/// same report: so what the load test is made of is known to work when it is asked for.
/// </para>
/// </remarks>
public class LoadTests
{
    [Fact]
    public async Task A_close_of_a_few_agents_is_measured_as_the_load_test_measures_it()
    {
        // Two users with four agents each, models that answer at once, and candles that Binance
        // gives a second after the close.
        var plan = new LoadPlan(
            Agents: 8,
            MaxPerProvider: null,
            ModelTakes: TimeSpan.FromMilliseconds(200),
            SpotCandleAfter: TimeSpan.FromSeconds(1),
            FuturesCandleAfter: TimeSpan.FromSeconds(1),
            Browsers: 1,
            ScreenEvery: TimeSpan.FromMilliseconds(100));

        using var application = new LoadApplication(plan);
        var result = await application.RunAsync();

        TestContext.Current.TestOutputHelper?.WriteLine(result.Report());

        // Every agent that runs had its cycle, of every market and every timeframe, and each
        // ran its team: three calls in a row, the first of them two at once.
        Assert.Equal((8, 8, 8), (result.Agents, result.Due, result.Started.Count));
        Assert.Equal(8, result.Started.Select(cycle => (cycle.Agent.Market, cycle.Agent.Symbol, cycle.Agent.Timeframe)).Distinct().Count());
        Assert.Equal(4, result.Started.Select(cycle => cycle.Agent.Timeframe).Distinct().Count());
        Assert.Equal(8 * 4, result.ModelCalls);

        // Each was recorded, and none was lost.
        Assert.Equal((8, 0, 0), (result.Summary.Ended, result.Summary.EndedLate, result.Lost));
        Assert.Equal(8, result.Recorded);

        // No cycle started before Binance gave its candle, a second after the close, and every
        // one started within the 30 seconds that it has.
        Assert.All(result.Started, cycle => Assert.InRange(cycle.After, plan.SpotCandleAfter, LoadPlan.StartsWithin));
        Assert.True(result.EveryCycleStartedInTime, result.Verdict);

        // The candles were asked for on both markets, and the screens were asked for meanwhile.
        Assert.Contains("/api/v3/klines", result.BinanceCalls.Keys);
        Assert.Contains("/fapi/v1/klines", result.BinanceCalls.Keys);
        Assert.True(result.Screens.All.LastHour.Count > 0);
        Assert.Equal(0, result.ScreensNotAnswered);

        // The report says it all, with the sentence that the run is for.
        Assert.Contains("Every cycle started within 30 seconds of the close: all 8 of them", result.Report());
    }

    [Fact]
    public async Task Five_hundred_running_agents_start_their_cycles_within_thirty_seconds_of_a_shared_close()
    {
        Assert.SkipUnless(
            LoadPlan.AskedFor,
            $"Set {LoadPlan.Variable} to run the load test, which takes minutes and wants the machine to itself. See docs/performance.md.");

        using var application = new LoadApplication(LoadPlan.OfTheEnvironment());
        var result = await application.RunAsync();

        // The report first: it is what the run is for, however the run is judged.
        var file = result.Write();

        TestContext.Current.TestOutputHelper?.WriteLine(result.Report());
        TestContext.Current.TestOutputHelper?.WriteLine($"Written to {file}");

        // Every agent that runs was due, and every cycle started within 30 seconds of the close
        // (NFR-PRF-3).
        Assert.True(result.EveryCycleStartedInTime, result.Verdict);

        // None was lost on the way: every cycle ran its team and was recorded.
        Assert.True(result.Lost == 0, $"{result.Lost} cycles failed, were stopped, or never ran. See {file}");
        Assert.Equal(result.Agents, result.Recorded);

        // And the screens answered within their target meanwhile (NFR-PRF-1).
        Assert.True(result.ScreensMetTheirTarget, $"The screens missed their target while the cycles ran. See {file}");
    }
}
