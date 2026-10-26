using Ecnanib.Configuration;
using Ecnanib.Tests.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Configuration;

/// <summary>FR-CFG-05: the first start generates the Data Protection key; every later start finds it in storage.</summary>
public class SystemKeysTests
{
    [Fact]
    public void TheFirstStartGeneratesTheKeyAndLaterStartsReuseIt()
    {
        var repository = new InMemoryXmlRepository();
        var logs = new TestLoggerProvider();

        using (var host = Host(repository, logs))
        {
            host.EnsureSystemKeys();
            Assert.Single(repository.Elements);
            Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Generated the Data Protection key"));

            host.EnsureSystemKeys();
            Assert.Single(repository.Elements);
        }

        logs = new TestLoggerProvider();
        using (var host = Host(repository, logs))
        {
            host.EnsureSystemKeys();

            Assert.Single(repository.Elements);
            Assert.Contains(logs.Entries, e => e.Message.Contains("key ring loaded from the database (1 keys)"));
            Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("Generated"));
        }
    }

    private static FakeHost Host(IXmlRepository repository, TestLoggerProvider logs)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddDataProtection().SetApplicationName("Ecnanib");
        // The same mechanism PersistKeysToDbContext uses, against memory instead of the database.
        services.Configure<KeyManagementOptions>(o => o.XmlRepository = repository);
        return new FakeHost(services.BuildServiceProvider());
    }

    private sealed class FakeHost(ServiceProvider services) : IHost
    {
        public IServiceProvider Services => services;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose() => services.Dispose();
    }
}
