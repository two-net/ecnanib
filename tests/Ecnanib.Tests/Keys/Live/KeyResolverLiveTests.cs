using Ecnanib.Ai.Abstractions;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Keys.Live;

/// <summary>FR-KEY-05, FR-KEY-08 and NFR-SEC-03: only the owner's Valid keys resolve to handles.</summary>
[Trait("Category", "Live")]
public class KeyResolverLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private const string ApiKey = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd";
    private const string Secret = "S3CR3T-VALUE-THAT-MUST-NEVER-LEAK-9876";
    private const string ProviderKey = "sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789wxyz";
    private readonly DatabaseLiveFixture _fixture;

    public KeyResolverLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ResolvesOnlyTheOwnersValidKeysForAllowedMarkets()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var user = await KeysLiveSeed.NewUserAsync(_fixture, "resolve@example.com", Ct);
        var other = await KeysLiveSeed.NewUserAsync(_fixture, "resolve-other@example.com", Ct);
        var exchange = new FakeExchangeClientFactory();
        await using var provider = KeysTestHost.BuildServices(_fixture.ConnectionString, time: new FakeTimeProvider(LiveSeed.Now), exchange: exchange);
        var keys = provider.GetRequiredService<IKeyService>();
        var resolver = provider.GetRequiredService<IKeyResolver>();
        var keyId = (await keys.AddExchangeKeyAsync(user.Id, new ExchangeKeyInput("spot-only", ExchangeEnvironment.Testnet, true, false, ApiKey, Secret), Ct)).KeyId!.Value;

        var resolved = await resolver.ResolveExchangeKeyAsync(user.Id, keyId, MarketType.Spot, Ct);
        Assert.True(resolved.IsResolved);
        Assert.Equal(keyId.ToString("D"), resolved.Handle!.KeyId);
        Assert.Equal(ExchangeEnvironment.Testnet, resolved.Handle.Environment);
        Assert.Equal(ApiKey, resolved.Handle.Credentials.ApiKey);
        Assert.Equal("…abcd", resolved.Handle.Credentials.MaskedApiKey);
        Assert.DoesNotContain(Secret, resolved.ToString());

        var futures = await resolver.ResolveExchangeKeyAsync(user.Id, keyId, MarketType.UsdFutures, Ct);
        Assert.Equal(KeyResolutionStatus.MarketNotAllowed, futures.Status);
        Assert.Null(futures.Handle);
        // The reason is stored on agents and logged; the label, the user's own text, is never part of it (FR-LOG-05).
        Assert.Equal("The exchange key is not enabled for UsdFutures.", futures.Reason);

        Assert.Equal(KeyResolutionStatus.Missing, (await resolver.ResolveExchangeKeyAsync(other.Id, keyId, MarketType.Spot, Ct)).Status);
        Assert.Equal(KeyResolutionStatus.Missing, (await resolver.ResolveExchangeKeyAsync(user.Id, Guid.NewGuid(), MarketType.Spot, Ct)).Status);

        exchange.OnPing = (_, _, _) => throw new ExchangeAuthException("Binance spot account info failed: Invalid API-key (code -2015)", -2015);
        Assert.Equal(KeyValidationOutcome.Invalid, (await keys.ValidateExchangeKeyAsync(user.Id, keyId, Ct)).Outcome);
        var invalid = await resolver.ResolveExchangeKeyAsync(user.Id, keyId, MarketType.Spot, Ct);
        Assert.Equal(KeyResolutionStatus.Invalid, invalid.Status);
        Assert.Null(invalid.Handle);
        Assert.Contains("code -2015", invalid.Reason);

        Assert.Equal(KeyResolutionStatus.Missing, (await resolver.ResolveProviderKeyAsync(user.Id, AiProvider.Claude, Ct)).Status);
        var providerKeyId = (await keys.SetProviderKeyAsync(user.Id, AiProvider.Claude, ProviderKey, Ct)).KeyId!.Value;
        var providerResolved = await resolver.ResolveProviderKeyAsync(user.Id, AiProvider.Claude, Ct);
        Assert.True(providerResolved.IsResolved);
        Assert.Equal(providerKeyId.ToString("D"), providerResolved.Handle!.KeyId);
        Assert.Equal(AiProvider.Claude, providerResolved.Handle.Provider);
        Assert.Equal("…wxyz", providerResolved.Handle.Credentials.MaskedApiKey);
        Assert.Equal(KeyResolutionStatus.Missing, (await resolver.ResolveProviderKeyAsync(other.Id, AiProvider.Claude, Ct)).Status);
        Assert.Equal(KeyResolutionStatus.Missing, (await resolver.ResolveProviderKeyAsync(user.Id, AiProvider.Grok, Ct)).Status);

        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, user.Id, other.Id);
    }
}
