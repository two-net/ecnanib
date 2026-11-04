namespace Ecnanib.Platform.Secrets;

/// <summary>
/// NFR-SEC-05: the configuration file holds the master keys, so only the account the application
/// runs as may read it. Startup is refused while anyone else can. Development is exempt, because a
/// checkout is normally readable by everyone. Windows is too, because its ACLs don't map onto a file
/// mode. Both are covered by the operator guide (#57).
/// </summary>
internal static class ConfigurationFileAccess
{
    private const UnixFileMode Others =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <exception cref="InvalidOperationException">A settings file is open to other users. The message names it.</exception>
    public static void Verify(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment() || OperatingSystem.IsWindows() || configuration is not IConfigurationRoot root)
        {
            return;
        }

        foreach (var provider in root.Providers.OfType<FileConfigurationProvider>())
        {
            var path = provider.Source.FileProvider?.GetFileInfo(provider.Source.Path ?? string.Empty).PhysicalPath;

            if (path is not null && File.Exists(path) && (File.GetUnixFileMode(path) & Others) != 0)
            {
                throw new InvalidOperationException(
                    $"{path} can be read or changed by users other than the one the application runs as. "
                    + "It holds the master keys, so restrict it to that user, for example with chmod 600 (NFR-SEC-05).");
            }
        }
    }
}
