using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Keys.Services;

/// <summary>Input rules and failure hygiene of the key service, without a database.</summary>
public class KeyServiceTests
{
    private const string ApiKey = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd";
    private const string Secret = "S3CR3T-VALUE-THAT-MUST-NEVER-LEAK-9876";
    private static readonly Guid User = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<ExchangeKeyInput, KeySaveStatus> BadInputs => new()
    {
        { new ExchangeKeyInput("", ExchangeEnvironment.Production, true, false, ApiKey, Secret), KeySaveStatus.InvalidInput },
        { new ExchangeKeyInput("   ", ExchangeEnvironment.Production, true, false, ApiKey, Secret), KeySaveStatus.InvalidInput },
        { new ExchangeKeyInput(new string('x', 101), ExchangeEnvironment.Production, true, false, ApiKey, Secret), KeySaveStatus.InvalidInput },
        { new ExchangeKeyInput("main", ExchangeEnvironment.Production, false, false, ApiKey, Secret), KeySaveStatus.NoMarketSelected },
        { new ExchangeKeyInput("main", ExchangeEnvironment.Production, true, false, " ", Secret), KeySaveStatus.InvalidInput },
        { new ExchangeKeyInput("main", ExchangeEnvironment.Production, true, false, ApiKey, null), KeySaveStatus.InvalidInput },
    };

    [Theory]
    [MemberData(nameof(BadInputs))]
    public async Task BadExchangeInputIsRefusedBeforeTheDatabaseOrTheExchangeIsTouched(ExchangeKeyInput input, KeySaveStatus expected)
    {
        var factory = new ThrowingDbContextFactory();
        var exchange = new FakeExchangeClientFactory();
        await using var provider = Build(factory, exchange);
        var keys = provider.GetRequiredService<IKeyService>();

        var result = await keys.AddExchangeKeyAsync(User, input, Ct);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.KeyId);
        Assert.Equal(0, factory.Calls);
        Assert.Empty(exchange.Pings);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has a space")]
    [InlineData("non-ascii-ключ")]
    public async Task AMalformedProviderKeyIsRefusedWithoutAnyCall(string? apiKey)
    {
        var factory = new ThrowingDbContextFactory();
        var ai = new FakeAiClientFactory();
        await using var provider = Build(factory, ai: ai);
        var keys = provider.GetRequiredService<IKeyService>();

        var result = await keys.SetProviderKeyAsync(User, AiProvider.Claude, apiKey, Ct);

        Assert.Equal(KeySaveStatus.InvalidInput, result.Status);
        Assert.Equal(0, factory.Calls);
        Assert.Empty(ai.Listings);
        if (!string.IsNullOrEmpty(apiKey))
            Assert.DoesNotContain(apiKey, result.Detail);
    }

    [Fact]
    public async Task ADatabaseFailurePropagatesWithoutCredentialsInTheExceptionOrTheLogs()
    {
        var factory = new ThrowingDbContextFactory();
        var logs = new TestLoggerProvider();
        await using var provider = Build(factory, logs: logs);
        var keys = provider.GetRequiredService<IKeyService>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => keys.AddExchangeKeyAsync(User, new ExchangeKeyInput("main", ExchangeEnvironment.Testnet, true, true, ApiKey, Secret), Ct));

        Assert.Equal("database down", ex.Message);
        Assert.Equal(1, factory.Calls);
        Assert.DoesNotContain(ApiKey, logs.AllText);
        Assert.DoesNotContain(Secret, logs.AllText);
    }

    [Fact]
    public void ExchangeKeyInputPrintsOnlyTheMaskedKey()
    {
        var text = new ExchangeKeyInput("main", ExchangeEnvironment.Production, true, false, ApiKey, Secret).ToString();

        Assert.Contains("…abcd", text);
        Assert.DoesNotContain(ApiKey, text);
        Assert.DoesNotContain(Secret, text);
    }

    private static ServiceProvider Build(ThrowingDbContextFactory factory, FakeExchangeClientFactory? exchange = null, FakeAiClientFactory? ai = null, TestLoggerProvider? logs = null) =>
        KeysTestHost.BuildServices(logs: logs, exchange: exchange, ai: ai, configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));
}
