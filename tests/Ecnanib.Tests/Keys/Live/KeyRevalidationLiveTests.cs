using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Ecnanib.Keys.Services;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Keys.Live;

/// <summary>FR-KEY-07: the scheduled job against a real PostgreSQL database, with the exchange and providers faked.</summary>
[Trait("Category", "Live")]
public class KeyRevalidationLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private const string ApiKey = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd";
    private const string Secret = "S3CR3T-VALUE-THAT-MUST-NEVER-LEAK-9876";
    private const string ProviderKey = "sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789wxyz";
    private readonly DatabaseLiveFixture _fixture;

    public KeyRevalidationLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheJobChecksDueKeysFailsAgentsOnInvalidOnesAndLeavesTheCatalogAlone()
    {
        await StartAsync();
        var user = await KeysLiveSeed.NewUserAsync(_fixture, "job@example.com", Ct);
        var exchange = new FakeExchangeClientFactory();
        var ai = new FakeAiClientFactory();
        var (provider, logs, time) = Build(exchange, ai);
        await using var _ = provider;
        var keys = provider.GetRequiredService<IKeyService>();
        var job = provider.GetRequiredService<KeyRevalidator>();
        var keyId = (await keys.AddExchangeKeyAsync(user.Id, new ExchangeKeyInput("job", ExchangeEnvironment.Production, true, true, ApiKey, Secret), Ct)).KeyId!.Value;
        Assert.Equal(KeySaveStatus.Succeeded, (await keys.SetProviderKeyAsync(user.Id, AiProvider.Claude, ProviderKey, Ct)).Status);
        var live = await KeysLiveSeed.NewAgentAsync(_fixture, user.Id, "live", AgentMode.Live, AgentState.Running, keyId, (AiProvider.Gemini, true));
        var paper = await KeysLiveSeed.NewAgentAsync(_fixture, user.Id, "paper", AgentMode.Paper, AgentState.Paused, null, (AiProvider.Claude, true));
        var draft = await KeysLiveSeed.NewAgentAsync(_fixture, user.Id, "draft", AgentMode.Live, AgentState.Draft, keyId, (AiProvider.Claude, true));
        var pingsAfterSetup = exchange.Pings.Count;
        var listingsAfterSetup = ai.Listings.Count;

        // Nothing is due within the interval.
        Assert.Equal(new KeyRevalidationResult(0, 0, 0, 0, 0, 0), await job.RevalidateOnceAsync(Ct));
        Assert.Equal(pingsAfterSetup, exchange.Pings.Count);

        time.Advance(TimeSpan.FromMinutes(60));
        exchange.OnPing = (_, _, _) => throw new ExchangeAuthException("Binance spot account info failed: Invalid API-key (code -2015)", -2015);
        var first = await job.RevalidateOnceAsync(Ct);

        Assert.Equal(new KeyRevalidationResult(2, 1, 1, 0, 1, 0), first);
        Assert.Equal(listingsAfterSetup + 1, ai.Listings.Count);
        var liveAfter = await KeysLiveSeed.AgentAsync(_fixture, live.Id, Ct);
        Assert.Equal(AgentState.Error, liveAfter.State);
        Assert.StartsWith("Exchange key 'job' is invalid: ", liveAfter.StateReason);
        Assert.Equal(AgentState.Paused, (await KeysLiveSeed.AgentAsync(_fixture, paper.Id, Ct)).State);
        Assert.Equal(AgentState.Draft, (await KeysLiveSeed.AgentAsync(_fixture, draft.Id, Ct)).State);
        await using (var db = _fixture.CreateContext())
        {
            var key = await db.ExchangeKeys.SingleAsync(k => k.Id == keyId, Ct);
            Assert.Equal(KeyStatus.Invalid, key.Status);
            Assert.Contains("code -2015", key.LastValidationError);
            Assert.Equal(LiveSeed.Now.AddMinutes(60), key.LastValidatedAt);
            var providerKey = await db.ProviderKeys.SingleAsync(k => k.UserId == user.Id, Ct);
            Assert.Equal(KeyStatus.Valid, providerKey.Status);
            Assert.Equal(LiveSeed.Now.AddMinutes(60), providerKey.LastValidatedAt);
            // The job never syncs the catalog; the sync from the user's save is the last one.
            Assert.Equal(LiveSeed.Now, (await db.ProviderSyncStates.SingleAsync(s => s.Provider == AiProvider.Claude, Ct)).LastSyncAt);
        }

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith($"Exchange key {keyId} of user {user.Id} is invalid:", StringComparison.Ordinal) && e.Message.EndsWith("; 1 agent(s) entered Error", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message == "Key re-validation checked 2 key(s): 1 valid, 1 invalid, 0 not checked, 1 agent(s) entered Error, 0 failure(s)");

        // Recovery: the key is Valid again, the agent stays in Error until the user starts it.
        time.Advance(TimeSpan.FromMinutes(60));
        exchange.OnPing = null;
        ai.OnListModels = (_, _) => throw new AiAuthException("Claude list models failed: invalid x-api-key (authentication_error)", AiProvider.Claude, 401, "authentication_error");
        var second = await job.RevalidateOnceAsync(Ct);

        Assert.Equal(new KeyRevalidationResult(2, 1, 1, 0, 1, 0), second);
        Assert.Equal(AgentState.Error, (await KeysLiveSeed.AgentAsync(_fixture, live.Id, Ct)).State);
        var paperAfter = await KeysLiveSeed.AgentAsync(_fixture, paper.Id, Ct);
        Assert.Equal(AgentState.Error, paperAfter.State);
        Assert.StartsWith("Claude key is invalid: ", paperAfter.StateReason);
        Assert.Null(paperAfter.PauseCause);
        Assert.Equal(AgentState.Draft, (await KeysLiveSeed.AgentAsync(_fixture, draft.Id, Ct)).State);
        await using (var db = _fixture.CreateContext())
        {
            var key = await db.ExchangeKeys.SingleAsync(k => k.Id == keyId, Ct);
            Assert.Equal(KeyStatus.Valid, key.Status);
            Assert.Null(key.LastValidationError);
            Assert.Equal(KeyStatus.Invalid, (await db.ProviderKeys.SingleAsync(k => k.UserId == user.Id, Ct)).Status);
        }

        Assert.Contains(logs.Entries, e => e.Message == $"Exchange key {keyId} of user {user.Id} is valid again");
        Assert.DoesNotContain(ApiKey, logs.AllText);
        Assert.DoesNotContain(Secret, logs.AllText);
        Assert.DoesNotContain(ProviderKey, logs.AllText);
        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, user.Id);
    }

    [Fact]
    public async Task AFailedCheckLeavesTheRowAndTheAgentsUntouched()
    {
        await StartAsync();
        var user = await KeysLiveSeed.NewUserAsync(_fixture, "job-failed@example.com", Ct);
        var exchange = new FakeExchangeClientFactory();
        var (provider, logs, time) = Build(exchange);
        await using var _ = provider;
        var keys = provider.GetRequiredService<IKeyService>();
        var job = provider.GetRequiredService<KeyRevalidator>();
        var keyId = (await keys.AddExchangeKeyAsync(user.Id, new ExchangeKeyInput("flaky", ExchangeEnvironment.Production, true, false, ApiKey, Secret), Ct)).KeyId!.Value;
        var live = await KeysLiveSeed.NewAgentAsync(_fixture, user.Id, "live", AgentMode.Live, AgentState.Running, keyId);
        time.Advance(TimeSpan.FromMinutes(60));
        exchange.OnPing = (_, _, _) => throw new ExchangeUnavailableException("Binance spot account info failed: gateway timeout");

        var result = await job.RevalidateOnceAsync(Ct);

        Assert.Equal(new KeyRevalidationResult(1, 0, 0, 1, 0, 0), result);
        Assert.Equal(AgentState.Running, (await KeysLiveSeed.AgentAsync(_fixture, live.Id, Ct)).State);
        await using (var db = _fixture.CreateContext())
        {
            var key = await db.ExchangeKeys.SingleAsync(k => k.Id == keyId, Ct);
            Assert.Equal(KeyStatus.Valid, key.Status);
            Assert.Equal(LiveSeed.Now, key.LastValidatedAt);
        }

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message == $"Exchange key {keyId} of user {user.Id} could not be checked: Binance spot account info failed: gateway timeout");
        // Still due: the next pass checks it again.
        Assert.Equal(new KeyRevalidationResult(1, 0, 0, 1, 0, 0), await job.RevalidateOnceAsync(Ct));
        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, user.Id);
    }

    [Fact]
    public async Task AKeyReplacedDuringTheCheckIsNotMarkedByTheStaleResult()
    {
        await StartAsync();
        var user = await KeysLiveSeed.NewUserAsync(_fixture, "job-race@example.com", Ct);
        var exchange = new FakeExchangeClientFactory();
        var (provider, _, time) = Build(exchange);
        await using var _ = provider;
        var keys = provider.GetRequiredService<IKeyService>();
        var revalidation = provider.GetRequiredService<StoredKeyRevalidation>();
        var keyId = (await keys.AddExchangeKeyAsync(user.Id, new ExchangeKeyInput("raced", ExchangeEnvironment.Production, true, false, ApiKey, Secret), Ct)).KeyId!.Value;
        var live = await KeysLiveSeed.NewAgentAsync(_fixture, user.Id, "live", AgentMode.Live, AgentState.Running, keyId);
        time.Advance(TimeSpan.FromMinutes(60));
        exchange.OnPing = async (_, _, ct) =>
        {
            // The user replaces the credentials while the old ones are being checked.
            await using var db = _fixture.CreateContext();
            await db.ExchangeKeys.Where(k => k.Id == keyId).ExecuteUpdateAsync(s => s.SetProperty(k => k.ApiKey, "REPLACEDKEYREPLACEDKEYREPLACEDKEY000000"), ct);
            throw new ExchangeAuthException("Binance spot account info failed: Invalid API-key (code -2015)", -2015);
        };

        var result = await revalidation.RevalidateExchangeKeyAsync(keyId, Ct);

        Assert.Equal(KeyValidationOutcome.Invalid, result.Outcome);
        Assert.Equal(0, result.AgentsFailed);
        Assert.Equal(AgentState.Running, (await KeysLiveSeed.AgentAsync(_fixture, live.Id, Ct)).State);
        await using (var db = _fixture.CreateContext())
        {
            var key = await db.ExchangeKeys.SingleAsync(k => k.Id == keyId, Ct);
            Assert.Equal(KeyStatus.Valid, key.Status);
            Assert.Null(key.LastValidationError);
            Assert.Equal(LiveSeed.Now, key.LastValidatedAt);
        }

        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, user.Id);
    }

    [Fact]
    public async Task WithdrawalPermissionEnabledLaterInvalidatesTheKey()
    {
        await StartAsync();
        var user = await KeysLiveSeed.NewUserAsync(_fixture, "job-withdrawal@example.com", Ct);
        var exchange = new FakeExchangeClientFactory();
        var (provider, _, time) = Build(exchange);
        await using var _ = provider;
        var keys = provider.GetRequiredService<IKeyService>();
        var job = provider.GetRequiredService<KeyRevalidator>();
        var keyId = (await keys.AddExchangeKeyAsync(user.Id, new ExchangeKeyInput("later", ExchangeEnvironment.Production, false, true, ApiKey, Secret), Ct)).KeyId!.Value;
        time.Advance(TimeSpan.FromMinutes(60));
        exchange.OnPermissions = _ => FakeExchangeClientFactory.WithWithdrawals;

        var result = await job.RevalidateOnceAsync(Ct);

        Assert.Equal(new KeyRevalidationResult(1, 0, 1, 0, 0, 0), result);
        var view = (await keys.GetExchangeKeyAsync(user.Id, keyId, Ct))!;
        Assert.Equal(KeyStatus.Invalid, view.Status);
        Assert.Equal(KeyReasons.WithdrawalEnabled, view.LastValidationError);
        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, user.Id);
    }

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }

    private (ServiceProvider Provider, TestLoggerProvider Logs, FakeTimeProvider Time) Build(FakeExchangeClientFactory? exchange = null, FakeAiClientFactory? ai = null)
    {
        var logs = new TestLoggerProvider();
        var time = new FakeTimeProvider(LiveSeed.Now);
        return (KeysTestHost.BuildServices(_fixture.ConnectionString, logs, time: time, exchange: exchange, ai: ai), logs, time);
    }
}
