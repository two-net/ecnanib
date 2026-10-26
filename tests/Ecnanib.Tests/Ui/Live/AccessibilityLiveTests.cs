using System.Net;
using System.Text.Json;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Components.Agents;
using Ecnanib.Components.Experience;
using Ecnanib.Components.Logging;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Configuration;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Execution.Live;
using Ecnanib.Tests.Experience.Live;
using Ecnanib.Tests.ModelSync.Live;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Ui.Live;

/// <summary>
/// NFR-USE-01 and NFR-USE-03 on every signed-in page of FR-UI-01, rendered by the application's own pipeline against a real
/// database: one account seeded with a Binance key, provider keys, a catalog with a deprecated model, three agents (running
/// with a decided, a failed and an in-flight evaluation, an open and a closed trade, statistics, log rows, a change and a
/// window; a draft with every setup field enabled; a paused live agent on the key), so that every table, form, island,
/// island branch and notice renders with content and passes <see cref="AccessibilityChecks"/>, in English, with its own title.
/// </summary>
[Trait("Category", "Live")]
public class AccessibilityLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private const string Email = "a11y@example.com";
    private static readonly DateTimeOffset Now = LiveSeed.Now;
    private readonly DatabaseLiveFixture _fixture;

    public AccessibilityLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EverySignedInPageIsStructurallyAccessibleEnglishAndTitled()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        await using var host = await PipelineTestHost.StartAsync(connectionString: _fixture.ConnectionString);
        var user = await PipelineSession.RegisterAsync(host, Email);
        var seeded = await SeedAsync(user.Id);
        var session = await PipelineSession.SignInAsync(host, Email);

        var problems = await PageTour.RunAsync(host, Pages(seeded), session);

        Assert.Empty(problems);

        // The seed reached the pages: the branches the checker had to see rendered with content, not as empty states.
        foreach (var (path, markers) in Markers(seeded))
        {
            var (_, html) = await PipelineSession.GetAsync(host, path, session);
            // Blazor writes non-ASCII text as entities (an ellipsis is &#x2026;); the markers are read as a browser would.
            var text = WebUtility.HtmlDecode(html);
            foreach (var marker in markers)
                Assert.True(text.Contains(marker, StringComparison.Ordinal), $"{path} does not contain \"{marker}\".");
        }

        // WCAG 2.2.1: the status page sits still until asked, and the reloading variant says how to stop.
        var (_, still) = await PipelineSession.GetAsync(host, "/status", session);
        Assert.Null(AccessibilityChecks.Parse(still).QuerySelector("meta[http-equiv=refresh]"));
        Assert.Contains("Reload every minute", still, StringComparison.Ordinal);
        var (_, reloading) = await PipelineSession.GetAsync(host, "/status?reload=1", session);
        Assert.Equal("60;url=/status?reload=1", AccessibilityChecks.Parse(reloading).QuerySelector("meta[http-equiv=refresh]")?.GetAttribute("content"));
        Assert.Contains("Stop reloading", reloading, StringComparison.Ordinal);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    private static IEnumerable<PageExpectation> Pages(Seeded s)
    {
        static PageExpectation Exact(string path, string title) => new(path, HttpStatusCode.OK, t => t == title, $"\"{title}\"");
        static PageExpectation Starts(string path, string prefix) => new(path, HttpStatusCode.OK, t => t.StartsWith(prefix, StringComparison.Ordinal), $"starting with \"{prefix}\"");

        yield return Exact("/agents", "Dashboard");
        yield return Exact("/agents?status=deleted", "Dashboard");
        yield return Exact("/agents/new", "New agent");
        yield return Exact($"/agents/{s.Running}", "Running BTC");
        yield return Exact($"/agents/{s.Draft}", "Draft BTC");
        yield return Exact($"/agents/{s.Paused}", "Paused live");
        yield return Exact($"/agents/{s.Draft}/setup", "Set up Draft BTC: Basics");
        foreach (var step in WizardSteps.Ordered)
            yield return Exact(WizardSteps.Href(s.Draft, step), $"Set up Draft BTC: {WizardSteps.Title(step)}");
        yield return Exact(WizardSteps.Href(s.Running, WizardStep.Council), "Set up Running BTC: Council");
        yield return Exact(WizardSteps.Href(s.Running, WizardStep.Review), "Set up Running BTC: Review and start");
        yield return Exact($"/agents/{s.Running}/log", "Log of Running BTC");
        yield return Exact($"/agents/{s.Running}/log?{LogFilters.LevelName}={LogFilters.LevelValue(LogLevel.Warning)}&{LogFilters.ComponentName}=Ecnanib.Tests", "Log of Running BTC");
        yield return Starts($"/agents/{s.Running}/evaluations/{s.DecidedEvaluation}", "Evaluation of Running BTC, ");
        yield return Starts($"/agents/{s.Running}/evaluations/{s.InFlightEvaluation}", "Evaluation of Running BTC, ");
        yield return Exact("/keys", "Keys");
        yield return Exact($"/keys/exchange/{s.Key}", "Binance key “testnet”");
        yield return Exact($"/keys/providers/{ProviderNames.Route(AiProvider.Claude)}", "Claude key");
        yield return Exact($"/keys/providers/{ProviderNames.Route(AiProvider.Gpt)}", "GPT key");
        yield return Exact("/providers", "Providers");
        yield return Exact($"/providers?{ExperienceFilters.RoleName}={ExperienceFilters.RoleValue(CouncilRole.RiskManager)}", "Providers");
        yield return Exact($"/providers/{ProviderNames.Route(AiProvider.Claude)}", "Claude experience");
        yield return Exact("/settings", "Settings");
        yield return Exact("/settings/delete-account", "Delete account");
        yield return Exact("/status", "System status");
    }

    /// <summary>What the seeded content must have put on the page; each is a branch the structural checks are meant to cover.</summary>
    private static IEnumerable<(string Path, string[] Markers)> Markers(Seeded s)
    {
        yield return ("/agents", ["Evaluating…", "Running BTC", "Paused live", "Stop all my agents", "aria-live=\"polite\""]);
        yield return ($"/agents/{s.Running}", ["Evaluating…", "Open positions", "Open orders", "Closed trades", "Entry deliberation", "Change history", "no longer lists", "Price chart of BTCUSDT", "300 candles", "Details</summary>"]);
        yield return (WizardSteps.Href(s.Draft, WizardStep.Council), ["aria-label=\"Market Analyst model\"", "aria-label=\"Risk Manager model id\"", "Model id for every role"]);
        yield return (WizardSteps.Href(s.Draft, WizardStep.Risk), ["From time", "Until day", ">Remove<", "Every Saturday"]);
        yield return (WizardSteps.Href(s.Draft, WizardStep.Strategy), [", default "]);
        yield return ($"/agents/{s.Running}/evaluations/{s.DecidedEvaluation}", ["Limits check", "Execution check", "Shadow outcome", "low volume"]);
        yield return ($"/agents/{s.Running}/log", [">Deliberation</a>", "Details</summary>", "Warning"]);
        yield return ($"/keys/exchange/{s.Key}", ["Reading: yes", "Stop the 1 running or paused agent"]);
        yield return ("/keys", ["revoked", "Add key…", "Manage"]);
        yield return ("/providers", ["Sync now", "401 Unauthorized", "Claude catalog", "test-model"]);
        yield return ($"/providers/{ProviderNames.Route(AiProvider.Claude)}", ["Risk Manager", "Running BTC"]);
        yield return ("/settings", ["Reduce animation", "Risk disclaimer"]);
    }

    /// <summary>
    /// Everything through the context and the seed helpers, never through the host's key service (its exchange and provider
    /// clients are real). The 300 candles matter: with fewer in the store the chart's reader would ask the exchange.
    /// </summary>
    private async Task<Seeded> SeedAsync(Guid userId)
    {
        var keyId = Guid.CreateVersion7();
        await using (var db = _fixture.CreateContext())
        {
            db.Add(new ExchangeKey
            {
                Id = keyId, UserId = userId, Label = "testnet", Environment = ExchangeEnvironment.Production, AllowSpot = true, AllowFutures = true,
                ApiKey = AgentsLiveSeed.ExchangeApiKey, ApiSecret = AgentsLiveSeed.ExchangeSecret,
                PermissionsSnapshot = JsonSerializer.SerializeToElement(new ApiKeyPermissions(true, true, true, true, false, true, Now), JsonSerializerOptions.Web),
                Status = KeyStatus.Valid, LastValidatedAt = Now, CreatedAt = Now,
            });
            db.Add(new ProviderSyncState { Provider = AiProvider.Claude, LastSyncAt = Now, LastStatus = SyncStatus.Succeeded, ModelCount = 2, Trigger = SyncTrigger.KeyValidation });
            db.Add(new ProviderSyncState { Provider = AiProvider.Gemini, LastSyncAt = Now, LastStatus = SyncStatus.Failed, LastError = "401 Unauthorized", Trigger = SyncTrigger.Scheduled });
            db.AddRange(Enumerable.Range(1, 300).Select(i => LiveSeed.NewCandle(Now.AddHours(-i))));
            await db.SaveChangesAsync(Ct);
        }

        await ModelSyncLiveSeed.NewKeyAsync(_fixture, userId, AiProvider.Claude, ModelSyncLiveSeed.ApiKey, KeyStatus.Valid);
        await ModelSyncLiveSeed.NewKeyAsync(_fixture, userId, AiProvider.Gemini, ModelSyncLiveSeed.ApiKey, KeyStatus.Invalid);
        // The agents' Risk Manager defaults to Claude on "test-model": deprecated, so the warning, the row prompt and the list marker render.
        await ExperienceLiveSeed.AddCatalogModelsAsync(_fixture, Now, (AiProvider.Claude, "test-model", ModelStatus.Deprecated), (AiProvider.Claude, "claude-new", ModelStatus.Available), (AiProvider.Gpt, "gpt-test", ModelStatus.Available));

        var running = await AgentsLiveSeed.NewAgentAsync(_fixture, userId, "Running BTC", AgentState.Running, AgentMode.Paper, MarketType.UsdFutures);
        var draft = await AgentsLiveSeed.NewAgentAsync(_fixture, userId, "Draft BTC", AgentState.Draft, AgentMode.Paper, MarketType.UsdFutures);
        var paused = await AgentsLiveSeed.NewAgentAsync(_fixture, userId, "Paused live", AgentState.Paused, AgentMode.Live, MarketType.UsdFutures, exchangeKeyId: keyId);

        Decision decided;
        Guid decidedEvaluation;
        await using (var db = _fixture.CreateContext())
        {
            var agent = await db.Agents.SingleAsync(a => a.Id == running.Id, Ct);
            var evaluation = LiveSeed.NewEvaluation(agent, Now.AddHours(-2));
            decided = LiveSeed.NewDecision(evaluation, Now.AddHours(-1));
            decided.ExecutionCheck = LiveSeed.Json("""[{"limit":"minNotional","status":"pass","detail":"12.5 USDT over 5"}]""");
            decided.Transcripts.First().Output = LiveSeed.Json("""{"attempts":1,"document":{"assessment":"Constructive: the pullback held the EMA20.","rationale":"Daily bias long, hourly setup valid.","flags":["low volume"]}}""");
            db.AddRange(evaluation, decided);
            await db.SaveChangesAsync(Ct);
            decidedEvaluation = evaluation.Id;
        }

        var second = await ExperienceLiveSeed.NewDecisionAsync(_fixture, running, Now.AddHours(-5), DecisionAction.OpenLong,
            [ExperienceLiveSeed.Turn(CouncilRole.MarketAnalyst, AiProvider.Gpt, "gpt-test", DecisionAction.OpenLong), ExperienceLiveSeed.Turn(CouncilRole.RiskManager, AiProvider.Claude, "test-model", DecisionAction.OpenLong)]);
        await ExperienceLiveSeed.NewDecisionAsync(_fixture, running, Now.AddHours(-8), DecisionAction.NoTrade,
            [ExperienceLiveSeed.Turn(CouncilRole.RiskManager, AiProvider.Claude, "test-model", null, RoleOutputStatus.Failed)], limits: null);
        // In flight: the dashboard, the header and the decisions table show the evaluating indicator inside their live regions.
        var inFlight = await ExecutionLiveSeed.NewEvaluationAsync(_fixture, running, Now.AddHours(-1));

        await AgentsLiveSeed.NewOpenTradeAsync(_fixture, running, decided);
        await ExperienceLiveSeed.NewClosedTradeAsync(_fixture, running, second, 100m, 95m, 110m, 0.1m, 0.2m, Now.AddHours(-3));
        await ExperienceLiveSeed.NewShadowAsync(_fixture, second, Now.AddHours(-5), ShadowResult.HitTarget, r: 2m);

        var metrics = new ExperienceMetrics { Decisions = 3, Approvals = 2, Trades = 1, Wins = 1, SumR = 2m, GrossProfitR = 2m, NetTrades = 1, SumNetR = 1.9m, TotalLatencyMs = 3000 };
        await ExperienceLiveSeed.AddStatAsync(_fixture, StatScope.Agent, userId, running.Id, AiProvider.Claude, "test-model", CouncilRole.RiskManager, CandleInterval.OneHour, MarketType.UsdFutures, metrics, Now);
        await ExperienceLiveSeed.AddStatAsync(_fixture, StatScope.User, userId, null, AiProvider.Claude, "test-model", CouncilRole.RiskManager, CandleInterval.OneHour, MarketType.UsdFutures, metrics, Now);
        await ExperienceLiveSeed.AddStatAsync(_fixture, StatScope.System, null, null, AiProvider.Claude, "test-model", CouncilRole.RiskManager, CandleInterval.OneHour, MarketType.UsdFutures, metrics, Now);

        await using (var db = _fixture.CreateContext())
        {
            db.Add(LiveSeed.NewLogEntry(Now, userId, running.Id));
            db.Add(new LogEntry
            {
                Timestamp = Now.AddMinutes(-1), Level = LogLevel.Warning, Component = "Ecnanib.Agents.Services.AgentScheduler", Message = "The evaluation overran its deadline.",
                AgentId = running.Id, DecisionId = decided.Id, Exception = "System.TimeoutException: the council did not answer in time",
            });
            db.Add(new AgentChange { AgentId = running.Id, Timestamp = Now, Field = "Name", OldValue = LiveSeed.Json("\"old\""), NewValue = LiveSeed.Json("\"Running BTC\"") });
            foreach (var agentId in new[] { running.Id, draft.Id })
                db.Add(new EntryWindow { AgentId = agentId, Kind = EntryWindowKind.Recurring, StartDay = DayOfWeek.Saturday, StartTime = new TimeOnly(0, 0), EndDay = DayOfWeek.Sunday, EndTime = new TimeOnly(23, 59) });
            await db.SaveChangesAsync(Ct);
        }

        return new Seeded(keyId, running.Id, draft.Id, paused.Id, decidedEvaluation, inFlight.Id);
    }

    private sealed record Seeded(Guid Key, Guid Running, Guid Draft, Guid Paused, Guid DecidedEvaluation, Guid InFlightEvaluation);
}
