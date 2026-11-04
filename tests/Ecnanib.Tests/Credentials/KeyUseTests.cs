using Ecnanib.Kernel;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Credentials;

/// <summary>FR-KEY-03's last use, FR-KEY-05's market check, and FR-KEY-09's decryption at the moment of use.</summary>
public sealed class KeyUseTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => CredentialApp.Cancellation;

    [Fact]
    public async Task A_Binance_key_is_opened_for_a_market_it_is_enabled_for_and_refused_for_another()
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts(futures: false);
        var userId = Guid.NewGuid();
        var added = (await app.AddBinanceAsync(userId)).Credential!;

        var key = await app.Keys.OpenExchangeKeyAsync(userId, added.Id, Market.Spot, Cancellation);

        try
        {
            Assert.Equal((added.Id, TradingEnvironment.Live), (key.CredentialId, key.Environment));
            Assert.Equal(ExchangeHarness.ExampleApiKey, key.ApiKey.Reveal());
            Assert.Equal(ExchangeHarness.ExampleSecret, key.Secret.Reveal());
        }
        finally
        {
            key.ApiKey.Dispose();
            key.Secret.Dispose();
        }

        var refused = await Assert.ThrowsAsync<CredentialUnusableException>(
            () => app.Keys.OpenExchangeKeyAsync(userId, added.Id, Market.Futures, Cancellation));

        Assert.Equal(CredentialUnusableReason.MarketNotEnabled, refused.Reason);
        Assert.DoesNotContain(ExchangeHarness.ExampleApiKey, refused.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_AI_key_is_opened_for_its_provider_and_credential()
    {
        await using var app = new CredentialApp(postgres);
        app.AiAccepts();
        var userId = Guid.NewGuid();
        var added = (await app.AddAiAsync(userId)).Credential!;

        var key = await app.Keys.OpenAiKeyAsync(userId, added.Id, Cancellation);

        using (key.ApiKey)
        {
            Assert.Equal((CredentialApp.Provider, added.Id, CredentialApp.AiKey), (key.Provider, key.CredentialId, key.ApiKey.Reveal()));
        }
    }

    [Fact]
    public async Task A_key_that_is_not_valid_is_never_opened()
    {
        await using var app = new CredentialApp(postgres);
        var userId = Guid.NewGuid();
        var unverified = (await app.AddAiAsync(userId)).Credential!;

        var refused = await Assert.ThrowsAsync<CredentialUnusableException>(() => app.Keys.OpenAiKeyAsync(userId, unverified.Id, Cancellation));
        var refusedToList = await Assert.ThrowsAsync<CredentialUnusableException>(
            () => app.Keys.OpenAiKeyToListModelsAsync(userId, unverified.Id, Cancellation));

        Assert.Equal((CredentialStatus.Unverified, CredentialUnusableReason.NotValid), (unverified.Status, refused.Reason));
        Assert.Equal(CredentialUnusableReason.NotValid, refusedToList.Reason);
    }

    /// <summary>Listing a key's models for the catalog isn't a use, as validating isn't (FR-KEY-03, FR-CAT-02).</summary>
    [Fact]
    public async Task Opening_a_key_to_list_its_models_leaves_its_last_use_alone()
    {
        await using var app = new CredentialApp(postgres);
        app.AiAccepts();
        var userId = Guid.NewGuid();
        var added = (await app.AddAiAsync(userId)).Credential!;

        var key = await app.Keys.OpenAiKeyToListModelsAsync(userId, added.Id, Cancellation);

        using (key.ApiKey)
        {
            Assert.Equal((CredentialApp.Provider, added.Id, CredentialApp.AiKey), (key.Provider, key.CredentialId, key.ApiKey.Reveal()));
        }

        Assert.Null((await app.Credentials.FindAsync(userId, added.Id, Cancellation))!.LastUsedAt);

        (await app.Keys.OpenAiKeyAsync(userId, added.Id, Cancellation)).ApiKey.Dispose();

        Assert.NotNull((await app.Credentials.FindAsync(userId, added.Id, Cancellation))!.LastUsedAt);
    }

    /// <summary>The catalog syncs from every user's AI keys (FR-CAT-02), whatever their status, and never Binance's.</summary>
    [Fact]
    public async Task AI_keys_are_listed_with_their_status_for_one_user_or_for_everyone()
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts();
        var userId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var unverified = (await app.AddAiAsync(userId)).Credential!;
        app.AiAccepts();
        var valid = (await app.AddAiAsync(userId, CredentialApp.OtherAiKey, provider: "Second")).Credential!;
        var others = (await app.AddAiAsync(otherId)).Credential!;
        await app.AddBinanceAsync(userId);

        Assert.Equal(
            [
                new AiCredential(unverified.Id, userId, CredentialApp.Provider, CredentialStatus.Unverified),
                new AiCredential(valid.Id, userId, "Second", CredentialStatus.Valid),
            ],
            await app.Keys.ListAiAsync(userId, Cancellation));
        Assert.Equal([unverified.Id, valid.Id, others.Id], (await app.Keys.ListAllAiAsync(Cancellation)).Select(key => key.Id));
    }

    [Fact]
    public async Task Someone_else_s_key_or_the_wrong_kind_is_not_found()
    {
        await using var app = new CredentialApp(postgres);
        app.AiAccepts();
        app.BinanceAccepts();
        var userId = Guid.NewGuid();
        var ai = (await app.AddAiAsync(userId)).Credential!;
        var binance = (await app.AddBinanceAsync(userId)).Credential!;

        var cases = new Func<Task>[]
        {
            () => app.Keys.OpenAiKeyAsync(Guid.NewGuid(), ai.Id, Cancellation),
            () => app.Keys.OpenExchangeKeyAsync(Guid.NewGuid(), binance.Id, Market.Spot, Cancellation),
            () => app.Keys.OpenAiKeyAsync(userId, binance.Id, Cancellation),
            () => app.Keys.OpenExchangeKeyAsync(userId, ai.Id, Market.Spot, Cancellation),
        };

        foreach (var open in cases)
        {
            Assert.Equal(CredentialUnusableReason.NotFound, (await Assert.ThrowsAsync<CredentialUnusableException>(open)).Reason);
        }
    }

    /// <summary>A busy key would otherwise write on every call.</summary>
    [Fact]
    public async Task Each_use_marks_the_key_used_at_most_once_a_minute()
    {
        await using var app = new CredentialApp(postgres);
        app.AiAccepts();
        var userId = Guid.NewGuid();
        var added = (await app.AddAiAsync(userId)).Credential!;

        (await app.Keys.OpenAiKeyAsync(userId, added.Id, Cancellation)).ApiKey.Dispose();
        var first = (await app.Credentials.FindAsync(userId, added.Id, Cancellation))!.LastUsedAt;

        app.Clock.Advance(TimeSpan.FromSeconds(30));
        (await app.Keys.OpenAiKeyAsync(userId, added.Id, Cancellation)).ApiKey.Dispose();
        var soon = (await app.Credentials.FindAsync(userId, added.Id, Cancellation))!.LastUsedAt;

        app.Clock.Advance(TimeSpan.FromSeconds(31));
        (await app.Keys.OpenAiKeyAsync(userId, added.Id, Cancellation)).ApiKey.Dispose();
        var later = (await app.Credentials.FindAsync(userId, added.Id, Cancellation))!.LastUsedAt;

        Assert.NotNull(first);
        Assert.Equal(first, soon);
        Assert.True(later > first);
    }
}
