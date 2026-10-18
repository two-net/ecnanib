using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Database.Migrations;
using Ecnanib.Database.Retention;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Database.DependencyInjection;

public class AddDatabaseTests
{
    [Fact]
    public async Task ResolvesTheModuleWithoutConnecting()
    {
        await using var provider = DatabaseTestHost.BuildServices();

        var factory = provider.GetRequiredService<IDbContextFactory<EcnanibDbContext>>();
        await using var context = await factory.CreateDbContextAsync(DatabaseTestHost.Ct);
        Assert.NotNull(provider.GetRequiredService<DatabaseMigrator>());
        await using var scope = provider.CreateAsyncScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<EcnanibDbContext>());

        // Data Protection registers a hosted service of its own; the module adds exactly the sweeper.
        var hosted = Assert.Single(provider.GetServices<IHostedService>().OfType<RetentionSweeper>());
        Assert.Same(hosted, provider.GetRequiredService<RetentionSweeper>());
    }

    [Fact]
    public async Task PersistsTheDataProtectionKeyRingInTheDatabase()
    {
        await using var provider = DatabaseTestHost.BuildServices();

        var repository = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository;

        Assert.IsType<EntityFrameworkCoreXmlRepository<EcnanibDbContext>>(repository);
    }

    [Fact]
    public async Task UsesSnakeCaseNamesAndVersion7Keys()
    {
        await using var provider = DatabaseTestHost.BuildServices();
        await using var context = await provider.GetRequiredService<IDbContextFactory<EcnanibDbContext>>().CreateDbContextAsync(DatabaseTestHost.Ct);

        Assert.Equal("exchange_keys", context.Model.FindEntityType(typeof(ExchangeKey))!.GetTableName());
        Assert.Equal("normalized_email", context.Model.FindEntityType(typeof(User))!.FindProperty(nameof(User.NormalizedEmail))!.GetColumnName());

        var user = new User { Email = "a@example.com", NormalizedEmail = "A@EXAMPLE.COM", PasswordHash = "h", SecurityStamp = "s" };
        context.Add(user);

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal(7, user.Id.Version);
    }

    [Fact]
    public async Task MissingConnectionStringRefusesToCreateAContext()
    {
        await using var provider = DatabaseTestHost.BuildServices(connectionString: null);

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IDbContextFactory<EcnanibDbContext>>().CreateDbContext());

        Assert.Contains("Database:ConnectionString", ex.Message);
    }
}
