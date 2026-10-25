using Ecnanib.Database;
using Ecnanib.Logging;
using Ecnanib.Logging.Abstractions;
using Ecnanib.Logging.Services;
using Ecnanib.Tests.Database;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Logging.DependencyInjection;

public class AddLogReaderTests
{
    [Fact]
    public void ResolvesTheReaderWithoutConnecting()
    {
        var factory = new ThrowingDbContextFactory();
        using var provider = DatabaseTestHost.BuildServices(configure: s =>
        {
            s.AddLogReader();
            s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory);
        });

        Assert.IsType<LogReader>(provider.GetRequiredService<ILogReader>());
        Assert.DoesNotContain(provider.GetServices<IHostedService>(), h => h.GetType().Namespace!.StartsWith("Ecnanib.Logging", StringComparison.Ordinal));
        Assert.Equal(0, factory.Calls);
    }
}
