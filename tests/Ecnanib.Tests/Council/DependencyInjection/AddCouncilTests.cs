using Ecnanib.Agents.Abstractions;
using Ecnanib.Council;
using Ecnanib.Council.Abstractions;
using Ecnanib.Council.Services;
using Ecnanib.Council.Services.Placeholders;
using Ecnanib.Database;
using Ecnanib.Tests.Database;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Council.DependencyInjection;

public class AddCouncilTests
{
    [Fact]
    public async Task RegisteredBeforeAddAgentsTheCouncilReplacesThePlaceholderWithoutConnecting()
    {
        var factory = new ThrowingDbContextFactory();
        var ai = new FakeAiClientFactory();
        await using var provider = CouncilTestHost.BuildServices(ai: ai, configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));

        Assert.IsType<Ecnanib.Council.Services.Council>(provider.GetRequiredService<ICouncil>());
        Assert.IsType<NoExperienceSummaries>(provider.GetRequiredService<IExperienceSummaries>());
        Assert.IsType<DeliberationReader>(provider.GetRequiredService<IDeliberationReader>());

        Assert.Equal(0, factory.Calls);
        Assert.Empty(ai.Listings);
        Assert.Empty(ai.Completions);
    }

    [Fact]
    public async Task AnExperienceImplementationRegisteredFirstIsKept()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(DatabaseTestHost.OfflineConfiguration()).Build();
        var experience = new FakeExperienceSummaries();
        var services = new ServiceCollection();
        services.AddDatabase(configuration);
        services.AddSingleton<IExperienceSummaries>(experience);
        services.AddCouncil(configuration);
        await using var provider = services.BuildServiceProvider();

        Assert.Same(experience, provider.GetRequiredService<IExperienceSummaries>());
    }
}
