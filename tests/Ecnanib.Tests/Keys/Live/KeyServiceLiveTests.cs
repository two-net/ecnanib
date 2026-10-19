using System.Text.Json;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Ecnanib.Keys.Services;
using Ecnanib.ModelSync.Abstractions;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Keys.Live;

/// <summary>FR-KEY-01..06, FR-KEY-08, FR-KEY-09 and FR-SYNC-02 against a real PostgreSQL database, with the exchange and providers faked.</summary>
[Trait("Category", "Live")]
public class KeyServiceLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private const string ApiKey = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd";
    private const string Secret = "S3CR3T-VALUE-THAT-MUST-NEVER-LEAK-9876";
    private const string ProviderKey = "sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789wxyz";
    private readonly DatabaseLiveFixture _fixture;

    public KeyServiceLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AddsAProductionKeyValidatedMaskedAndNeverLogged()
    {
        await StartAsync();
        var user = await KeysLiveSeed.NewUserAsync(_fixture, "add@example.com", Ct);
        var exchange = new FakeExchangeClientFactory();
        var (provider, logs, _) = Build(exchange);
        await using var _ = provider;
        var keys = provider.GetRequiredService<IKeyService>();

        var result = await keys.AddExchangeKeyAsync(user.Id, new ExchangeKeyInput("  Main  ", ExchangeEnvironment.Production, true, true, ApiKey, Secret), Ct);

        Assert.Equal(KeySaveStatus.Succeeded, result.Status);
        Assert.NotNull(result.KeyId);
        Assert.Equal([MarketType.Spot, MarketType.UsdFutures], exchange.Pings.Select(p => p.Market));
        Assert.All(exchange.Pings, p => Assert.StartsWith(ExchangeKeyValidator.ScratchPrefix, p.Handle.KeyId));
        Assert.Equal([exchange.Pings[0].Handle.KeyId], exchange.Released);
        Assert.Empty(exchange.CachedKeyIds);

        await using (var db = _fixture.CreateContext())
        {
            var row = await db.ExchangeKeys.SingleAsync(k => k.Id == result.KeyId, Ct);
            Assert.Equal(user.Id, row.UserId);
            Assert.Equal("Main", row.Label);
            Assert.Equal(KeyStatus.Valid, row.Status);
            Assert.Equal(LiveSeed.Now, row.LastValidatedAt);
            Assert.Equal(LiveSeed.Now, row.CreatedAt);
            Assert.Null(row.LastValidationError);
            // A-02: stored plain; masked everywhere else.
            Assert.Equal(ApiKey, row.ApiKey);
            Assert.Equal(Secret, row.ApiSecret);
            Assert.True(row.PermissionsSnapshot!.Value.GetProperty("verified").GetBoolean());
            Assert.False(row.PermissionsSnapshot.Value.GetProperty("enableWithdrawals").GetBoolean());
        }

        var overview = await keys.GetOverviewAsync(user.Id, Ct);
        var view = Assert.Single(overview.ExchangeKeys);
        Assert.Equal("…abcd", view.MaskedApiKey);
        Assert.Equal(FakeExchangeClientFactory.TradingOnly, view.Permissions);
        Assert.Equal(0, view.ActiveAgents);
        Assert.Equal(4, overview.Providers.Count);
        Assert.All(overview.Providers, slot => Assert.Null(slot.Key));
        var json = JsonSerializer.Serialize(overview);
        Assert.DoesNotContain(ApiKey, json);
        Assert.DoesNotContain(Secret, json);

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message.StartsWith($"User {user.Id} added exchange key {result.KeyId} on Production", StringComparison.Ordinal));
        AssertNoSecrets(logs);
        Assert.DoesNotContain("Main", logs.AllText);
        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, user.Id);
    }

    [Fact]
    public async Task RefusedOrUnverifiableKeysStoreNothing()
    {
        await StartAsync();
        var user = await KeysLiveSeed.NewUserAsync(_fixture, "refused@example.com", Ct);
        var exchange = new FakeExchangeClientFactory();
        var (provider, logs, _) = Build(exchange);
        await using var _ = provider;
        var keys = provider.GetRequiredService<IKeyService>();
        var input = new ExchangeKeyInput("main", ExchangeEnvironment.Production, true, false, ApiKey, Secret);

        exchange.OnPermissions = _ => FakeExchangeClientFactory.WithWithdrawals;
        var withdrawal = await keys.AddExchangeKeyAsync(user.Id, input, Ct);
        Assert.Equal(KeySaveStatus.WithdrawalEnabled, withdrawal.Status);
        Assert.Equal(KeyReasons.WithdrawalEnabled, withdrawal.Detail);

        exchange.OnPermissions = null;
        exchange.OnPing = (_, _, _) => throw new ExchangeAuthException("Binance spot account info failed: Invalid API-key, IP, or permissions for action. (code -2015)", -2015);
        var rejected = await keys.AddExchangeKeyAsync(user.Id, input, Ct);
        Assert.Equal(KeySaveStatus.Rejected, rejected.Status);
        Assert.Contains("code -2015", rejected.Detail);

        exchange.OnPing = (_, _, _) => throw new ExchangeUnavailableException("Binance spot account info failed: connection refused");
        var failed = await keys.AddExchangeKeyAsync(user.Id, input, Ct);
        Assert.Equal(KeySaveStatus.Failed, failed.Status);
        Assert.Contains("connection refused", failed.Detail);

        await using (var db = _fixture.CreateContext())
            Assert.Equal(0, await db.ExchangeKeys.CountAsync(k => k.UserId == user.Id, Ct));
        Assert.Equal(3, exchange.Released.Count);
        Assert.Empty(exchange.CachedKeyIds);
        Assert.Equal(3, logs.Entries.Count(e => e.Level == LogLevel.Information && e.Message.StartsWith($"Exchange key for user {user.Id} not stored", StringComparison.Ordinal)));
        AssertNoSecrets(logs);
        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, user.Id);
    }

    [Fact]
    public async Task LabelsAreUniquePerUserAndCheckedBeforeAnyPing()
    {
        await StartAsync();
        var alice = await KeysLiveSeed.NewUserAsync(_fixture, "alice-label@example.com", Ct);
        var bob = await KeysLiveSeed.NewUserAsync(_fixture, "bob-label@example.com", Ct);
        var exchange = new FakeExchangeClientFactory();
        var (provider, _, _) = Build(exchange);
        await using var _ = provider;
        var keys = provider.GetRequiredService<IKeyService>();
        var input = new ExchangeKeyInput("dup", ExchangeEnvironment.Testnet, true, false, ApiKey, Secret);

        Assert.Equal(KeySaveStatus.Succeeded, (await keys.AddExchangeKeyAsync(alice.Id, input, Ct)).Status);
        Assert.Single(exchange.Pings);
        Assert.Equal(KeySaveStatus.LabelTaken, (await keys.AddExchangeKeyAsync(alice.Id, input, Ct)).Status);
        Assert.Single(exchange.Pings);
        Assert.Equal(KeySaveStatus.Succeeded, (await keys.AddExchangeKeyAsync(bob.Id, input, Ct)).Status);

        var view = Assert.Single((await keys.GetOverviewAsync(alice.Id, Ct)).ExchangeKeys);
        Assert.Equal(ExchangeEnvironment.Testnet, view.Environment);
        Assert.Equal(KeyStatus.Valid, view.Status);
        Assert.False(view.Permissions!.Verified);
        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, alice.Id, bob.Id);
    }

    [Fact]
    public async Task ReplacingKeepsTheIdAndReleasesTheStoredIdOnlyOnSuccess()
    {
        await StartAsync();
        var user = await KeysLiveSeed.NewUserAsync(_fixture, "replace@example.com", Ct);
        var exchange = new FakeExchangeClientFactory();
        var (provider, logs, time) = Build(exchange);
        await using var _ = provider;
        var keys = provider.GetRequiredService<IKeyService>();
        var keyId = (await keys.AddExchangeKeyAsync(user.Id, new ExchangeKeyInput("rotating", ExchangeEnvironment.Production, false, true, ApiKey, Secret), Ct)).KeyId!.Value;
        var agent = await KeysLiveSeed.NewAgentAsync(_fixture, user.Id, "live", AgentMode.Live, AgentState.Running, keyId);
        var storedId = keyId.ToString("D");
        const string newKey = "NEWKEYNEWKEYNEWKEYNEWKEYNEWKEY0000000001";
        time.Advance(TimeSpan.FromHours(1));

        exchange.OnPing = (h, _, _) => h.Credentials.ApiKey == newKey ? throw new ExchangeAuthException("Binance futures account info failed: Invalid API-key (code -2015)", -2015) : Task.CompletedTask;
        var refused = await keys.ReplaceExchangeKeyAsync(user.Id, keyId, newKey, "new-secret-0001", Ct);
        Assert.Equal(KeySaveStatus.Rejected, refused.Status);
        Assert.DoesNotContain(storedId, exchange.Released);
        await using (var db = _fixture.CreateContext())
        {
            var row = await db.ExchangeKeys.SingleAsync(k => k.Id == keyId, Ct);
            Assert.Equal(ApiKey, row.ApiKey);
            Assert.Equal(LiveSeed.Now, row.LastValidatedAt);
        }

        exchange.OnPing = null;
        Assert.Equal(KeySaveStatus.InvalidInput, (await keys.ReplaceExchangeKeyAsync(user.Id, keyId, newKey, "", Ct)).Status);
        Assert.Equal(KeySaveStatus.NotFound, (await keys.ReplaceExchangeKeyAsync(Guid.NewGuid(), keyId, newKey, "new-secret-0001", Ct)).Status);

        var replaced = await keys.ReplaceExchangeKeyAsync(user.Id, keyId, newKey, "new-secret-0001", Ct);
        Assert.Equal(KeySaveStatus.Succeeded, replaced.Status);
        Assert.Equal(keyId, replaced.KeyId);
        Assert.Contains(storedId, exchange.Released);
        Assert.Equal([MarketType.UsdFutures, MarketType.UsdFutures, MarketType.UsdFutures], exchange.Pings.Select(p => p.Market));
        await using (var db = _fixture.CreateContext())
        {
            var row = await db.ExchangeKeys.SingleAsync(k => k.Id == keyId, Ct);
            Assert.Equal(newKey, row.ApiKey);
            Assert.Equal("new-secret-0001", row.ApiSecret);
            Assert.Equal(KeyStatus.Valid, row.Status);
            Assert.Equal(LiveSeed.Now.AddHours(1), row.LastValidatedAt);
            Assert.Equal(keyId, (await db.Agents.SingleAsync(a => a.Id == agent.Id, Ct)).ExchangeKeyId);
        }

        Assert.Contains(logs.Entries, e => e.Message.StartsWith($"User {user.Id} replaced exchange key {keyId}", StringComparison.Ordinal));
        AssertNoSecrets(logs);
        Assert.DoesNotContain(newKey, logs.AllText);
        Assert.DoesNotContain("new-secret-0001", logs.AllText);
        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, user.Id);
    }

    [Fact]
    public async Task DeletionIsBlockedByActiveLiveAgentsUnlessTheyAreStoppedAsPartOfIt()
    {
        await StartAsync();
        var user = await KeysLiveSeed.NewUserAsync(_fixture, "delete@example.com", Ct);
        var other = await KeysLiveSeed.NewUserAsync(_fixture, "delete-other@example.com", Ct);
        var exchange = new FakeExchangeClientFactory();
        var (provider, logs, time) = Build(exchange);
        await using var _ = provider;
        var keys = provider.GetRequiredService<IKeyService>();
        var keyId = (await keys.AddExchangeKeyAsync(user.Id, new ExchangeKeyInput("to-delete", ExchangeEnvironment.Production, true, true, ApiKey, Secret), Ct)).KeyId!.Value;
        var running = await KeysLiveSeed.NewAgentAsync(_fixture, user.Id, "running", AgentMode.Live, AgentState.Running, keyId);
        var paused = await KeysLiveSeed.NewAgentAsync(_fixture, user.Id, "paused", AgentMode.Live, AgentState.Paused, keyId);
        var paper = await KeysLiveSeed.NewAgentAsync(_fixture, user.Id, "paper", AgentMode.Paper, AgentState.Running, keyId);
        var draft = await KeysLiveSeed.NewAgentAsync(_fixture, user.Id, "draft", AgentMode.Live, AgentState.Draft, keyId);
        var error = await KeysLiveSeed.NewAgentAsync(_fixture, user.Id, "error", AgentMode.Live, AgentState.Error, keyId);
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(2, (await keys.GetExchangeKeyAsync(user.Id, keyId, Ct))!.ActiveAgents);
        Assert.Null(await keys.GetExchangeKeyAsync(other.Id, keyId, Ct));
        Assert.Equal(new KeyDeletionResult(KeyDeletionStatus.NotFound), await keys.DeleteExchangeKeyAsync(other.Id, keyId, stopAgents: true, Ct));

        var blocked = await keys.DeleteExchangeKeyAsync(user.Id, keyId, stopAgents: false, Ct);
        Assert.Equal(new KeyDeletionResult(KeyDeletionStatus.AgentsInUse, 2), blocked);
        Assert.Equal(AgentState.Running, (await KeysLiveSeed.AgentAsync(_fixture, running.Id, Ct)).State);
        Assert.NotNull(await keys.GetExchangeKeyAsync(user.Id, keyId, Ct));
        Assert.DoesNotContain(keyId.ToString("D"), exchange.Released);

        var deleted = await keys.DeleteExchangeKeyAsync(user.Id, keyId, stopAgents: true, Ct);
        Assert.Equal(new KeyDeletionResult(KeyDeletionStatus.Succeeded, 2), deleted);
        Assert.Contains(keyId.ToString("D"), exchange.Released);
        Assert.Null(await keys.GetExchangeKeyAsync(user.Id, keyId, Ct));

        foreach (var id in new[] { running.Id, paused.Id })
        {
            var agent = await KeysLiveSeed.AgentAsync(_fixture, id, Ct);
            Assert.Equal(AgentState.Stopped, agent.State);
            Assert.Equal(KeyReasons.StoppedExchangeKeyDeleted, agent.StateReason);
            Assert.Null(agent.PauseCause);
            Assert.Null(agent.ExchangeKeyId);
            Assert.Equal(LiveSeed.Now.AddMinutes(5), agent.UpdatedAt);
        }

        foreach (var (id, state) in new[] { (paper.Id, AgentState.Running), (draft.Id, AgentState.Draft), (error.Id, AgentState.Error) })
        {
            var agent = await KeysLiveSeed.AgentAsync(_fixture, id, Ct);
            Assert.Equal(state, agent.State);
            Assert.Null(agent.ExchangeKeyId);
            Assert.Equal(LiveSeed.Now, agent.UpdatedAt);
        }

        Assert.Contains(logs.Entries, e => e.Message == $"User {user.Id} removed exchange key {keyId}; 2 agent(s) stopped");
        AssertNoSecrets(logs);
        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, user.Id, other.Id);
    }

    [Fact]
    public async Task ProviderKeysAreOnePerProviderValidatedBySyncingTheCatalog()
    {
        await StartAsync();
        var user = await KeysLiveSeed.NewUserAsync(_fixture, "provider@example.com", Ct);
        var ai = new FakeAiClientFactory();
        var (provider, logs, _) = Build(ai: ai);
        await using var _ = provider;
        var keys = provider.GetRequiredService<IKeyService>();

        var added = await keys.SetProviderKeyAsync(user.Id, AiProvider.Claude, " " + ProviderKey + " ", Ct);

        Assert.Equal(KeySaveStatus.Succeeded, added.Status);
        Assert.Equal(2, added.ModelCount);
        Assert.True(added.CatalogSynced);
        var listing = Assert.Single(ai.Listings);
        Assert.StartsWith(ExchangeKeyValidator.ScratchPrefix, listing.KeyId);
        Assert.Equal(AiProvider.Claude, listing.Provider);
        await using (var db = _fixture.CreateContext())
        {
            var row = await db.ProviderKeys.SingleAsync(k => k.UserId == user.Id, Ct);
            Assert.Equal(added.KeyId, row.Id);
            Assert.Equal(ProviderKey, row.ApiKey);
            Assert.Equal(KeyStatus.Valid, row.Status);
            Assert.Equal(LiveSeed.Now, row.LastValidatedAt);
            Assert.Equal(["claude-large", "claude-small"], await db.ProviderModels.Where(m => m.Provider == AiProvider.Claude).OrderBy(m => m.ModelId).Select(m => m.ModelId).ToListAsync(Ct));
            var state = await db.ProviderSyncStates.SingleAsync(s => s.Provider == AiProvider.Claude, Ct);
            Assert.Equal(SyncStatus.Succeeded, state.LastStatus);
            Assert.Equal(SyncTrigger.KeyValidation, state.Trigger);
            Assert.Equal(2, state.ModelCount);
        }

        const string second = "sk-ant-api03-SECONDKEYSECONDKEYSECONDKEY0000";
        var replaced = await keys.SetProviderKeyAsync(user.Id, AiProvider.Claude, second, Ct);
        Assert.Equal(KeySaveStatus.Succeeded, replaced.Status);
        Assert.Equal(added.KeyId, replaced.KeyId);
        await using (var db = _fixture.CreateContext())
        {
            var row = await db.ProviderKeys.SingleAsync(k => k.UserId == user.Id, Ct);
            Assert.Equal(second, row.ApiKey);
        }

        var slot = await keys.GetProviderSlotAsync(user.Id, AiProvider.Claude, Ct);
        Assert.Equal("…0000", slot.Key!.MaskedApiKey);
        var overview = await keys.GetOverviewAsync(user.Id, Ct);
        Assert.Equal([AiProvider.Claude, AiProvider.Gemini, AiProvider.Gpt, AiProvider.Grok], overview.Providers.Select(s => s.Provider));
        Assert.Single(overview.Providers, s => s.Key is not null);

        Assert.Contains(logs.Entries, e => e.Message == $"User {user.Id} added Claude key {added.KeyId}; 2 model(s) listed");
        Assert.Contains(logs.Entries, e => e.Message == $"User {user.Id} replaced Claude key {added.KeyId}; 2 model(s) listed");
        Assert.DoesNotContain(ProviderKey, logs.AllText);
        Assert.DoesNotContain(second, logs.AllText);
        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, user.Id);
    }

    [Fact]
    public async Task ACatalogFailureNeverFailsTheKeySave()
    {
        await StartAsync();
        var user = await KeysLiveSeed.NewUserAsync(_fixture, "catalog-failure@example.com", Ct);
        var catalog = new FakeProviderCatalogSync { Failure = new InvalidOperationException("catalog unavailable") };
        var (provider, logs, _) = Build(configure: s => s.AddSingleton<IProviderCatalogSync>(catalog));
        await using var _ = provider;
        var keys = provider.GetRequiredService<IKeyService>();

        var result = await keys.SetProviderKeyAsync(user.Id, AiProvider.Gemini, ProviderKey, Ct);

        Assert.Equal(KeySaveStatus.Succeeded, result.Status);
        Assert.False(result.CatalogSynced);
        Assert.Equal(2, result.ModelCount);
        var call = Assert.Single(catalog.Calls);
        Assert.Equal((AiProvider.Gemini, SyncTrigger.KeyValidation), (call.Provider, call.Trigger));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message == "Catalog sync for Gemini after key validation failed; the key is stored");
        Assert.NotNull((await keys.GetProviderSlotAsync(user.Id, AiProvider.Gemini, Ct)).Key);
        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, user.Id);
    }

    [Fact]
    public async Task RejectedProviderKeysStoreNothing()
    {
        await StartAsync();
        var user = await KeysLiveSeed.NewUserAsync(_fixture, "provider-refused@example.com", Ct);
        var ai = new FakeAiClientFactory { OnListModels = (_, _) => throw new AiAuthException("GPT list models failed: Incorrect API key provided (invalid_api_key)", AiProvider.Gpt, 401, "invalid_api_key") };
        var (provider, logs, _) = Build(ai: ai);
        await using var _ = provider;
        var keys = provider.GetRequiredService<IKeyService>();

        var rejected = await keys.SetProviderKeyAsync(user.Id, AiProvider.Gpt, ProviderKey, Ct);
        Assert.Equal(KeySaveStatus.Rejected, rejected.Status);
        Assert.Contains("invalid_api_key", rejected.Detail);

        ai.OnListModels = (_, _) => throw new AiUnavailableException("GPT list models failed: bad gateway (HTTP 502)", AiProvider.Gpt, AiErrorKind.ServerError, 502);
        var failed = await keys.SetProviderKeyAsync(user.Id, AiProvider.Gpt, ProviderKey, Ct);
        Assert.Equal(KeySaveStatus.Failed, failed.Status);

        await using (var db = _fixture.CreateContext())
        {
            Assert.Equal(0, await db.ProviderKeys.CountAsync(k => k.UserId == user.Id, Ct));
            Assert.Equal(0, await db.ProviderModels.CountAsync(m => m.Provider == AiProvider.Gpt, Ct));
        }

        Assert.Null((await keys.GetProviderSlotAsync(user.Id, AiProvider.Gpt, Ct)).Key);
        Assert.DoesNotContain(ProviderKey, logs.AllText);
        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, user.Id);
    }

    [Fact]
    public async Task ProviderDeletionCountsOnlyEnabledAssignmentsOfThisUser()
    {
        await StartAsync();
        var user = await KeysLiveSeed.NewUserAsync(_fixture, "provider-delete@example.com", Ct);
        var other = await KeysLiveSeed.NewUserAsync(_fixture, "provider-delete-other@example.com", Ct);
        var (provider, _, time) = Build();
        await using var _ = provider;
        var keys = provider.GetRequiredService<IKeyService>();
        Assert.Equal(KeySaveStatus.Succeeded, (await keys.SetProviderKeyAsync(user.Id, AiProvider.Claude, ProviderKey, Ct)).Status);
        var uses = await KeysLiveSeed.NewAgentAsync(_fixture, user.Id, "uses", AgentMode.Paper, AgentState.Running, null, (AiProvider.Claude, true), (AiProvider.Grok, true));
        var disabled = await KeysLiveSeed.NewAgentAsync(_fixture, user.Id, "disabled", AgentMode.Paper, AgentState.Paused, null, (AiProvider.Claude, false));
        var otherProvider = await KeysLiveSeed.NewAgentAsync(_fixture, user.Id, "grok", AgentMode.Live, AgentState.Running, null, (AiProvider.Grok, true));
        var stopped = await KeysLiveSeed.NewAgentAsync(_fixture, user.Id, "stopped", AgentMode.Paper, AgentState.Stopped, null, (AiProvider.Claude, true));
        var foreign = await KeysLiveSeed.NewAgentAsync(_fixture, other.Id, "foreign", AgentMode.Paper, AgentState.Running, null, (AiProvider.Claude, true));
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, (await keys.GetProviderSlotAsync(user.Id, AiProvider.Claude, Ct)).ActiveAgents);
        Assert.Equal(2, (await keys.GetOverviewAsync(user.Id, Ct)).Providers.Single(s => s.Provider == AiProvider.Grok).ActiveAgents);
        Assert.Equal(new KeyDeletionResult(KeyDeletionStatus.NotFound), await keys.DeleteProviderKeyAsync(other.Id, AiProvider.Claude, true, Ct));
        Assert.Equal(new KeyDeletionResult(KeyDeletionStatus.AgentsInUse, 1), await keys.DeleteProviderKeyAsync(user.Id, AiProvider.Claude, false, Ct));

        var deleted = await keys.DeleteProviderKeyAsync(user.Id, AiProvider.Claude, true, Ct);

        Assert.Equal(new KeyDeletionResult(KeyDeletionStatus.Succeeded, 1), deleted);
        var usesAfter = await KeysLiveSeed.AgentAsync(_fixture, uses.Id, Ct);
        Assert.Equal(AgentState.Stopped, usesAfter.State);
        Assert.Equal("Stopped: its Claude key was deleted.", usesAfter.StateReason);
        Assert.Equal(AgentState.Paused, (await KeysLiveSeed.AgentAsync(_fixture, disabled.Id, Ct)).State);
        Assert.Equal(AgentState.Running, (await KeysLiveSeed.AgentAsync(_fixture, otherProvider.Id, Ct)).State);
        Assert.Equal(AgentState.Stopped, (await KeysLiveSeed.AgentAsync(_fixture, stopped.Id, Ct)).State);
        Assert.Equal(AgentState.Running, (await KeysLiveSeed.AgentAsync(_fixture, foreign.Id, Ct)).State);
        Assert.Null((await keys.GetProviderSlotAsync(user.Id, AiProvider.Claude, Ct)).Key);
        Assert.Equal(new KeyDeletionResult(KeyDeletionStatus.NotFound), await keys.DeleteProviderKeyAsync(user.Id, AiProvider.Claude, true, Ct));
        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, user.Id, other.Id);
    }

    [Fact]
    public async Task ValidateNowUpdatesTheStatusAndFailsTheAgentsUsingAnInvalidKey()
    {
        await StartAsync();
        var user = await KeysLiveSeed.NewUserAsync(_fixture, "validate-now@example.com", Ct);
        var exchange = new FakeExchangeClientFactory();
        var ai = new FakeAiClientFactory();
        var (provider, logs, time) = Build(exchange, ai);
        await using var _ = provider;
        var keys = provider.GetRequiredService<IKeyService>();
        var keyId = (await keys.AddExchangeKeyAsync(user.Id, new ExchangeKeyInput("checked", ExchangeEnvironment.Production, true, false, ApiKey, Secret), Ct)).KeyId!.Value;
        Assert.Equal(KeySaveStatus.Succeeded, (await keys.SetProviderKeyAsync(user.Id, AiProvider.Claude, ProviderKey, Ct)).Status);
        var live = await KeysLiveSeed.NewAgentAsync(_fixture, user.Id, "live", AgentMode.Live, AgentState.Running, keyId, (AiProvider.Claude, true));
        var paperPaused = await KeysLiveSeed.NewAgentAsync(_fixture, user.Id, "paper", AgentMode.Paper, AgentState.Paused, keyId, (AiProvider.Claude, true));
        time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(KeyValidationOutcome.NotFound, (await keys.ValidateExchangeKeyAsync(Guid.NewGuid(), keyId, Ct)).Outcome);
        Assert.Equal(KeyValidationOutcome.NotFound, (await keys.ValidateProviderKeyAsync(user.Id, AiProvider.Grok, Ct)).Outcome);

        exchange.OnPing = (_, _, _) => throw new ExchangeAuthException("Binance spot account info failed: API-key format invalid. (code -2014)", -2014);
        var invalid = await keys.ValidateExchangeKeyAsync(user.Id, keyId, Ct);
        Assert.Equal(KeyValidationOutcome.Invalid, invalid.Outcome);
        Assert.Contains("code -2014", invalid.Detail);
        Assert.Equal(1, invalid.AgentsFailed);
        var view = (await keys.GetExchangeKeyAsync(user.Id, keyId, Ct))!;
        Assert.Equal(KeyStatus.Invalid, view.Status);
        Assert.Equal(invalid.Detail, view.LastValidationError);
        Assert.Equal(LiveSeed.Now.AddMinutes(10), view.LastValidatedAt);
        var failedAgent = await KeysLiveSeed.AgentAsync(_fixture, live.Id, Ct);
        Assert.Equal(AgentState.Error, failedAgent.State);
        Assert.Equal($"Exchange key 'checked' is invalid: {invalid.Detail}", failedAgent.StateReason);
        Assert.Equal(AgentState.Paused, (await KeysLiveSeed.AgentAsync(_fixture, paperPaused.Id, Ct)).State);

        exchange.OnPing = null;
        time.Advance(TimeSpan.FromMinutes(10));
        var valid = await keys.ValidateExchangeKeyAsync(user.Id, keyId, Ct);
        Assert.Equal(KeyValidationOutcome.Valid, valid.Outcome);
        view = (await keys.GetExchangeKeyAsync(user.Id, keyId, Ct))!;
        Assert.Equal(KeyStatus.Valid, view.Status);
        Assert.Null(view.LastValidationError);
        Assert.Equal(LiveSeed.Now.AddMinutes(20), view.LastValidatedAt);
        Assert.Equal(AgentState.Error, (await KeysLiveSeed.AgentAsync(_fixture, live.Id, Ct)).State);
        Assert.Contains(logs.Entries, e => e.Message == $"Exchange key {keyId} of user {user.Id} is valid again");

        var providerValid = await keys.ValidateProviderKeyAsync(user.Id, AiProvider.Claude, Ct);
        Assert.Equal(KeyValidationOutcome.Valid, providerValid.Outcome);
        Assert.Equal(2, providerValid.ModelCount);
        Assert.True(providerValid.CatalogSynced);
        ai.OnListModels = (_, _) => throw new AiAuthException("Claude list models failed: invalid x-api-key (authentication_error)", AiProvider.Claude, 401, "authentication_error");
        var providerInvalid = await keys.ValidateProviderKeyAsync(user.Id, AiProvider.Claude, Ct);
        Assert.Equal(KeyValidationOutcome.Invalid, providerInvalid.Outcome);
        Assert.Equal(1, providerInvalid.AgentsFailed);
        var pausedAfter = await KeysLiveSeed.AgentAsync(_fixture, paperPaused.Id, Ct);
        Assert.Equal(AgentState.Error, pausedAfter.State);
        Assert.Equal($"Claude key is invalid: {providerInvalid.Detail}", pausedAfter.StateReason);
        Assert.Null(pausedAfter.PauseCause);
        Assert.Equal(KeyStatus.Invalid, (await keys.GetProviderSlotAsync(user.Id, AiProvider.Claude, Ct)).Key!.Status);

        AssertNoSecrets(logs);
        Assert.DoesNotContain(ProviderKey, logs.AllText);
        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, user.Id);
    }

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }

    private (ServiceProvider Provider, TestLoggerProvider Logs, FakeTimeProvider Time) Build(FakeExchangeClientFactory? exchange = null, FakeAiClientFactory? ai = null, Action<IServiceCollection>? configure = null)
    {
        var logs = new TestLoggerProvider();
        var time = new FakeTimeProvider(LiveSeed.Now);
        return (KeysTestHost.BuildServices(_fixture.ConnectionString, logs, time: time, exchange: exchange, ai: ai, configure: configure), logs, time);
    }

    private static void AssertNoSecrets(TestLoggerProvider logs)
    {
        Assert.DoesNotContain(ApiKey, logs.AllText);
        Assert.DoesNotContain(Secret, logs.AllText);
    }
}
