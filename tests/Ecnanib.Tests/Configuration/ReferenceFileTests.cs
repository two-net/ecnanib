using System.Text.RegularExpressions;
using Ecnanib.Configuration;
using Ecnanib.Logging;
using Ecnanib.Tests.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Configuration;

/// <summary>
/// The committed <c>ecnanib.json</c> is the documented default file (FR-CFG-01, FR-CFG-08): every section it has is
/// known, every known section is in it, it holds no credential (FR-CFG-03) and every module binds and validates it.
/// </summary>
public class ReferenceFileTests
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, ConfigurationFile.DefaultFileName);

    [Fact]
    public void EveryTopLevelSectionIsKnownAndEveryKnownSectionIsPresent()
    {
        var configuration = Load();

        var present = ((IConfigurationRoot)configuration).Providers.OfType<JsonConfigurationProvider>().Single().GetChildKeys([], null).Distinct(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(ConfigurationFile.Sections.Order(StringComparer.OrdinalIgnoreCase), present.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void HoldsNoCredential()
    {
        var configuration = Load();
        // Names that end like a credential; MinPasswordLength and MaxOutputTokens are settings about credentials, not credentials.
        var keyLike = new Regex("(key|secret|password|passwd|pwd|credential|token|bearer)$", RegexOptions.IgnoreCase);

        Assert.Equal(string.Empty, configuration["Database:ConnectionString"]);
        foreach (var (key, value) in configuration.AsEnumerable().Where(pair => pair.Value is not null))
        {
            if (key == "Database:ConnectionString")
                continue;
            Assert.False(keyLike.IsMatch(key.Split(':')[^1]), $"{key} looks like a credential.");
            Assert.DoesNotMatch("(?i)(password|pwd)=", value);
        }
    }

    [Fact]
    public void EveryModuleBindsAndValidatesTheFile()
    {
        var configuration = Load();
        using (var provider = Register(configuration).BuildServiceProvider())
        {
            var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
            Assert.Contains("Database:ConnectionString is required", ex.Message);
        }

        // Test-only layering: the operator edits the file instead.
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = DatabaseTestHost.OfflineConnectionString });
        using (var provider = Register(configuration).BuildServiceProvider())
        {
            provider.GetRequiredService<IStartupValidator>().Validate();
        }
    }

    private static ConfigurationManager Load()
    {
        var configuration = new ConfigurationManager();
        ConfigurationFile.Load(configuration, FilePath);
        return configuration;
    }

    private static ServiceCollection Register(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogSinks(configuration);
        services.AddEcnanibModules(configuration);
        return services;
    }
}
