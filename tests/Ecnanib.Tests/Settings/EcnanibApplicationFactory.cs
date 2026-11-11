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
    private SettingsDirectory? _ownDirectory;
    private readonly string _contentRoot;
    private readonly Action<IWebHostBuilder>? _configure;

    /// <summary>Runs the application with a valid configuration file, on the test server's shared database.</summary>
    public EcnanibApplicationFactory()
    {
        _ownDirectory = new SettingsDirectory(SettingsJson.Valid().OnTestServer());
        _contentRoot = _ownDirectory.Path;
    }

    /// <summary>Runs the application in a content root the test prepared.</summary>
    internal EcnanibApplicationFactory(SettingsDirectory contentRoot, Action<IWebHostBuilder>? configure = null)
    {
        _contentRoot = contentRoot.Path;
        _configure = configure;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(_contentRoot);
        _configure?.Invoke(builder);
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
