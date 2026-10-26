using Ecnanib.Configuration;
using Microsoft.Extensions.Configuration;

namespace Ecnanib.Tests.Support;

/// <summary>
/// The committed <c>appsettings.json</c>, the documented reference file with every default (FR-CFG-01, FR-CFG-08),
/// copied beside the tests by the build and loaded alone: no overlay, no environment variables, no command line.
/// </summary>
internal static class ReferenceConfiguration
{
    public static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, ConfigurationFile.FileName);

    public static ConfigurationManager Load()
    {
        var configuration = new ConfigurationManager();
        configuration.AddJsonFile(Path, optional: false, reloadOnChange: false);
        return configuration;
    }
}
