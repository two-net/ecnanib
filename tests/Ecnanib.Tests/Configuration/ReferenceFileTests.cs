using System.Text.RegularExpressions;
using Ecnanib.Configuration;
using Ecnanib.Logging;
using Ecnanib.Tests.Database;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Configuration;

/// <summary>
/// The committed <c>appsettings.json</c> is the documented reference file (FR-CFG-01, FR-CFG-08): every section it has
/// is known, every module's section is in it, it holds no credential (FR-CFG-03: the connection string and the stored-key encryption
/// keys ship empty) and every module binds and validates it.
/// </summary>
public class ReferenceFileTests
{
    [Fact]
    public void EveryTopLevelSectionIsKnownAndEveryModuleSectionIsPresent()
    {
        var configuration = ReferenceConfiguration.Load();

        var present = ((IConfigurationRoot)configuration).Providers.OfType<JsonConfigurationProvider>().Single().GetChildKeys([], null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Assert.All(ConfigurationFile.Sections, section => Assert.Contains(section, present, StringComparer.OrdinalIgnoreCase));
        Assert.All(present, key => Assert.Contains(key, ConfigurationFile.Sections.Concat(ConfigurationFile.FrameworkSections), StringComparer.OrdinalIgnoreCase));
        // The listen endpoint is documented in the file too, in the framework's own section.
        Assert.Equal("http://127.0.0.1:5207", configuration["Kestrel:Endpoints:Http:Url"]);
    }

    [Fact]
    public void HoldsNoCredential()
    {
        var configuration = ReferenceConfiguration.Load();
        // Names that end like a credential; MinPasswordLength and MaxOutputTokens are settings about credentials, not credentials.
        var keyLike = new Regex("(key|secret|password|passwd|pwd|credential|token|bearer)$", RegexOptions.IgnoreCase);

        // FR-CFG-03: the settings that are credentials ship empty: the connection string, the key that encrypts the users' stored keys and its predecessor for a rotation.
        string[] credentials = ["Database:ConnectionString", "Database:EncryptionKey", "Database:PreviousEncryptionKey"];
        Assert.All(credentials, setting => Assert.Equal(string.Empty, configuration[setting]));
        foreach (var (key, value) in configuration.AsEnumerable().Where(pair => pair.Value is not null))
        {
            if (credentials.Contains(key, StringComparer.OrdinalIgnoreCase))
                continue;
            Assert.False(keyLike.IsMatch(key.Split(':')[^1]), $"{key} looks like a credential.");
            Assert.DoesNotMatch("(?i)(password|pwd)=", value);
        }
    }

    [Fact]
    public void EveryModuleBindsAndValidatesTheFile()
    {
        var configuration = ReferenceConfiguration.Load();
        using (var provider = Register(configuration).BuildServiceProvider())
        {
            var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
            Assert.Contains("Database:ConnectionString is required", ex.Message);
        }

        // Test-only layering: the operator edits the file, an overlay, the environment or the command line instead.
        configuration.AddInMemoryCollection(DatabaseTestHost.OfflineConfiguration());
        using (var provider = Register(configuration).BuildServiceProvider())
        {
            provider.GetRequiredService<IStartupValidator>().Validate();
        }
    }

    private static ServiceCollection Register(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogSinks(configuration);
        services.AddEcnanibModules(configuration);
        return services;
    }
}
