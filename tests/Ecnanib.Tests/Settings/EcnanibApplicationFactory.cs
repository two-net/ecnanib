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

    private SettingsDirectory? _ownDirectory;
    private readonly string _contentRoot;
    private readonly Action<IWebHostBuilder>? _configure;
    private readonly bool _watchesBinanceClock;
    private readonly bool _runsCycles;

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
    internal EcnanibApplicationFactory(
        SettingsDirectory contentRoot,
        Action<IWebHostBuilder>? configure = null,
        bool watchesBinanceClock = false,
        bool runsCycles = false)
    {
        _contentRoot = contentRoot.Path;
        _configure = configure;
        _watchesBinanceClock = watchesBinanceClock;
        _runsCycles = runsCycles;
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
