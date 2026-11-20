using Ecnanib.Platform.BackgroundWork;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Settings;

/// <summary>
/// Runs the application in memory with a content root that holds a test's configuration file. The
/// content root is the platform's own way to say where the application lives, so this adds no
/// test-only path into the application.
/// </summary>
public sealed class EcnanibApplicationFactory : WebApplicationFactory<Program>
{
    // The job of the Exchange Gateway that reads Binance's clocks. It is not a public type, so
    // it is named as the log names it.
    private const string ClockWatch = "Ecnanib.ExchangeGateway.Adapters.Binance.BinanceClockJob";

    // The job of the Decision Engine that starts the cycles, named in the same way.
    private const string Cycles = "Ecnanib.DecisionEngine.DecisionCyclesJob";

    // The job of the Decision Engine that settles the orders with the exchange, named in the same way.
    private const string OrderSettling = "Ecnanib.DecisionEngine.OrderSettlingJob";

    /// <summary>
    /// The key under which the job that settles the orders is kept in an application whose
    /// scheduler does not run it: a test runs it from there itself, at a moment of its choosing.
    /// </summary>
    public const string OrderSettlingKey = "order-settling";

    // The job of the Decision Engine that watches the positions of the Futures market, named in the same way.
    private const string PositionWatch = "Ecnanib.DecisionEngine.PositionWatchJob";

    /// <summary>
    /// The key under which the job that watches the positions of the Futures market is kept: the
    /// scheduler of no application of the tests runs it, and a test runs it from there itself, at
    /// a moment of its choosing.
    /// </summary>
    public const string PositionWatchKey = "position-watch";

    // The job of the Decision Engine that records the results of the closed positions, named in the same way.
    private const string PositionResults = "Ecnanib.DecisionEngine.PositionResultsJob";

    /// <summary>
    /// The key under which the job that records the results of the closed positions is kept: the
    /// scheduler of no application of the tests runs it, and a test runs it from there itself, at
    /// a moment of its choosing.
    /// </summary>
    public const string PositionResultsKey = "position-results";

    // The job of the Decision Engine that reconciles the agents with the exchange, named in the same way.
    private const string Reconciliation = "Ecnanib.DecisionEngine.ReconciliationJob";

    // What says whether an agent was reconciled since the application took up the background work.
    private const string ReconciledSinceStart = "Ecnanib.DecisionEngine.ReconciledSinceStart";

    /// <summary>
    /// The key under which the job that reconciles the agents with the exchange is kept in an
    /// application whose scheduler does not run it: a test runs it from there itself, at a
    /// moment of its choosing.
    /// </summary>
    public const string ReconciliationKey = "reconciliation";

    // The job of the Decision Engine that follows the account event feed of the exchange, named in the same way.
    private const string AccountFeed = "Ecnanib.DecisionEngine.AccountFeedJob";

    /// <summary>
    /// The key under which the job that follows the account event feed is kept: the scheduler of
    /// no application of the tests runs it, and a test runs it from there itself, at a moment of
    /// its choosing.
    /// </summary>
    public const string AccountFeedKey = "account-feed";

    // The job of Experience that scores the calls of the analysts and follows the shadow trades, named in the same way.
    private const string ExperienceScoring = "Ecnanib.Experience.ExperienceScoringJob";

    /// <summary>
    /// The key under which the job that scores the calls of the analysts and follows the shadow
    /// trades is kept: the scheduler of no application of the tests runs it, and a test runs it
    /// from there itself, at a moment of its choosing.
    /// </summary>
    public const string ExperienceScoringKey = "experience-scoring";

    private SettingsDirectory? _ownDirectory;
    private readonly string _contentRoot;
    private readonly Action<IWebHostBuilder>? _configure;
    private readonly bool _watchesBinanceClock;
    private readonly bool _runsCycles;
    private readonly bool _settlesOrders;
    private readonly bool _reconciles;

    /// <summary>
    /// Where a test's client sends its requests. Over TLS, as a browser does: the application
    /// serves nothing over plain HTTP, and its cookies are sent only over TLS.
    /// </summary>
    public static readonly Uri Address = new("https://localhost");

    /// <summary>Runs the application with a valid configuration file, on the test server's shared database.</summary>
    public EcnanibApplicationFactory()
    {
        _ownDirectory = new SettingsDirectory(SettingsJson.Valid().OnTestServer());
        _contentRoot = _ownDirectory.Path;
        ClientOptions.BaseAddress = Address;
    }

    /// <summary>Runs the application in a content root the test prepared.</summary>
    /// <param name="contentRoot">The folder that holds the test's configuration file.</param>
    /// <param name="configure">What the test adds to the host, such as a service of its own.</param>
    /// <param name="watchesBinanceClock">
    /// Whether the application reads Binance's clocks on its schedule, as it does outside the
    /// tests. Only a test of that watch says so, and gives the application a Binance to read.
    /// </param>
    /// <param name="runsCycles">
    /// Whether the application starts the cycles of the agents that run when a candle closes, as
    /// it does outside the tests. Only a test of the cycles says so, and gives the application
    /// candles to start them with.
    /// </param>
    /// <param name="settlesOrders">
    /// Whether the application settles the orders of agents with the exchange on its schedule, as
    /// it does outside the tests. Only a test of that schedule says so, and gives the application
    /// a Binance to ask. Without it the job is still there, under <see cref="OrderSettlingKey"/>,
    /// for a test to run itself.
    /// </param>
    /// <param name="reconciles">
    /// Whether the application reconciles the agents with the exchange on its schedule, as it
    /// does outside the tests, and holds a cycle back for an agent that it has not reconciled
    /// since it started. Only a test of that says so, and gives the application a Binance to
    /// ask. Without it the job is still there, under <see cref="ReconciliationKey"/>, for a test
    /// to run itself.
    /// </param>
    internal EcnanibApplicationFactory(
        SettingsDirectory contentRoot,
        Action<IWebHostBuilder>? configure = null,
        bool watchesBinanceClock = false,
        bool runsCycles = false,
        bool settlesOrders = false,
        bool reconciles = false)
    {
        _contentRoot = contentRoot.Path;
        _configure = configure;
        _watchesBinanceClock = watchesBinanceClock;
        _runsCycles = runsCycles;
        _settlesOrders = settlesOrders;
        _reconciles = reconciles;
        ClientOptions.BaseAddress = Address;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(_contentRoot);

        builder.ConfigureServices(services =>
        {
            // No test reaches another system over the network: a client of the host's factory
            // gets no answer, as from a host that is down. A test that needs an answer puts a
            // handler of its own behind the client by its name, which comes after this.
            services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => new NoNetwork()));

            // The job that reads Binance's clocks is due as the background work starts, and calls
            // Binance without being asked. In a test of something else that is a call to a host
            // that is down, or one more call to a Binance of the test's, at a moment that the
            // test does not choose. So the application of a test is without that one job, unless
            // the test is about it. A job that was renamed is not found, and this says so.
            if (!_watchesBinanceClock)
            {
                services.Remove(services.Single(job =>
                    job.ServiceType == typeof(IBackgroundJob) && job.ImplementationType?.FullName == ClockWatch));
            }

            // The job that starts the cycles is due at every quarter hour, and asks the exchange
            // for candles when an agent runs then. Many tests have agents that run, and move a
            // clock over a quarter hour for a reason of their own: their application would ask a
            // market data that has no candles, at a moment that the test does not choose. So the
            // application of a test is without this job as well, unless the test is about it.
            if (!_runsCycles)
            {
                services.Remove(services.Single(job =>
                    job.ServiceType == typeof(IBackgroundJob) && job.ImplementationType?.FullName == Cycles));
            }

            // The job that settles the orders runs every minute, for the agents whose orders
            // or positions have something to settle, and asks the exchange about them. Many
            // tests give a user orders and positions for a reason of their own: their
            // application would ask a Binance that is not there about them, and write what
            // came of it, at a moment that the test does not choose. So the application of a
            // test is without this job as well, unless the test is about it.
            if (!_settlesOrders)
            {
                var settling = services.Single(job =>
                    job.ServiceType == typeof(IBackgroundJob) && job.ImplementationType?.FullName == OrderSettling);

                services.Remove(settling);

                // The scheduler does not know it any more. A test that is about what a run does
                // finds it by its key, and runs it at a moment that the test chooses, with no
                // run of the schedule beside it.
                services.AddKeyedSingleton(typeof(IBackgroundJob), OrderSettlingKey, settling.ImplementationType!);
            }

            // The job that watches the positions of the Futures market is due as the background
            // work starts when there is one, and then every few minutes: it asks the exchange
            // about the margin of every open position, and about its funding. Many tests give a
            // user a position for a reason of their own, and count the calls that their Binance
            // was sent. So no application of a test runs this job on a schedule: a test that is
            // about what a run does finds it by its key, and runs it itself.
            var watching = services.Single(job =>
                job.ServiceType == typeof(IBackgroundJob) && job.ImplementationType?.FullName == PositionWatch);

            services.Remove(watching);
            services.AddKeyedSingleton(typeof(IBackgroundJob), PositionWatchKey, watching.ImplementationType!);

            // The job that records the results of the closed positions is due as the background
            // work starts when a position is closed and has no result, and then every minute: it
            // writes the result, and has the loss limits of the agent evaluated, which reads a
            // price where the agent holds a position. Many tests give a user a closed position
            // for a reason of their own, and compare what the tables hold before and after. So
            // no application of a test runs this job on a schedule: a test that is about what a
            // run does finds it by its key, and runs it itself.
            var recording = services.Single(job =>
                job.ServiceType == typeof(IBackgroundJob) && job.ImplementationType?.FullName == PositionResults);

            services.Remove(recording);
            services.AddKeyedSingleton(typeof(IBackgroundJob), PositionResultsKey, recording.ImplementationType!);

            // The job that follows the account event feed runs every minute, for most of its
            // minute, and has the gateway open a connection for every account in which an agent
            // holds something. The file of the tests names no address of a feed, so it would
            // open none, but it would still be a run of a minute in every application. So no
            // application of a test runs it on a schedule: a test that is about what a run does
            // finds it by its key, and runs it itself.
            var following = services.Single(job =>
                job.ServiceType == typeof(IBackgroundJob) && job.ImplementationType?.FullName == AccountFeed);

            services.Remove(following);
            services.AddKeyedSingleton(typeof(IBackgroundJob), AccountFeedKey, following.ImplementationType!);

            // The job that scores the calls of the analysts and follows the shadow trades is due
            // as the background work starts, and then every few minutes: it asks the exchange
            // for the candles of every call and every shadow trade that is due, and writes what
            // became of each. Many tests record a cycle for a reason of their own, which leaves
            // such rows, over a market data that has no candles, and compare what the tables
            // hold before and after. So no application of a test runs this job on a schedule: a
            // test that is about what a run does finds it by its key, and runs it itself.
            var scoring = services.Single(job =>
                job.ServiceType == typeof(IBackgroundJob) && job.ImplementationType?.FullName == ExperienceScoring);

            services.Remove(scoring);
            services.AddKeyedSingleton(typeof(IBackgroundJob), ExperienceScoringKey, scoring.ImplementationType!);

            // The job that reconciles the agents with the exchange is due as the background
            // work starts when an agent holds anything, and then every few minutes: it asks
            // the exchange about every order that is not over, and about what the account of
            // each agent holds. And a cycle waits for an agent that holds something from before
            // the application started until that job has reconciled it. Many tests give a user
            // orders and positions for a reason of their own, and start an application over
            // them. So the application of a test is without the job on its schedule, and
            // without what holds a cycle back for it, unless the test is about them. The job
            // is kept under a key, for a test that runs it itself.
            if (!_reconciles)
            {
                var reconciling = services.Single(job =>
                    job.ServiceType == typeof(IBackgroundJob) && job.ImplementationType?.FullName == Reconciliation);

                services.Remove(reconciling);
                services.AddKeyedSingleton(typeof(IBackgroundJob), ReconciliationKey, reconciling.ImplementationType!);

                services.Remove(services.Single(gate => gate.ImplementationType?.FullName == ReconciledSinceStart));
            }
        });

        _configure?.Invoke(builder);
    }

    /// <summary>What every client of the host's factory ends in, unless a test says otherwise.</summary>
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException($"The tests have no network, and {request.RequestUri?.Host} was called."));
    }

    /// <summary>
    /// Starts the application, and says so when it refused to start. The application logs why
    /// and returns, as it does in production, and the factory takes a returning entry point for
    /// a started one. Without this a test would go on to use a host that is already disposed.
    /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        IHost? host = null;
        var started = false;

        try
        {
            host = base.CreateHost(builder);
            started = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested;
        }
        catch (ObjectDisposedException)
        {
            // The application disposed its host on the way out.
        }

        if (!started)
        {
            host?.Dispose();

            throw new InvalidOperationException(
                "The application did not start. " + (WhatItLogged() ?? "It logged nothing to a file under its content root."));
        }

        return host!;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            // Once: disposing the factory without awaiting it comes through here twice.
            Interlocked.Exchange(ref _ownDirectory, null)?.Dispose();
        }
    }

    /// <summary>The event the application wrote when it gave up, as its log file has it.</summary>
    private string? WhatItLogged()
    {
        var logs = Path.Combine(_contentRoot, "logs");
        if (!Directory.Exists(logs))
        {
            return null;
        }

        return Directory.GetFiles(logs)
            .SelectMany(ReadLines)
            .LastOrDefault(line => line.Contains("\"@l\":\"Fatal\"", StringComparison.Ordinal));

        static IEnumerable<string> ReadLines(string path)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);

            while (reader.ReadLine() is { } line)
            {
                yield return line;
            }
        }
    }
}
