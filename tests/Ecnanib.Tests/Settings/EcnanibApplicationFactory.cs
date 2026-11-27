using System.Net;
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

    // The job of Agents that gives the roles of the agents that opted in other models by strength, named in the same way.
    private const string RoleReassignment = "Ecnanib.Agents.RoleReassignmentJob";

    /// <summary>
    /// The key under which the job that reassigns the roles of agents by strength is kept: the
    /// scheduler of no application of the tests runs it, and a test runs it from there itself,
    /// at a moment of its choosing.
    /// </summary>
    public const string RoleReassignmentKey = "role-reassignment";

    // The job of Agents that tells the users whose agents run where the exchange is out, named in the same way.
    private const string ExchangeOutageAlerts = "Ecnanib.Agents.ExchangeOutageAlertsJob";

    /// <summary>
    /// The key under which the job that tells users of an outage of the exchange is kept: the
    /// scheduler of no application of the tests runs it, and a test runs it from there itself,
    /// at a moment of its choosing.
    /// </summary>
    public const string ExchangeOutageAlertsKey = "exchange-outage-alerts";

    // The job of the Decision Engine that has the lessons of the closed positions written, named in the same way.
    private const string LessonWriting = "Ecnanib.DecisionEngine.LessonWritingJob";

    /// <summary>
    /// The key under which the job that has the lessons of the closed positions written is kept:
    /// the scheduler of no application of the tests runs it, and a test runs it from there
    /// itself, at a moment of its choosing.
    /// </summary>
    public const string LessonWritingKey = "lesson-writing";

    // The job of Notifications that sends the notifications about critical events by email, named in the same way.
    private const string NotificationEmail = "Ecnanib.Notifications.NotificationEmailJob";

    /// <summary>
    /// The key under which the job that sends the notifications about critical events by email
    /// is kept: the scheduler of no application of the tests runs it, and a test runs it from
    /// there itself, at a moment of its choosing.
    /// </summary>
    public const string NotificationEmailKey = "notification-email";

    // The job of the Decision Engine that deletes the records of the cycles that are kept no longer, named in the same way.
    private const string RecordRetention = "Ecnanib.DecisionEngine.RecordRetentionJob";

    /// <summary>
    /// The key under which the job that deletes the records of the cycles that are kept no
    /// longer is kept: the scheduler of no application of the tests runs it, and a test runs it
    /// from there itself, at a moment of its choosing.
    /// </summary>
    public const string RecordRetentionKey = "record-retention";

    // The job of the Decision Engine that thins the equity of the agents over time, named in the same way.
    private const string EquityThinning = "Ecnanib.DecisionEngine.EquityThinningJob";

    /// <summary>
    /// The key under which the job that thins the equity of the agents over time is kept: the
    /// scheduler of no application of the tests runs it, and a test runs it from there itself,
    /// at a moment of its choosing.
    /// </summary>
    public const string EquityThinningKey = "equity-thinning";

    // The job of Notifications that deletes the notifications that are kept no longer, named in the same way.
    private const string NotificationRetention = "Ecnanib.Notifications.NotificationRetentionJob";

    /// <summary>
    /// The key under which the job that deletes the notifications that are kept no longer is
    /// kept: the scheduler of no application of the tests runs it, and a test runs it from there
    /// itself, at a moment of its choosing.
    /// </summary>
    public const string NotificationRetentionKey = "notification-retention";

    // The job of Accounts that deletes the events of the audit trail that are kept no longer, named in the same way.
    private const string AuditRetention = "Ecnanib.Accounts.AuditRetentionJob";

    /// <summary>
    /// The key under which the job that deletes the events of the audit trail that are kept no
    /// longer is kept: the scheduler of no application of the tests runs it, and a test runs it
    /// from there itself, at a moment of its choosing.
    /// </summary>
    public const string AuditRetentionKey = "audit-retention";

    private SettingsDirectory? _ownDirectory;
    private readonly string _contentRoot;
    private readonly Action<IWebHostBuilder>? _configure;
    private readonly bool _watchesBinanceClock;
    // The job of the platform that backs the database up, named in the same way.
    private const string DatabaseBackup = "Ecnanib.Platform.Persistence.DatabaseBackupJob";

    /// <summary>
    /// The key under which the job that backs the database up is kept: the scheduler of no
    /// application of the tests runs it, and a test runs it from there itself, at a moment of its
    /// choosing.
    /// </summary>
    public const string DatabaseBackupKey = "database-backup";

    // The job of the platform that tests a restore, named in the same way.
    private const string RestoreTest = "Ecnanib.Platform.Persistence.RestoreTestJob";

    /// <summary>
    /// The key under which the job that tests a restore is kept: the scheduler of no application
    /// of the tests runs it, and a test runs it from there itself, at a moment of its choosing.
    /// </summary>
    public const string RestoreTestKey = "restore-test";

    // The job of the platform that says each minute that the service is up, named in the same way.
    private const string Uptime = "Ecnanib.Platform.Availability.UptimeJob";

    /// <summary>
    /// The key under which the job that says that the service is up is kept: the scheduler of no
    /// application of the tests runs it, and a test runs it from there itself, at a moment of its
    /// choosing.
    /// </summary>
    public const string UptimeKey = "uptime";

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
    /// <param name="listens">
    /// Whether the application listens on a port of this machine, over TLS, as it does outside
    /// the tests, in place of answering in memory. Only a test that drives a browser says so: a
    /// browser reaches nothing that is in memory. See <see cref="ListeningAt"/>.
    /// </param>
    internal EcnanibApplicationFactory(
        SettingsDirectory contentRoot,
        Action<IWebHostBuilder>? configure = null,
        bool watchesBinanceClock = false,
        bool runsCycles = false,
        bool settlesOrders = false,
        bool reconciles = false,
        bool listens = false)
    {
        _contentRoot = contentRoot.Path;
        _configure = configure;
        _watchesBinanceClock = watchesBinanceClock;
        _runsCycles = runsCycles;
        _settlesOrders = settlesOrders;
        _reconciles = reconciles;
        ClientOptions.BaseAddress = Address;

        if (listens)
        {
            // A port that is free, on this machine alone, and over TLS: the cookies of the
            // application are sent only over TLS, and a browser keeps them only from a host
            // that it reached so. The certificate is made for this run, and whoever asks is
            // told to take it: see LoopbackCertificate.
            Certificate = LoopbackCertificate.Create();
            UseKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(Certificate)));
        }
    }

    /// <summary>The certificate that an application which listens answers with, and null for one that answers in memory.</summary>
    internal System.Security.Cryptography.X509Certificates.X509Certificate2? Certificate { get; }

    /// <summary>
    /// Where an application that listens is reached, once it has started: <c>https://127.0.0.1</c>
    /// with the port that it was given. A client of the factory does not reach it, because it
    /// does not take the certificate: a browser that is told to does, and so does a client that
    /// a test makes for it.
    /// </summary>
    internal Uri ListeningAt
    {
        get
        {
            if (Certificate is null)
            {
                throw new InvalidOperationException("The application answers in memory, and listens nowhere. Say listens: true where it is made.");
            }

            StartServer();

            return ClientOptions.BaseAddress;
        }
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

            // The job that settles the orders runs first as the background work starts, then
            // every minute, and once more as the application stops, for the agents whose orders
            // or positions have something to settle, and asks the exchange about them. Many
            // tests give a user orders and positions for a reason of their own: their
            // application would ask a Binance that is not there about them, and write what
            // came of it, at a moment that the test does not choose, and take the entries off
            // that rest as it is disposed of. So the application of a test is without this job
            // as well, unless the test is about it.
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

            // The job that reassigns the roles of agents by strength is due as the background
            // work starts, and then every hour: it gives a role of an agent that opted in, runs,
            // and holds nothing its strongest candidate. A test that gives an agent such a
            // setting and scores for a reason of its own would find its team changed under it,
            // at a moment that it does not choose. So no application of a test runs this job on
            // a schedule: a test that is about what a run does finds it by its key, and runs it
            // itself.
            var reassigning = services.Single(job =>
                job.ServiceType == typeof(IBackgroundJob) && job.ImplementationType?.FullName == RoleReassignment);

            services.Remove(reassigning);
            services.AddKeyedSingleton(typeof(IBackgroundJob), RoleReassignmentKey, reassigning.ImplementationType!);

            // The job that tells the users whose agents run where the exchange is out is due
            // as the background work starts, and then every minute: it raises a notification
            // for each of them once an outage has lasted. Many tests have their Binance leave
            // calls without an answer for a reason of their own, and count the notifications of
            // a user. So no application of a test runs this job on a schedule: a test that is
            // about what a run does finds it by its key, and runs it itself.
            var alerting = services.Single(job =>
                job.ServiceType == typeof(IBackgroundJob) && job.ImplementationType?.FullName == ExchangeOutageAlerts);

            services.Remove(alerting);
            services.AddKeyedSingleton(typeof(IBackgroundJob), ExchangeOutageAlertsKey, alerting.ImplementationType!);

            // The job that has the lessons of the closed positions written is due as the
            // background work starts, and then every few minutes: it calls the model of the
            // Risk Manager of an agent for every lesson that is due, with the key of the agent's
            // user. Many tests give an agent a closed position for a reason of their own, which
            // leaves such a lesson, and stand nothing in for the models, or script what the
            // roles of a cycle answer and nothing else. So no application of a test runs this
            // job on a schedule: a test that is about what a run does finds it by its key, and
            // runs it itself.
            var writing = services.Single(job =>
                job.ServiceType == typeof(IBackgroundJob) && job.ImplementationType?.FullName == LessonWriting);

            services.Remove(writing);
            services.AddKeyedSingleton(typeof(IBackgroundJob), LessonWritingKey, writing.ImplementationType!);

            // The job that sends the notifications about critical events by email is due as the
            // background work starts, and then every minute: it hands the email delivery
            // service a message for every user who opted in and has such a notification. A
            // test that raises one for a reason of its own would have it sent, and marked as
            // sent, at a moment that it does not choose. So no application of a test runs this
            // job on a schedule: a test that is about what a run does finds it by its key, and
            // runs it itself.
            var emailing = services.Single(job =>
                job.ServiceType == typeof(IBackgroundJob) && job.ImplementationType?.FullName == NotificationEmail);

            services.Remove(emailing);
            services.AddKeyedSingleton(typeof(IBackgroundJob), NotificationEmailKey, emailing.ImplementationType!);

            // The four jobs that delete what is kept no longer are due as the background work
            // starts, and then every hour: each deletes the rows of its tables that are older
            // than the configuration file keeps them, by the clock of the application. Many
            // tests store rows of long ago for a reason of their own, or set their clock far
            // ahead, and compare what the tables hold before and after: their rows would be
            // gone at a moment that the test does not choose. So no application of a test runs
            // these jobs on a schedule: a test that is about what a run does finds the job by
            // its key, and runs it itself.
            foreach (var (retention, key) in new[]
            {
                (RecordRetention, RecordRetentionKey),
                (EquityThinning, EquityThinningKey),
                (NotificationRetention, NotificationRetentionKey),
                (AuditRetention, AuditRetentionKey),
            })
            {
                var deleting = services.Single(job =>
                    job.ServiceType == typeof(IBackgroundJob) && job.ImplementationType?.FullName == retention);

                services.Remove(deleting);
                services.AddKeyedSingleton(typeof(IBackgroundJob), key, deleting.ImplementationType!);
            }

            // The job that backs the database up and the job that tests a restore are due as
            // the background work starts, and then after hours and days: each starts a tool of
            // PostgreSQL, the second makes a database and drops it, and both write files. In a
            // test of something else that is work that nobody asked for, in every application.
            // So no application of a test runs these jobs on a schedule: a test that is about
            // what a run does finds the job by its key, and runs it itself.
            foreach (var (backup, key) in new[]
            {
                (DatabaseBackup, DatabaseBackupKey),
                (RestoreTest, RestoreTestKey),
            })
            {
                var backing = services.Single(job =>
                    job.ServiceType == typeof(IBackgroundJob) && job.ImplementationType?.FullName == backup);

                services.Remove(backing);
                services.AddKeyedSingleton(typeof(IBackgroundJob), key, backing.ImplementationType!);
            }

            // The job that says that the service is up writes a row as an application starts,
            // and lengthens it every minute. A test that holds everything stored against what it
            // was before a request would find that row changed, in a table that its request has
            // nothing to do with, and a test of the availability decides itself when the service
            // was up. So no application of a test runs the job on a schedule: a test that is
            // about a beat finds the job by its key, and runs it itself.
            var beating = services.Single(job =>
                job.ServiceType == typeof(IBackgroundJob) && job.ImplementationType?.FullName == Uptime);

            services.Remove(beating);
            services.AddKeyedSingleton(typeof(IBackgroundJob), UptimeKey, beating.ImplementationType!);

            // The job that reconciles the agents with the exchange runs first as the background
            // work starts, before any other job, and then every few minutes: it asks
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
            Certificate?.Dispose();
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
