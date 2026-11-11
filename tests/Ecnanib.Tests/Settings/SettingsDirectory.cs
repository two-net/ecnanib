using Ecnanib.Platform.Settings;

namespace Ecnanib.Tests.Settings;

/// <summary>
/// A temporary content root for one test, holding the configuration file the test wants. No test
/// runs the application against the file in the project folder.
/// </summary>
internal sealed class SettingsDirectory : IDisposable
{
    public SettingsDirectory(string? settings = null)
    {
        if (settings is not null)
        {
            Write(SettingsFile.Name, settings);
        }
    }

    public SettingsDirectory(SettingsJson settings)
        : this(settings.ToString())
    {
    }

    public string Path { get; } = Directory.CreateTempSubdirectory("ecnanib-tests-").FullName;

    /// <summary>Where the application looks for its configuration file in this content root.</summary>
    public string SettingsPath => System.IO.Path.Combine(Path, SettingsFile.Name);

    /// <summary>Writes a file that only the current account can access, as the application requires.</summary>
    public void Write(string name, string content)
    {
        var path = System.IO.Path.Combine(Path, name);
        File.WriteAllText(path, content);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
