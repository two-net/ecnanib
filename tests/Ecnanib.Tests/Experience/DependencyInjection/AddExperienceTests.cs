using Ecnanib.Agents.Abstractions;
using Ecnanib.Council.Abstractions;
using Ecnanib.Database;
using Ecnanib.Experience;
using Ecnanib.Experience.Abstractions;
using Ecnanib.Experience.Configuration;
using Ecnanib.Experience.Services;
using Ecnanib.Tests.Database;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Experience.DependencyInjection;

public class AddExperienceTests
{
    [Fact]
    public async Task RegisteredBeforeAddCouncilAndAddAgentsTheModuleReplacesTheirPlaceholdersWithoutConnecting()
    {
        var factory = new ThrowingDbContextFactory();
        var ai = new FakeAiClientFactory();
        await using var provider = ExperienceTestHost.BuildServices(ai: ai, configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));

        Assert.IsType<ExperienceSummaries>(provider.GetRequiredService<IExperienceSummaries>());
        Assert.IsType<ExperienceTracker>(provider.GetRequiredService<IExperienceTracker>());
        Assert.IsType<CouncilSuggestions>(provider.GetRequiredService<ICouncilSuggestions>());
        Assert.IsType<ExperienceReader>(provider.GetRequiredService<IExperienceReader>());
        var hosted = Assert.Single(provider.GetServices<IHostedService>(), h => h.GetType().Namespace!.StartsWith("Ecnanib.Experience", StringComparison.Ordinal));
        Assert.Same(provider.GetRequiredService<ExperienceRefresher>(), hosted);

        Assert.Equal(0, factory.Calls);
        Assert.Empty(ai.Listings);
        Assert.Empty(ai.Completions);
    }

    [Fact]
    public async Task BindsAndValidatesTheExperienceSection()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = DatabaseTestHost.OfflineConnectionString,
 ["Database:EncryptionKey"] = DatabaseTestHost.EncryptionKey,
            ["Experience:ShadowHorizonCandles"] = "12",
        }).Build();
        var services = new ServiceCollection();
        services.AddDatabase(configuration);
        services.AddExperience(configuration);
        await using var provider = services.BuildServiceProvider();

        Assert.Equal(12, provider.GetRequiredService<IOptions<ExperienceOptions>>().Value.ShadowHorizonCandles);
        Assert.Contains(provider.GetServices<IValidateOptions<ExperienceOptions>>(), v => v is ExperienceOptionsValidator);
    }
}
