using Ecnanib.Accounts.Abstractions;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Services;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Agents.Live;

/// <summary>FR-AGT-01..04, FR-AGT-07, FR-AGT-12..15, FR-ACC-08 and FR-KEY-08/09 against a real PostgreSQL database, with the exchange, providers and streams faked.</summary>
[Trait("Category", "Live")]
public class AgentServiceLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public AgentServiceLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>FR-SYNC-03: each role's model is looked up in the shared catalog; the list flags an agent only for an enabled role on a deprecated model.</summary>
    [Fact]
    public async Task GetReportsEachRolesCatalogStatusAndTheListFlagsAgentsOnADeprecatedModel()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "deprecated@example.com");
        await using (var db = _fixture.CreateContext())
        {
            db.AddRange(
                new ProviderModel { Provider = AiProvider.Claude, ModelId = "sync-shared", DisplayName = "sync-shared", FirstSeenAt = AgentsLiveSeed.Now, LastSeenAt = AgentsLiveSeed.Now, Status = ModelStatus.Deprecated },
                new ProviderModel { Provider = AiProvider.Gpt, ModelId = "sync-shared", DisplayName = "sync-shared", FirstSeenAt = AgentsLiveSeed.Now, LastSeenAt = AgentsLiveSeed.Now },
                new ProviderModel { Provider = AiProvider.Gemini, ModelId = "sync-disabled", DisplayName = "sync-disabled", FirstSeenAt = AgentsLiveSeed.Now, LastSeenAt = AgentsLiveSeed.Now, Status = ModelStatus.Deprecated });
            await db.SaveChangesAsync(Ct);
        }

        // Every role of a seeded agent runs the same model id on its Appendix B provider.
        var flagged = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "flagged", model: "sync-shared");
        var clean = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "clean", model: "sync-unknown");
        var disabledOnly = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "disabled-only", model: "sync-disabled");
        await using (var db = _fixture.CreateContext())
            await db.CouncilAssignments.Where(c => c.AgentId == disabledOnly.Id && c.Role == CouncilRole.CrossMarketContext).ExecuteUpdateAsync(s => s.SetProperty(c => c.Enabled, false), Ct);
        var (provider, _, _, _, _) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agents = provider.GetRequiredService<IAgentService>();

        var detail = await agents.GetAsync(user.Id, flagged.Id, Ct);

        Assert.Equal(ModelStatus.Available, detail!.Council.Single(c => c.Role == CouncilRole.MarketAnalyst).CatalogStatus);
        Assert.Null(detail.Council.Single(c => c.Role == CouncilRole.SentimentFlow).CatalogStatus);
        Assert.Equal(ModelStatus.Deprecated, detail.Council.Single(c => c.Role == CouncilRole.RiskManager).CatalogStatus);
        Assert.All((await agents.GetAsync(user.Id, clean.Id, Ct))!.Council, c => Assert.Null(c.CatalogStatus));
        Assert.Equal(ModelStatus.Deprecated, (await agents.GetAsync(user.Id, disabledOnly.Id, Ct))!.Council.Single(c => c.Role == CouncilRole.CrossMarketContext).CatalogStatus);

        var list = await agents.ListAsync(user.Id, Ct);

        Assert.True(list.Single(a => a.Id == flagged.Id).HasDeprecatedModel);
        Assert.False(list.Single(a => a.Id == clean.Id).HasDeprecatedModel);
        Assert.False(list.Single(a => a.Id == disabledOnly.Id).HasDeprecatedModel);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task CreatesADraftWithTheTimeframeDefaultsAndTheAppendixBCouncil()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "create@example.com");
        await using (var db = _fixture.CreateContext())
        {
            db.AddRange(
                new ProviderModel { Provider = AiProvider.Claude, ModelId = "claude-old", DisplayName = "old", ProviderCreatedAt = AgentsLiveSeed.Now.AddDays(10), FirstSeenAt = AgentsLiveSeed.Now, LastSeenAt = AgentsLiveSeed.Now },
                new ProviderModel { Provider = AiProvider.Claude, ModelId = "claude-new", DisplayName = "new", ProviderCreatedAt = AgentsLiveSeed.Now.AddDays(30), FirstSeenAt = AgentsLiveSeed.Now, LastSeenAt = AgentsLiveSeed.Now },
                new ProviderModel { Provider = AiProvider.Claude, ModelId = "claude-gone", DisplayName = "gone", ProviderCreatedAt = AgentsLiveSeed.Now.AddDays(60), FirstSeenAt = AgentsLiveSeed.Now, LastSeenAt = AgentsLiveSeed.Now, Status = ModelStatus.Deprecated });
            await db.SaveChangesAsync(Ct);
        }

        var (provider, logs, _, exchange, _) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agents = provider.GetRequiredService<IAgentService>();

        var result = await agents.CreateAsync(user.Id, new AgentCreateInput("  BTC swing ", MarketType.UsdFutures, " ethusdt ", CandleInterval.FourHours, AgentMode.Paper, Guid.NewGuid(), 5, null), Ct);

        Assert.Equal(AgentSaveStatus.Succeeded, result.Status);
        var agent = await AgentsLiveSeed.AgentAsync(_fixture, result.AgentId!.Value);
        Assert.Equal("BTC swing", agent.Name);
        Assert.Equal("ETHUSDT", agent.Symbol);
        Assert.Equal(AgentState.Draft, agent.State);
        Assert.Equal("swing-pullback", agent.StrategyId);
        Assert.Equal(1, agent.StrategyVersion);
        Assert.Equal("{}", Canonical(agent.Parameters));
        Assert.Null(agent.ExchangeKeyId);
        Assert.Equal(5, agent.Leverage);
        Assert.Equal(MarginType.Isolated, agent.MarginType);
        Assert.Equal(1m, agent.RiskLimits.RiskPerTradePercent);
        Assert.Equal(1, agent.RiskLimits.MaxOpenPositions);
        Assert.Equal(3m, agent.RiskLimits.DailyLossLimitPercent);
        Assert.Equal(4, agent.CouncilAssignments.Count);
        Assert.Equal("claude-new", agent.CouncilAssignments.Single(c => c.Role == CouncilRole.RiskManager).Model);
        Assert.Equal(AiProvider.Claude, agent.CouncilAssignments.Single(c => c.Role == CouncilRole.RiskManager).Provider);
        Assert.Equal(AiProvider.Gpt, agent.CouncilAssignments.Single(c => c.Role == CouncilRole.MarketAnalyst).Provider);
        // The catalog is shared by every test: the Market Analyst gets GPT's newest available model, or nothing when there is none.
        await using (var db = _fixture.CreateContext())
        {
            var newestGpt = await db.ProviderModels.Where(m => m.Provider == AiProvider.Gpt && m.Status == ModelStatus.Available)
                .OrderByDescending(m => m.ProviderCreatedAt ?? m.FirstSeenAt).ThenBy(m => m.ModelId).Select(m => m.ModelId).FirstOrDefaultAsync(Ct) ?? "";
            Assert.Equal(newestGpt, agent.CouncilAssignments.Single(c => c.Role == CouncilRole.MarketAnalyst).Model);
        }
        Assert.All(agent.CouncilAssignments, c => Assert.True(c.Enabled));
        Assert.Equal([(ExchangeEnvironment.Production, MarketType.UsdFutures)], exchange.ExchangeInfoReads);
        var changes = await AgentsLiveSeed.ChangesAsync(_fixture, agent.Id);
        Assert.Equal(ChangeSet.CreatedField, Assert.Single(changes).Field);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message.Contains($"created agent {agent.Id}", StringComparison.Ordinal));

        var summary = Assert.Single(await agents.ListAsync(user.Id, Ct));
        Assert.Equal("Swing Pullback", summary.StrategyName);
        Assert.Null(summary.LastOutcome);
        Assert.Null(await agents.GetAsync(Guid.NewGuid(), agent.Id, Ct));
        var detail = await agents.GetAsync(user.Id, agent.Id, Ct);
        Assert.Equal(4, detail!.Council.Count);
        Assert.Equal(CouncilRole.RiskManager, detail.Council[3].Role);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task CreateRefusesAnUnknownSymbolAnUnreachableExchangeAndTheAgentsLimit()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "refuse@example.com");
        var (provider, _, _, exchange, _) = AgentsLiveSeed.Build(_fixture, new() { ["Agents:MaxAgentsPerUser"] = "1", ["Agents:MaxRunningAgentsPerUser"] = "1" });
        await using var _ = provider;
        var agents = provider.GetRequiredService<IAgentService>();

        var unknown = await agents.CreateAsync(user.Id, new AgentCreateInput("x", MarketType.Spot, "DOGEUSDT", CandleInterval.OneHour, AgentMode.Paper, null, null, null), Ct);
        Assert.Equal(AgentSaveStatus.InvalidInput, unknown.Status);
        Assert.Equal("symbol", Assert.Single(unknown.Errors!).Field);

        exchange.OnExchangeInfo = (_, _) => throw new TimeoutException("binance down");
        var down = await agents.CreateAsync(user.Id, new AgentCreateInput("x", MarketType.Spot, "BTCUSDT", CandleInterval.OneHour, AgentMode.Paper, null, null, null), Ct);
        Assert.Equal(AgentSaveStatus.ExchangeUnavailable, down.Status);
        Assert.Empty(await agents.ListAsync(user.Id, Ct));

        exchange.OnExchangeInfo = (_, _) => FakeExchangeClientFactory.Listing("BTCUSDT");
        var first = await agents.CreateAsync(user.Id, new AgentCreateInput("x", MarketType.Spot, "BTCUSDT", CandleInterval.OneHour, AgentMode.Paper, null, null, null), Ct);
        Assert.Equal(AgentSaveStatus.Succeeded, first.Status);
        var second = await agents.CreateAsync(user.Id, new AgentCreateInput("y", MarketType.Spot, "BTCUSDT", CandleInterval.OneHour, AgentMode.Paper, null, null, null), Ct);
        Assert.Equal(AgentSaveStatus.LimitReached, second.Status);
        Assert.Equal(1, second.Limit!.Current);
        Assert.Equal(1, second.Limit.Max);
        var clone = await agents.CloneAsync(user.Id, first.AgentId!.Value, Ct);
        Assert.Equal(AgentActionStatus.LimitReached, clone.Status);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task LiveModeNeedsOneOfTheUsersKeysForTheMarket()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "livekey@example.com");
        var other = await AgentsLiveSeed.NewUserAsync(_fixture, "livekey-other@example.com");
        var (provider, _, _, exchange, _) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var keys = provider.GetRequiredService<IKeyService>();
        var agents = provider.GetRequiredService<IAgentService>();
        var spotOnly = await AgentsLiveSeed.AddExchangeKeyAsync(keys, user.Id, ExchangeEnvironment.Testnet, spot: true, futures: false);
        var theirs = await AgentsLiveSeed.AddExchangeKeyAsync(keys, other.Id, ExchangeEnvironment.Production, spot: true, futures: true);

        var foreign = await agents.CreateAsync(user.Id, new AgentCreateInput("x", MarketType.Spot, "BTCUSDT", CandleInterval.OneHour, AgentMode.Live, theirs, null, null), Ct);
        Assert.Equal(AgentSaveStatus.InvalidInput, foreign.Status);
        Assert.Equal("exchangeKeyId", Assert.Single(foreign.Errors!).Field);

        var wrongMarket = await agents.CreateAsync(user.Id, new AgentCreateInput("x", MarketType.UsdFutures, "BTCUSDT", CandleInterval.OneHour, AgentMode.Live, spotOnly, 3, null), Ct);
        Assert.Equal(AgentSaveStatus.InvalidInput, wrongMarket.Status);
        Assert.Equal("exchangeKeyId", Assert.Single(wrongMarket.Errors!).Field);

        exchange.ExchangeInfoReads.Clear();
        var ok = await agents.CreateAsync(user.Id, new AgentCreateInput("x", MarketType.Spot, "BTCUSDT", CandleInterval.OneHour, AgentMode.Live, spotOnly, null, null), Ct);
        Assert.Equal(AgentSaveStatus.Succeeded, ok.Status);
        Assert.Equal([(ExchangeEnvironment.Testnet, MarketType.Spot)], exchange.ExchangeInfoReads);
        Assert.Equal(spotOnly, (await AgentsLiveSeed.AgentAsync(_fixture, ok.AgentId!.Value)).ExchangeKeyId);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id, other.Id);
    }

    [Fact]
    public async Task SettingsEditsFollowTheStateRulesAndRecordHistory()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "settings@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC");
        var (provider, _, _, _, _) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agents = provider.GetRequiredService<IAgentService>();

        var edited = await agents.UpdateSettingsAsync(user.Id, agent.Id,
            new AgentSettingsInput("ETH swing", MarketType.Spot, "ethusdt", CandleInterval.FourHours, AgentMode.Paper, null, null, null, true), Ct);
        Assert.Equal(AgentSaveStatus.Succeeded, edited.Status);
        var row = await AgentsLiveSeed.AgentAsync(_fixture, agent.Id);
        Assert.Equal("ETH swing", row.Name);
        Assert.Equal("ETHUSDT", row.Symbol);
        Assert.Equal(CandleInterval.FourHours, row.Timeframe);
        Assert.Equal("swing-pullback", row.StrategyId);
        Assert.True(row.Discretionary);
        Assert.Equal(["name", "discretionary", "symbol", "timeframe", "strategyId"], (await AgentsLiveSeed.ChangesAsync(_fixture, agent.Id)).Select(c => c.Field));

        var unchanged = await agents.UpdateSettingsAsync(user.Id, agent.Id,
            new AgentSettingsInput("ETH swing", MarketType.Spot, "ETHUSDT", CandleInterval.FourHours, AgentMode.Paper, null, null, null, true), Ct);
        Assert.Equal(AgentSaveStatus.Succeeded, unchanged.Status);
        Assert.Equal(5, (await AgentsLiveSeed.ChangesAsync(_fixture, agent.Id)).Count);

        await AgentsLiveSeed.SetStateAsync(_fixture, agent.Id, AgentState.Running);
        var blocked = await agents.UpdateSettingsAsync(user.Id, agent.Id,
            new AgentSettingsInput("ETH swing", MarketType.Spot, "BTCUSDT", CandleInterval.FourHours, AgentMode.Paper, null, null, null, true), Ct);
        Assert.Equal(AgentSaveStatus.InvalidState, blocked.Status);
        Assert.Contains("the symbol", blocked.Detail);
        var renamed = await agents.UpdateSettingsAsync(user.Id, agent.Id,
            new AgentSettingsInput("ETH running", MarketType.Spot, "ETHUSDT", CandleInterval.FourHours, AgentMode.Paper, null, null, null, true), Ct);
        Assert.Equal(AgentSaveStatus.Succeeded, renamed.Status);
        Assert.Equal("ETH running", (await AgentsLiveSeed.AgentAsync(_fixture, agent.Id)).Name);

        var someoneElse = await agents.UpdateSettingsAsync(Guid.NewGuid(), agent.Id,
            new AgentSettingsInput("x", MarketType.Spot, "ETHUSDT", CandleInterval.FourHours, AgentMode.Paper, null, null, null, true), Ct);
        Assert.Equal(AgentSaveStatus.NotFound, someoneElse.Status);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task StrategyParametersCouncilAndRiskLimitsAreValidatedSavedAndRecorded()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "sections@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC");
        var (provider, _, _, _, _) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agents = provider.GetRequiredService<IAgentService>();

        var wrongTimeframe = await agents.UpdateStrategyAsync(user.Id, agent.Id, new StrategyInput("swing-pullback", new Dictionary<string, string?>()), Ct);
        Assert.Equal(AgentSaveStatus.InvalidInput, wrongTimeframe.Status);
        var badValue = await agents.UpdateStrategyAsync(user.Id, agent.Id, new StrategyInput("trend-pullback", new Dictionary<string, string?> { [StrategyParameters.AtrPeriod] = "abc" }), Ct);
        Assert.Equal("parameters.atrPeriod", Assert.Single(badValue.Errors!).Field);
        var crossed = await agents.UpdateStrategyAsync(user.Id, agent.Id, new StrategyInput("trend-pullback", new Dictionary<string, string?> { [StrategyParameters.EmaFastPeriod] = "60" }), Ct);
        Assert.Equal("parameters.emaFastPeriod", Assert.Single(crossed.Errors!).Field);

        var saved = await agents.UpdateStrategyAsync(user.Id, agent.Id, new StrategyInput("range-breakout-retest",
            new Dictionary<string, string?> { [StrategyParameters.AtrPeriod] = "21", [StrategyParameters.TrailingMethod] = "ema", [StrategyParameters.EmaFastPeriod] = "20" }), Ct);
        Assert.Equal(AgentSaveStatus.Succeeded, saved.Status);
        var row = await AgentsLiveSeed.AgentAsync(_fixture, agent.Id);
        Assert.Equal("range-breakout-retest", row.StrategyId);
        Assert.Equal("{}", Canonical(row.Parameters));
        var tuned = await agents.UpdateStrategyAsync(user.Id, agent.Id, new StrategyInput("range-breakout-retest",
            new Dictionary<string, string?> { [StrategyParameters.AtrPeriod] = "21", [StrategyParameters.TrailingMethod] = "ema", [StrategyParameters.EmaFastPeriod] = "20" }), Ct);
        Assert.Equal(AgentSaveStatus.Succeeded, tuned.Status);
        row = await AgentsLiveSeed.AgentAsync(_fixture, agent.Id);
        Assert.Equal("""{"atrPeriod":21,"trailingMethod":"ema"}""", Canonical(row.Parameters));
        var reset = await agents.UpdateStrategyAsync(user.Id, agent.Id, new StrategyInput("range-breakout-retest", new Dictionary<string, string?>(), ResetToDefaults: true), Ct);
        Assert.Equal(AgentSaveStatus.Succeeded, reset.Status);
        Assert.Equal("{}", Canonical((await AgentsLiveSeed.AgentAsync(_fixture, agent.Id)).Parameters));
        var changes = await AgentsLiveSeed.ChangesAsync(_fixture, agent.Id);
        Assert.Contains(changes, c => c.Field == "strategyId" && c.NewValue!.Value.GetString() == "range-breakout-retest");
        Assert.Contains(changes, c => c.Field == "parameters.atrPeriod" && c.OldValue!.Value.GetInt32() == 14 && c.NewValue!.Value.GetInt32() == 21);
        Assert.Contains(changes, c => c.Field == "parameters.atrPeriod" && c.OldValue!.Value.GetInt32() == 21 && c.NewValue!.Value.GetInt32() == 14);

        var disabledRiskManager = await agents.UpdateCouncilAsync(user.Id, agent.Id, new CouncilInput(
        [
            new CouncilRoleInput(CouncilRole.MarketAnalyst, AiProvider.Gpt, "gpt-x", true),
            new CouncilRoleInput(CouncilRole.SentimentFlow, AiProvider.Grok, "grok-x", true),
            new CouncilRoleInput(CouncilRole.CrossMarketContext, AiProvider.Gemini, "gemini-x", true),
            new CouncilRoleInput(CouncilRole.RiskManager, AiProvider.Claude, "claude-x", false),
        ]), Ct);
        Assert.Equal(AgentSaveStatus.InvalidInput, disabledRiskManager.Status);
        var council = await agents.UpdateCouncilAsync(user.Id, agent.Id, new CouncilInput(
        [
            new CouncilRoleInput(CouncilRole.MarketAnalyst, AiProvider.Claude, "claude-analyst", true),
            new CouncilRoleInput(CouncilRole.SentimentFlow, AiProvider.Grok, "", false),
            new CouncilRoleInput(CouncilRole.CrossMarketContext, AiProvider.Gemini, "test-model", true),
            new CouncilRoleInput(CouncilRole.RiskManager, AiProvider.Claude, " claude-risk ", true),
        ]), Ct);
        Assert.Equal(AgentSaveStatus.Succeeded, council.Status);
        row = await AgentsLiveSeed.AgentAsync(_fixture, agent.Id);
        Assert.Equal(AiProvider.Claude, row.CouncilAssignments.Single(c => c.Role == CouncilRole.MarketAnalyst).Provider);
        Assert.Equal("claude-risk", row.CouncilAssignments.Single(c => c.Role == CouncilRole.RiskManager).Model);
        Assert.False(row.CouncilAssignments.Single(c => c.Role == CouncilRole.SentimentFlow).Enabled);
        changes = await AgentsLiveSeed.ChangesAsync(_fixture, agent.Id);
        Assert.Contains(changes, c => c.Field == "council.marketAnalyst.provider");
        Assert.Contains(changes, c => c.Field == "council.sentimentFlow.enabled");
        Assert.DoesNotContain(changes, c => c.Field == "council.crossMarketContext.model");

        var overCap = await agents.UpdateRiskLimitsAsync(user.Id, agent.Id, new RiskLimitsInput(3m, 1, 3m, null), Ct);
        Assert.Equal(AgentSaveStatus.InvalidInput, overCap.Status);
        var risk = await agents.UpdateRiskLimitsAsync(user.Id, agent.Id, new RiskLimitsInput(0.5m, 2, 4m, 2500m), Ct);
        Assert.Equal(AgentSaveStatus.Succeeded, risk.Status);
        row = await AgentsLiveSeed.AgentAsync(_fixture, agent.Id);
        Assert.Equal(0.5m, row.RiskLimits.RiskPerTradePercent);
        Assert.Equal(2, row.RiskLimits.MaxOpenPositions);
        Assert.Equal(4m, row.RiskLimits.DailyLossLimitPercent);
        Assert.Equal(2500m, row.RiskLimits.MaxNotional);
        Assert.Contains(await AgentsLiveSeed.ChangesAsync(_fixture, agent.Id), c => c.Field == "riskLimits.maxNotional");

        await AgentsLiveSeed.SetStateAsync(_fixture, agent.Id, AgentState.Paused);
        var whilePaused = await agents.UpdateRiskLimitsAsync(user.Id, agent.Id, new RiskLimitsInput(1m, 2, 4m, 2500m), Ct);
        Assert.Equal(AgentSaveStatus.Succeeded, whilePaused.Status);
        await AgentsLiveSeed.SetStateAsync(_fixture, agent.Id, AgentState.Running);
        var whileRunning = await agents.UpdateRiskLimitsAsync(user.Id, agent.Id, new RiskLimitsInput(1.5m, 2, 4m, 2500m), Ct);
        Assert.Equal(AgentSaveStatus.InvalidState, whileRunning.Status);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task CloneCopiesTheConfigurationIntoADraftAndDeleteNeedsDraftOrStopped()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "clone@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", state: AgentState.Running, market: MarketType.UsdFutures, discretionary: true);
        await using (var db = _fixture.CreateContext())
        {
            await db.Agents.Where(a => a.Id == agent.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Parameters, LiveSeed.Json("""{"atrPeriod":21}""")), Ct);
        }

        await using (var db = _fixture.CreateContext())
        {
            db.EntryWindows.Add(new EntryWindow { AgentId = agent.Id, Kind = EntryWindowKind.Recurring, StartDay = DayOfWeek.Friday, StartTime = new TimeOnly(21, 0), EndDay = DayOfWeek.Monday, EndTime = new TimeOnly(1, 0) });
            db.EntryWindows.Add(new EntryWindow { AgentId = agent.Id, Kind = EntryWindowKind.OneOff, StartAt = AgentsLiveSeed.Now.AddDays(1), EndAt = AgentsLiveSeed.Now.AddDays(2) });
            await db.SaveChangesAsync(Ct);
        }

        var (provider, _, _, _, _) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agents = provider.GetRequiredService<IAgentService>();

        var cloned = await agents.CloneAsync(user.Id, agent.Id, Ct);
        Assert.Equal(AgentActionStatus.Succeeded, cloned.Status);
        var copy = await AgentsLiveSeed.AgentAsync(_fixture, cloned.AgentId!.Value);
        Assert.Equal("BTC (copy)", copy.Name);
        Assert.Equal(2, copy.EntryWindows.Count);
        Assert.Contains(copy.EntryWindows, w => w.Kind == EntryWindowKind.Recurring && w.StartDay == DayOfWeek.Friday);
        Assert.Equal(AgentState.Draft, copy.State);
        Assert.Equal(MarketType.UsdFutures, copy.MarketType);
        Assert.Equal(3, copy.Leverage);
        Assert.True(copy.Discretionary);
        Assert.Equal("""{"atrPeriod":21}""", Canonical(copy.Parameters));
        Assert.Equal(4, copy.CouncilAssignments.Count);
        Assert.Equal(ChangeSet.CreatedField, Assert.Single(await AgentsLiveSeed.ChangesAsync(_fixture, copy.Id)).Field);

        var running = await agents.DeleteAsync(user.Id, agent.Id, Ct);
        Assert.Equal(AgentActionStatus.InvalidState, running.Status);
        var deleted = await agents.DeleteAsync(user.Id, copy.Id, Ct);
        Assert.Equal(AgentActionStatus.Succeeded, deleted.Status);
        Assert.Equal(AgentActionStatus.NotFound, (await agents.DeleteAsync(user.Id, copy.Id, Ct)).Status);
        Assert.Single(await agents.ListAsync(user.Id, Ct));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task StartChecksConfigurationProviderKeysAndTheRunningLimitThenTracksTheAgent()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "start@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", model: null);
        var second = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "ETH", symbol: "ETHUSDT");
        var gone = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "gone", strategyId: "no-such-strategy");
        var (provider, logs, _, _, streams) = AgentsLiveSeed.Build(_fixture, new() { ["Agents:MaxRunningAgentsPerUser"] = "1" });
        await using var _ = provider;
        var agents = provider.GetRequiredService<IAgentService>();
        var keys = provider.GetRequiredService<IKeyService>();
        var runtime = provider.GetRequiredService<AgentScheduler>();
        var events = new List<AgentEvent>();
        using var subscription = provider.GetRequiredService<IAgentEvents>().Subscribe(user.Id, e =>
        {
            lock (events)
                events.Add(e);
            return ValueTask.CompletedTask;
        });

        var noModel = await agents.StartAsync(user.Id, agent.Id, Ct);
        Assert.Equal(AgentActionStatus.InvalidConfiguration, noModel.Status);
        Assert.Contains("has no model", noModel.Detail);

        Assert.Equal(AgentActionStatus.StrategyUnavailable, (await agents.StartAsync(user.Id, gone.Id, Ct)).Status);

        var council = await agents.UpdateCouncilAsync(user.Id, agent.Id, new CouncilInput(AgentRules.Roles.Select(r => new CouncilRoleInput(r, AgentRules.DefaultProvider(r), "m", true)).ToList()), Ct);
        Assert.Equal(AgentSaveStatus.Succeeded, council.Status);
        var noKeys = await agents.StartAsync(user.Id, agent.Id, Ct);
        Assert.Equal(AgentActionStatus.ProviderKeyMissing, noKeys.Status);
        Assert.Contains("Claude, Gemini, GPT, Grok", noKeys.Detail);

        await AgentsLiveSeed.AddProviderKeysAsync(keys, user.Id, AiProvider.Claude, AiProvider.Gemini, AiProvider.Gpt, AiProvider.Grok);
        var started = await agents.StartAsync(user.Id, agent.Id, Ct);
        Assert.Equal(AgentActionStatus.Succeeded, started.Status);
        var row = await AgentsLiveSeed.AgentAsync(_fixture, agent.Id);
        Assert.Equal(AgentState.Running, row.State);
        Assert.Null(row.StateReason);
        Assert.True(runtime.IsTracked(agent.Id));
        Assert.True(streams.HasCandleSubscription(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour));
        Assert.Contains(await AgentsLiveSeed.ChangesAsync(_fixture, agent.Id), c => c.Field == ChangeSet.StateField);
        await Wait.UntilAsync(() => events.OfType<AgentStateChanged>().Any(e => e.AgentId == agent.Id && e.State == AgentState.Running), "event");
        Assert.Contains(logs.Entries, e => e.Message.Contains($"started agent {agent.Id}", StringComparison.Ordinal));

        var limited = await agents.StartAsync(user.Id, second.Id, Ct);
        Assert.Equal(AgentActionStatus.LimitReached, limited.Status);
        Assert.Equal(1, limited.Limit!.Current);
        Assert.Equal(AgentActionStatus.InvalidState, (await agents.StartAsync(user.Id, agent.Id, Ct)).Status);
        Assert.Equal(AgentActionStatus.NotFound, (await agents.StartAsync(Guid.NewGuid(), second.Id, Ct)).Status);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task TwoConcurrentStartsRespectTheRunningLimit()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "race@example.com");
        var a = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "A");
        var b = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "B", symbol: "ETHUSDT");
        var (provider, _, _, _, _) = AgentsLiveSeed.Build(_fixture, new() { ["Agents:MaxRunningAgentsPerUser"] = "1" });
        await using var _ = provider;
        var agents = provider.GetRequiredService<IAgentService>();
        await AgentsLiveSeed.AddProviderKeysAsync(provider.GetRequiredService<IKeyService>(), user.Id, AiProvider.Claude, AiProvider.Gemini, AiProvider.Gpt, AiProvider.Grok);

        var results = await Task.WhenAll(agents.StartAsync(user.Id, a.Id, Ct), agents.StartAsync(user.Id, b.Id, Ct));

        Assert.Equal(1, results.Count(r => r.Status == AgentActionStatus.Succeeded));
        Assert.Equal(1, results.Count(r => r.Status == AgentActionStatus.LimitReached));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task LiveModeNeedsTheDisclaimerAndAValidKeyAndTracksTheKeysEnvironment()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "live@example.com");
        var (provider, _, _, _, streams) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var keys = provider.GetRequiredService<IKeyService>();
        var agents = provider.GetRequiredService<IAgentService>();
        var accounts = provider.GetRequiredService<IAccountService>();
        var keyId = await AgentsLiveSeed.AddExchangeKeyAsync(keys, user.Id, ExchangeEnvironment.Testnet, spot: true, futures: true);
        await AgentsLiveSeed.AddProviderKeysAsync(keys, user.Id, AiProvider.Claude, AiProvider.Gemini, AiProvider.Gpt, AiProvider.Grok);
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "ETH", mode: AgentMode.Live, market: MarketType.UsdFutures, symbol: "ETHUSDT", exchangeKeyId: keyId);

        Assert.Equal(AgentActionStatus.DisclaimerRequired, (await agents.StartAsync(user.Id, agent.Id, Ct)).Status);
        await accounts.AcceptDisclaimerAsync(user.Id, Ct);

        await using (var db = _fixture.CreateContext())
            await db.ExchangeKeys.Where(k => k.Id == keyId).ExecuteUpdateAsync(s => s.SetProperty(k => k.Status, KeyStatus.Invalid).SetProperty(k => k.LastValidationError, "revoked"), Ct);
        var invalid = await agents.StartAsync(user.Id, agent.Id, Ct);
        Assert.Equal(AgentActionStatus.KeyUnavailable, invalid.Status);
        Assert.Equal("revoked", invalid.Detail);
        await using (var db = _fixture.CreateContext())
            await db.ExchangeKeys.Where(k => k.Id == keyId).ExecuteUpdateAsync(s => s.SetProperty(k => k.Status, KeyStatus.Valid), Ct);

        var started = await agents.StartAsync(user.Id, agent.Id, Ct);
        Assert.Equal(AgentActionStatus.Succeeded, started.Status);
        Assert.True(streams.HasCandleSubscription(new MarketKey(ExchangeEnvironment.Testnet, MarketType.UsdFutures, "ETHUSDT"), CandleInterval.OneHour));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task PauseResumeAndStopFollowAppendixE()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "lifecycle@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC");
        var (provider, _, _, _, streams) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agents = provider.GetRequiredService<IAgentService>();
        var runtime = provider.GetRequiredService<AgentScheduler>();
        await AgentsLiveSeed.AddProviderKeysAsync(provider.GetRequiredService<IKeyService>(), user.Id, AiProvider.Claude, AiProvider.Gemini, AiProvider.Gpt, AiProvider.Grok);

        Assert.Equal(AgentActionStatus.InvalidState, (await agents.PauseAsync(user.Id, agent.Id, Ct)).Status);
        Assert.Equal(AgentActionStatus.InvalidState, (await agents.StopAsync(user.Id, agent.Id, false, Ct)).Status);
        Assert.Equal(AgentActionStatus.Succeeded, (await agents.StartAsync(user.Id, agent.Id, Ct)).Status);

        Assert.Equal(AgentActionStatus.Succeeded, (await agents.PauseAsync(user.Id, agent.Id, Ct)).Status);
        var paused = await AgentsLiveSeed.AgentAsync(_fixture, agent.Id);
        Assert.Equal(AgentState.Paused, paused.State);
        Assert.Equal(PauseCause.User, paused.PauseCause);
        Assert.Equal(AgentService.PausedByUser, paused.StateReason);
        Assert.True(runtime.IsTracked(agent.Id));
        Assert.Equal(AgentActionStatus.InvalidState, (await agents.PauseAsync(user.Id, agent.Id, Ct)).Status);

        Assert.Equal(AgentActionStatus.Succeeded, (await agents.ResumeAsync(user.Id, agent.Id, Ct)).Status);
        var resumed = await AgentsLiveSeed.AgentAsync(_fixture, agent.Id);
        Assert.Equal(AgentState.Running, resumed.State);
        Assert.Null(resumed.PauseCause);
        Assert.Null(resumed.StateReason);

        var stopped = await agents.StopAsync(user.Id, agent.Id, false, Ct);
        Assert.Equal(AgentActionStatus.Succeeded, stopped.Status);
        Assert.Contains("0 order(s) cancelled", stopped.Detail);
        var row = await AgentsLiveSeed.AgentAsync(_fixture, agent.Id);
        Assert.Equal(AgentState.Stopped, row.State);
        Assert.Equal(AgentService.StoppedByUser, row.StateReason);
        Assert.False(runtime.IsTracked(agent.Id));
        Assert.False(streams.HasCandleSubscription(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour));
        Assert.Equal(AgentActionStatus.InvalidState, (await agents.ResumeAsync(user.Id, agent.Id, Ct)).Status);

        Assert.Equal(AgentActionStatus.Succeeded, (await agents.StartAsync(user.Id, agent.Id, Ct)).Status);
        Assert.True(runtime.IsTracked(agent.Id));
        await AgentsLiveSeed.SetStateAsync(_fixture, agent.Id, AgentState.Error);
        Assert.Equal(AgentActionStatus.Succeeded, (await agents.StopAsync(user.Id, agent.Id, true, Ct)).Status);
        Assert.Equal(AgentState.Stopped, (await AgentsLiveSeed.AgentAsync(_fixture, agent.Id)).State);
        var states = (await AgentsLiveSeed.ChangesAsync(_fixture, agent.Id)).Where(c => c.Field == ChangeSet.StateField).ToList();
        Assert.Equal(6, states.Count);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task AParametersOnlySaveKeepsThePinnedVersionAndAStrategyChangeRePins()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "pinned@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC");
        var (provider, _, _, _, _) = AgentsLiveSeed.Build(_fixture, configure: s => AgentsLiveSeed.WithCatalog(s,
            inner => new VersionBumpingCatalog(inner, new Dictionary<string, int> { ["trend-pullback"] = 2, ["range-breakout-retest"] = 3 }, StrategyParameters.MarketFallback)));
        await using var _ = provider;
        var agents = provider.GetRequiredService<IAgentService>();

        var saved = await agents.UpdateStrategyAsync(user.Id, agent.Id, new StrategyInput("trend-pullback", new Dictionary<string, string?> { [StrategyParameters.AtrPeriod] = "21", [StrategyParameters.MarketFallback] = "true" }), Ct);
        Assert.Equal(AgentSaveStatus.Succeeded, saved.Status);
        var row = await AgentsLiveSeed.AgentAsync(_fixture, agent.Id);
        Assert.Equal(1, row.StrategyVersion);
        Assert.Equal("""{"atrPeriod":21,"marketFallback":true}""", Canonical(row.Parameters));
        Assert.DoesNotContain(await AgentsLiveSeed.ChangesAsync(_fixture, agent.Id), c => c.Field == "strategyVersion");
        var detail = await agents.GetAsync(user.Id, agent.Id, Ct);
        Assert.Equal(1, detail!.StrategyVersion);
        Assert.Equal(2, detail.LatestStrategyVersion);
        Assert.Equal("Trend Pullback", Assert.Single(await agents.ListAsync(user.Id, Ct)).StrategyName);

        var changed = await agents.UpdateStrategyAsync(user.Id, agent.Id, new StrategyInput("range-breakout-retest", new Dictionary<string, string?>()), Ct);
        Assert.Equal(AgentSaveStatus.Succeeded, changed.Status);
        row = await AgentsLiveSeed.AgentAsync(_fixture, agent.Id);
        Assert.Equal(3, row.StrategyVersion);
        Assert.Equal("{}", Canonical(row.Parameters));
        var changes = await AgentsLiveSeed.ChangesAsync(_fixture, agent.Id);
        Assert.Contains(changes, c => c.Field == "strategyVersion" && c.OldValue!.Value.GetInt32() == 1 && c.NewValue!.Value.GetInt32() == 3);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task UpgradeMovesToTheNewestVersionResetsWhatItNoLongerAcceptsAndFollowsTheState()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "upgrade@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", state: AgentState.Running);
        await using (var db = _fixture.CreateContext())
            await db.Agents.Where(a => a.Id == agent.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Parameters, LiveSeed.Json("""{"atrPeriod":21,"marketFallback":true}""")), Ct);
        var (provider, _, _, _, _) = AgentsLiveSeed.Build(_fixture, configure: s => AgentsLiveSeed.WithCatalog(s,
            inner => new VersionBumpingCatalog(inner, new Dictionary<string, int> { ["trend-pullback"] = 2 }, StrategyParameters.MarketFallback)));
        await using var _ = provider;
        var agents = provider.GetRequiredService<IAgentService>();

        var running = await agents.UpgradeStrategyAsync(user.Id, agent.Id, Ct);
        Assert.Equal(AgentSaveStatus.InvalidState, running.Status);
        Assert.Contains("strategy version", running.Detail);
        Assert.Equal(1, (await AgentsLiveSeed.AgentAsync(_fixture, agent.Id)).StrategyVersion);

        await AgentsLiveSeed.SetStateAsync(_fixture, agent.Id, AgentState.Paused);
        var upgraded = await agents.UpgradeStrategyAsync(user.Id, agent.Id, Ct);
        Assert.Equal(AgentSaveStatus.Succeeded, upgraded.Status);
        Assert.Contains("marketFallback", upgraded.Detail);
        var row = await AgentsLiveSeed.AgentAsync(_fixture, agent.Id);
        Assert.Equal(2, row.StrategyVersion);
        Assert.Equal("""{"atrPeriod":21}""", Canonical(row.Parameters));
        var changes = await AgentsLiveSeed.ChangesAsync(_fixture, agent.Id);
        Assert.Contains(changes, c => c.Field == "strategyVersion" && c.OldValue!.Value.GetInt32() == 1 && c.NewValue!.Value.GetInt32() == 2);
        Assert.Contains(changes, c => c.Field == "parameters.marketFallback" && c.OldValue!.Value.GetBoolean() && c.NewValue is null);
        Assert.DoesNotContain(changes, c => c.Field == "parameters.atrPeriod");

        var again = await agents.UpgradeStrategyAsync(user.Id, agent.Id, Ct);
        Assert.Equal(AgentSaveStatus.Succeeded, again.Status);
        Assert.Null(again.Detail);
        Assert.Equal(changes.Count, (await AgentsLiveSeed.ChangesAsync(_fixture, agent.Id)).Count);
        Assert.Equal(AgentSaveStatus.NotFound, (await agents.UpgradeStrategyAsync(Guid.NewGuid(), agent.Id, Ct)).Status);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task StartRefusesAMissingPinnedVersionAndInvalidStoredParameters()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "pinned-start@example.com");
        var old = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "old", strategyVersion: 0);
        var bad = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "bad", symbol: "ETHUSDT");
        await using (var db = _fixture.CreateContext())
            await db.Agents.Where(a => a.Id == bad.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Parameters, LiveSeed.Json("""{"atrPeriod":1000}""")), Ct);
        var (provider, _, _, _, _) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agents = provider.GetRequiredService<IAgentService>();
        await AgentsLiveSeed.AddProviderKeysAsync(provider.GetRequiredService<IKeyService>(), user.Id, AiProvider.Claude, AiProvider.Gemini, AiProvider.Gpt, AiProvider.Grok);

        var missing = await agents.StartAsync(user.Id, old.Id, Ct);
        Assert.Equal(AgentActionStatus.StrategyUnavailable, missing.Status);
        Assert.Contains("upgrade", missing.Detail);
        var editing = await agents.UpdateStrategyAsync(user.Id, old.Id, new StrategyInput("trend-pullback", new Dictionary<string, string?> { [StrategyParameters.AtrPeriod] = "21" }), Ct);
        Assert.Equal(AgentSaveStatus.InvalidInput, editing.Status);
        Assert.Contains("upgrade", Assert.Single(editing.Errors!).Message);
        Assert.Equal(AgentSaveStatus.Succeeded, (await agents.UpgradeStrategyAsync(user.Id, old.Id, Ct)).Status);
        Assert.Equal(1, (await AgentsLiveSeed.AgentAsync(_fixture, old.Id)).StrategyVersion);

        var invalid = await agents.StartAsync(user.Id, bad.Id, Ct);
        Assert.Equal(AgentActionStatus.InvalidConfiguration, invalid.Status);
        Assert.Contains("strategy parameters", invalid.Detail);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task EntryWindowsAreAddedRemovedRecordedAndGuardedByState()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "windows@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC");
        var (provider, _, _, _, _) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agents = provider.GetRequiredService<IAgentService>();
        var weekend = new EntryWindowInput(EntryWindowKind.Recurring, StartDay: DayOfWeek.Friday, StartTime: new TimeOnly(21, 0), EndDay: DayOfWeek.Monday, EndTime: new TimeOnly(1, 0));
        var macro = new EntryWindowInput(EntryWindowKind.OneOff, AgentsLiveSeed.Now.AddDays(1), AgentsLiveSeed.Now.AddDays(1).AddHours(2));

        Assert.Equal(AgentSaveStatus.Succeeded, (await agents.AddEntryWindowAsync(user.Id, agent.Id, weekend, Ct)).Status);
        Assert.Equal(AgentSaveStatus.Succeeded, (await agents.AddEntryWindowAsync(user.Id, agent.Id, macro, Ct)).Status);
        var duplicate = await agents.AddEntryWindowAsync(user.Id, agent.Id, weekend, Ct);
        Assert.Equal(AgentSaveStatus.InvalidInput, duplicate.Status);
        Assert.Contains("already exists", Assert.Single(duplicate.Errors!).Message);
        var detail = await agents.GetAsync(user.Id, agent.Id, Ct);
        Assert.Equal(2, detail!.EntryWindows.Count);
        Assert.Equal(EntryWindowKind.Recurring, detail.EntryWindows[0].Kind);
        Assert.Equal(DayOfWeek.Friday, detail.EntryWindows[0].StartDay);
        Assert.Equal(AgentsLiveSeed.Now.AddDays(1), detail.EntryWindows[1].StartAt);
        var changes = (await AgentsLiveSeed.ChangesAsync(_fixture, agent.Id)).Where(c => c.Field == "entryWindows").ToList();
        Assert.Equal(2, changes.Count);
        Assert.Equal(0, changes[0].OldValue!.Value.GetArrayLength());
        Assert.Equal(1, changes[0].NewValue!.Value.GetArrayLength());
        Assert.Equal(2, changes[1].NewValue!.Value.GetArrayLength());
        Assert.Contains("\"Friday\"", changes[0].NewValue!.Value.GetRawText());
        Assert.Contains("21:00:00", changes[0].NewValue!.Value.GetRawText());

        await AgentsLiveSeed.SetStateAsync(_fixture, agent.Id, AgentState.Running);
        Assert.Equal(AgentSaveStatus.InvalidState, (await agents.AddEntryWindowAsync(user.Id, agent.Id, new EntryWindowInput(EntryWindowKind.OneOff, AgentsLiveSeed.Now.AddDays(3), AgentsLiveSeed.Now.AddDays(4)), Ct)).Status);
        Assert.Equal(AgentSaveStatus.InvalidState, (await agents.RemoveEntryWindowAsync(user.Id, agent.Id, detail.EntryWindows[1].Id, Ct)).Status);
        await AgentsLiveSeed.SetStateAsync(_fixture, agent.Id, AgentState.Paused);
        Assert.Equal(AgentSaveStatus.Succeeded, (await agents.RemoveEntryWindowAsync(user.Id, agent.Id, detail.EntryWindows[1].Id, Ct)).Status);
        var removedTwice = await agents.RemoveEntryWindowAsync(user.Id, agent.Id, detail.EntryWindows[1].Id, Ct);
        Assert.Equal(AgentSaveStatus.InvalidInput, removedTwice.Status);
        Assert.Single((await agents.GetAsync(user.Id, agent.Id, Ct))!.EntryWindows);
        Assert.Equal(3, (await AgentsLiveSeed.ChangesAsync(_fixture, agent.Id)).Count(c => c.Field == "entryWindows"));
        Assert.Equal(AgentSaveStatus.NotFound, (await agents.AddEntryWindowAsync(Guid.NewGuid(), agent.Id, macro, Ct)).Status);

        for (var i = 1; i < AgentRules.MaxEntryWindows; i++)
            Assert.Equal(AgentSaveStatus.Succeeded, (await agents.AddEntryWindowAsync(user.Id, agent.Id, new EntryWindowInput(EntryWindowKind.OneOff, AgentsLiveSeed.Now.AddDays(i + 1), AgentsLiveSeed.Now.AddDays(i + 1).AddHours(1)), Ct)).Status);
        var tooMany = await agents.AddEntryWindowAsync(user.Id, agent.Id, new EntryWindowInput(EntryWindowKind.OneOff, AgentsLiveSeed.Now.AddDays(40), AgentsLiveSeed.Now.AddDays(41)), Ct);
        Assert.Equal(AgentSaveStatus.InvalidInput, tooMany.Status);
        Assert.Contains("at most 20", Assert.Single(tooMany.Errors!).Message);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    /// <summary>jsonb re-formats what it stores; comparing the canonical shape keeps the assertions about values.</summary>
    private static string Canonical(System.Text.Json.JsonElement parameters) => StrategyParameters.Write(StrategyParameters.Read(parameters)).GetRawText();

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }
}
