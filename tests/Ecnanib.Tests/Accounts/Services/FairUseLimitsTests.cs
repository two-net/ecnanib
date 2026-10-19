using Ecnanib.Accounts.Abstractions;
using Ecnanib.Accounts.Services;
using Ecnanib.Database;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Accounts.Services;

public class FairUseLimitsTests
{
    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(9, 10, true)]
    [InlineData(10, 10, false)]
    [InlineData(11, 10, false)]
    public void DecidesFromCountAndLimit(int current, int max, bool allowed)
    {
        var check = FairUseLimits.Decide("agents per user", current, max);

        Assert.Equal(new FairUseCheck("agents per user", allowed, current, max), check);
    }

    [Fact]
    public async Task ADatabaseFailurePropagatesRatherThanAllowing()
    {
        var factory = new ThrowingDbContextFactory();
        await using var provider = AccountsTestHost.BuildServices(configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));
        var limits = provider.GetRequiredService<IFairUseLimits>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => limits.CheckCreateAgentAsync(Guid.NewGuid(), AccountsTestHost.Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => limits.CheckStartAgentAsync(Guid.NewGuid(), AccountsTestHost.Ct));
        Assert.Equal(2, factory.Calls);
    }
}
