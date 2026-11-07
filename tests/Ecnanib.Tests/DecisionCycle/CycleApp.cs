using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Ai;
using Ecnanib.Tests.Council;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.DecisionCycle;

/// <summary>
/// <see cref="AgentApp"/> with what a decision cycle needs: Binance's candles and prices, and a model on both
/// providers that answers each role from <see cref="Replies"/>, or with a conforming answer that buys at market and
/// passes the Risk Engine. Each test runs its cycles itself, through <see cref="IDecisionCycles"/>.
/// </summary>
internal sealed class CycleApp : IAsyncDisposable
{
    /// <summary>The sample ticker's ask, which a market buy enters at.</summary>
    public const decimal Ask = 60123.46m;

    /// <summary>
    /// A Spot market buy with a stop below the ask and two targets above it, for 1% of the capital. The sample candles
    /// move by about 3 an hour, so the stop is a little under 2 ATR away and the nearer target over 2R.
    /// </summary>
    public const string Buy = """
        {"action":"BUY","side":"long","entry_type":"market","entry_price":null,"stop_price":60118,
         "target_prices":[60150,60135],"risk_percent":1,"limit_ttl_candles":null,"close_fraction":null,
         "invalidation":"A close below 60118.","rationale":"Momentum above the 20 EMA.","confidence":0.6}
        """;

    public const string Hold = """
        {"action":"HOLD","side":null,"entry_type":null,"entry_price":null,"stop_price":null,"target_prices":[],
         "risk_percent":null,"limit_ttl_candles":null,"close_fraction":null,"invalidation":null,
         "rationale":"No setup.","confidence":0.4}
        """;

    public const string Approve = """
        {"verdict":"approve","modifications":null,"concerns":[],
         "checklist":{"trend_alignment":{"result":"pass","note":"Aligned."},"reward_to_risk":{"result":"pass","note":"2R."},
          "event_risk":{"result":"pass","note":"None."},"funding":{"result":"not_applicable","note":"Spot."},
          "reflections":{"result":"not_applicable","note":"None yet."}},
         "rationale":"Sound."}
        """;

    /// <param name="manualJobs">Other jobs a test runs itself, by class name, besides <c>ApprovalExpiryJob</c>.</param>
    public CycleApp(PostgresFixture postgres, Dictionary<string, string?>? settings = null, IReadOnlyCollection<string>? manualJobs = null)
    {
        Agents = new AgentApp(
            postgres,
            settings,
            services: services => services.AddSingleton<ICycleObserver>(Observer),
            manualJobs: ["ApprovalExpiryJob", .. manualJobs ?? []]);

        foreach (var path in new[] { "/api/v3/klines", "/fapi/v1/klines" })
        {
            Agents.Binance.On(HttpMethod.Get, path, request => FakeBinance.Json(BinanceSamples.Klines(request, Clock.GetUtcNow(), Missing)));
        }

        Agents.Binance.On(HttpMethod.Get, "/api/v3/ticker/24hr", _ => FakeBinance.Json(BinanceSamples.SpotTicker24h));
        Agents.Binance.On(HttpMethod.Get, "/fapi/v1/ticker/bookTicker", _ => FakeBinance.Json(BinanceSamples.FuturesBookTicker));
        Agents.Binance.On(HttpMethod.Get, "/fapi/v1/ticker/price", _ => FakeBinance.Json(BinanceSamples.FuturesPrice));
        Agents.Binance.On(HttpMethod.Get, "/fapi/v1/premiumIndex", _ => FakeBinance.Json(BinanceSamples.PremiumIndex()));
        Agents.Binance.On(HttpMethod.Get, "/fapi/v1/fundingInfo", _ => FakeBinance.Json(BinanceSamples.FundingInfo));
        Agents.Binance.On(HttpMethod.Get, "/fapi/v1/leverageBracket", _ => FakeBinance.Json(BinanceSamples.LeverageBrackets()));
        Agents.Ai.On(HttpMethod.Post, "/v1/complete", Answer);
    }

    public static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public AgentApp Agents { get; }

    public TestClock Clock => Agents.Clock;

    public string ConnectionString => Agents.ConnectionString;

    public RecordingObserver Observer { get; } = new();

    /// <summary>A test's own answer for a role, which may look at the request, such as its host for the provider.</summary>
    public ConcurrentDictionary<AiRole, Func<RecordedAiRequest, HttpResponseMessage>> Replies { get; } = new();

    /// <summary>Candles Binance doesn't have, as during maintenance.</summary>
    public Func<DateTimeOffset, bool>? Missing { get; set; }

    public IDecisionTraces Traces => Agents.App.Services.GetRequiredService<IDecisionTraces>();

    public IApprovals Approvals => Agents.App.Services.GetRequiredService<IApprovals>();

    /// <summary>The user's one pending approval, which a Confirm agent's cycle left.</summary>
    public async Task<ApprovalRecord> PendingAsync(AgentApp.User user) =>
        Assert.Single((await Approvals.ListAsync(user.Id, Cancellation)).Pending);

    /// <summary>The close of the last hourly candle, which a cycle now would decide on.</summary>
    public DateTimeOffset LastClose => BinanceSamples.Floor("1h", Clock.GetUtcNow());

    public static HttpResponseMessage Reply(string json) => FakeAiProvider.Completion(json, 1000, 200);

    /// <summary>A user and an Active Spot BTCUSDT 1H agent, with any change to the settings made first.</summary>
    /// <param name="prepare">Runs for the user before the agent is created, such as to add another provider's key.</param>
    public async Task<(AgentApp.User User, AgentRecord Agent)> ActiveAgentAsync(
        Func<AgentApp.User, AgentSettings, AgentSettings>? change = null,
        Func<AgentApp.User, Task>? prepare = null)
    {
        var user = await Agents.UserAsync();

        if (prepare is not null)
        {
            await prepare(user);
        }

        var settings = AgentApp.Ready(user);
        var agent = await Agents.CreateAsync(user.Id, change is null ? settings : change(user, settings));
        var result = await Agents.Agents.ActivateAsync(user.Id, agent.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Activated, result.Outcome);

        return (user, result.Agent!);
    }

    /// <summary>Runs the agent's cycle for <paramref name="close"/>, the last close unless given, as the scheduler would.</summary>
    public Task RunAsync(AgentApp.User user, AgentRecord agent, DateTimeOffset? close = null, DateTimeOffset? deadline = null) =>
        Agents.App.Services.GetRequiredService<IDecisionCycles>().RunAsync(
            new CycleRequest(
                user.Id,
                agent.Id,
                agent.Settings.Timeframe,
                close ?? LastClose,
                deadline ?? Clock.GetUtcNow() + TimeSpan.FromMinutes(10),
                CatchUp: false),
            new JobContext(CancellationToken.None),
            Cancellation);

    /// <summary>The agent's cycles, newest first, as outcome and failure.</summary>
    public Task<List<string>> CyclesAsync(Guid agentId) =>
        Sql.ColumnAsync(
            ConnectionString,
            $"SELECT concat_ws(' ', coalesce(outcome, 'Running'), failure) FROM decision_cycles WHERE agent_id = '{agentId}' ORDER BY candle_close DESC",
            Cancellation);

    /// <summary>Each AI call of the agent's cycles, in order, as role, provider, status, failure and fallback.</summary>
    public Task<List<string>> CallsAsync(Guid agentId) =>
        Sql.ColumnAsync(
            ConnectionString,
            $"""
            SELECT concat_ws(' ', c.role, c.provider, c.status, c.failure, CASE WHEN c.fallback THEN 'fallback' END)
            FROM ai_calls c JOIN decision_cycles d ON d.id = c.cycle_id
            WHERE d.agent_id = '{agentId}' ORDER BY c.id
            """,
            Cancellation);

    /// <summary>A column of the agent's AI calls for <paramref name="role"/>, in order.</summary>
    public Task<List<string>> CallColumnAsync(Guid agentId, AiRole role, string column) =>
        Sql.ColumnAsync(
            ConnectionString,
            $"""
            SELECT coalesce(c.{column}::text, '') FROM ai_calls c JOIN decision_cycles d ON d.id = c.cycle_id
            WHERE d.agent_id = '{agentId}' AND c.role = '{role}' ORDER BY c.id
            """,
            Cancellation);

    /// <summary>The agent's roles in its latest cycle, as role, state and whether the fallback answered.</summary>
    public async Task<List<string>> RolesAsync(Guid userId, Guid agentId)
    {
        var latest = await Traces.FindLatestAsync(userId, agentId, Cancellation);

        return [.. latest!.Roles.Select(role => $"{role.Role} {role.State}{(role.UsedFallback ? " fallback" : "")}")];
    }

    /// <summary>The agent's latest Risk Engine evaluation, as JSON with its verdict, intent, rules and sized order.</summary>
    public async Task<JsonElement?> EvaluationAsync(Guid agentId)
    {
        var rows = await Sql.ColumnAsync(
            ConnectionString,
            $"""
            SELECT json_build_object('verdict', r.verdict, 'intent', r.intent, 'rules', r.rules, 'sizedOrder', r.sized_order)::text
            FROM risk_evaluations r JOIN decision_cycles d ON d.id = r.cycle_id
            WHERE d.agent_id = '{agentId}' ORDER BY r.id DESC LIMIT 1
            """,
            Cancellation);

        return rows.Count == 0 ? null : JsonDocument.Parse(rows[0]).RootElement.Clone();
    }

    public ValueTask DisposeAsync() => Agents.DisposeAsync();

    /// <summary>A test's answer for the role the request is for, or the default, which buys and is approved.</summary>
    private HttpResponseMessage Answer(RecordedAiRequest request)
    {
        using var body = JsonDocument.Parse(request.Body!);
        var role = Enum.Parse<AiRole>(body.RootElement.GetProperty("role").GetString()!);

        if (Replies.TryGetValue(role, out var reply))
        {
            return reply(request);
        }

        return Reply(role switch
        {
            AiRole.TechnicalAnalyst => ContractSamples.Technical,
            AiRole.SentimentAnalyst => ContractSamples.Sentiment,
            AiRole.Strategist => Buy,
            _ => Approve,
        });
    }
}

/// <summary>Every change a cycle published, in order.</summary>
internal sealed class RecordingObserver : ICycleObserver
{
    private readonly ConcurrentQueue<CycleProgress> _changes = new();

    public IReadOnlyList<CycleProgress> Changes => [.. _changes];

    public void OnChanged(CycleProgress progress) => _changes.Enqueue(progress);

    public Task UntilAsync(Func<IReadOnlyList<CycleProgress>, bool> condition) =>
        LoggedApp.UntilAsync(() => ValueTask.FromResult(condition(Changes)));
}
