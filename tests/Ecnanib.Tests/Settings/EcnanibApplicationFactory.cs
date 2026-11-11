using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ecnanib.Tests.Settings;

/// <summary>
/// Runs the application in memory with a content root that holds a test's configuration file. The
/// content root is the platform's own way to say where the application lives, so this adds no
/// test-only path into the application.
/// </summary>
public sealed class EcnanibApplicationFactory : WebApplicationFactory<Program>
{
    private readonly SettingsDirectory? _ownDirectory;
    private readonly string _contentRoot;
    private readonly Action<IWebHostBuilder>? _configure;

    /// <summary>Runs the application with a valid configuration file.</summary>
    public EcnanibApplicationFactory()
    {
        _ownDirectory = new SettingsDirectory(SettingsJson.Valid());
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

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _ownDirectory?.Dispose();
        }
    }
}
