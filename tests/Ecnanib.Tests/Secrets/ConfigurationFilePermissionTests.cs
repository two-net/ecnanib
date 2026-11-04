using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Secrets;

/// <summary>NFR-SEC-05: only the account the application runs as may read the configuration file.</summary>
public sealed class ConfigurationFilePermissionTests(PostgresFixture postgres) : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode Readable = OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ecnanib-settings-");

    [Theory]
    [InlineData(Readable)]
    [InlineData(OwnerOnly | UnixFileMode.GroupRead)]
    [InlineData(OwnerOnly | UnixFileMode.OtherWrite)]
    public async Task A_file_others_can_open_stops_startup_and_is_named(UnixFileMode mode)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows ACLs aren't file modes. The operator guide covers them (#57).");

        var file = SettingsFile(mode);
        await using var app = new AppFactory(postgres.NewConnectionString(), configure: AddFile(file), environment: Environments.Production);

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains($"{file} can be read or changed by users other than", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_only_its_owner_can_open_starts()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows ACLs aren't file modes. The operator guide covers them (#57).");

        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            configure: AddFile(SettingsFile(OwnerOnly)),
            environment: Environments.Production);

        app.Start();
    }

    /// <summary>A checkout is normally readable by everyone, and Development holds no real keys.</summary>
    [Fact]
    public async Task Development_doesnt_check()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows ACLs aren't file modes. The operator guide covers them (#57).");

        await using var app = new AppFactory(postgres.NewConnectionString(), configure: AddFile(SettingsFile(Readable)));

        app.Start();
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private static Action<IConfigurationBuilder> AddFile(string path) =>
        configuration => configuration.AddJsonFile(path, optional: false);

    private string SettingsFile(UnixFileMode mode)
    {
        var path = Path.Combine(_directory.FullName, $"appsettings-{Guid.NewGuid():N}.json");

        File.WriteAllText(path, "{}");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }

        return path;
    }
}
