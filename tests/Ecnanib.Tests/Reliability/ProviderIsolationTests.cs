using System.Net;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.AiProviderGateway;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.DecisionEngine;
using Ecnanib.Tests.Logging;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Reliability;

/// <summary>
/// An outage at one AI provider affects only the cycles that need it (NFR-REL-4). The cycles of
/// the agents run each on its own, through the gateway itself here, with the stored keys of
/// their users: a provider that is down, or that hangs, ends the cycles whose teams call it with
/// no decision, and the others decide as they would have. Once its calls are paused, the cycles
/// that need it no longer wait for it either. See "One provider's outage" in docs/reliability.md.
/// </summary>
public class ProviderIsolationTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Close = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
    /// <summary>
    /// The time between two closes of a test. The scheduler of the application starts the job
    /// by itself when the clock passes one quarter hour, beside the run of the test, and skips
    /// a quarter hour that it finds passed together with the one after it: so a test that runs
    /// the job itself moves its clock by two quarter hours, once the scheduler works.
    /// </summary>
    private static readonly TimeSpan Half = TimeSpan.FromMinutes(30);

    /// <summary>A team as a new agent has it: each role at a provider of its own, with the Market Analyst at GPT.</summary>
    private static readonly IReadOnlyDictionary<Role, string> WithGpt = new Dictionary<Role, string>
    {
        [Role.MarketAnalyst] = "gpt",
        [Role.SentimentAnalyst] = "grok",
        [Role.Strategist] = "gemini",
        [Role.RiskManager] = "claude",
    };

    /// <summary>A team that calls GPT for nothing: Claude holds two of its roles.</summary>
    private static readonly IReadOnlyDictionary<Role, string> WithoutGpt = new Dictionary<Role, string>
    {
        [Role.MarketAnalyst] = "claude",
        [Role.SentimentAnalyst] = "grok",
        [Role.Strategist] = "gemini",
        [Role.RiskManager] = "claude",
    };

    [Fact]
    public async Task While_a_provider_is_down_the_cycles_that_need_it_end_with_no_decision_and_the_others_decide()
    {
        // GPT answers nobody, close after close. Every other provider answers.
        var providers = new StandInSystem(async (request, cancellation) => ModelCalls.ProviderOf(request) == "gpt"
            ? StandInSystem.Status(HttpStatusCode.ServiceUnavailable)
            : await AnsweredAsync(request, cancellation));

        using var application = new CyclesApplication(Close, providers: providers);
        var (_, adas) = await RunningAsync(application, Ada, WithGpt);
        var (_, graces) = await RunningAsync(application, Grace, WithoutGpt, symbol: "SOLUSDT");
        await application.SchedulerStartedAsync();

        foreach (var close in new[] { Close, Close + Half, Close + (2 * Half) })
        {
            application.Clock.Set(close);
            await application.RunAsync(close).Within();
        }

        // The agent whose Market Analyst is called at GPT decided nothing, and entered nothing.
        Assert.Equal(
            [CycleOutcome.NoDecision, CycleOutcome.NoDecision, CycleOutcome.NoDecision],
            (await CyclesOfAsync(application, adas)).Select(cycle => cycle.Outcome));
        Assert.Empty(await OrdersOfAsync(application, adas));

        // The agent whose team does not call GPT decided at every close, as it would have.
        Assert.Equal(
            [CycleOutcome.Approved, CycleOutcome.Approved, CycleOutcome.Approved],
            (await CyclesOfAsync(application, graces)).Select(cycle => cycle.Outcome));
        Assert.All(
            await CallsOfAsync(application, graces),
            call => Assert.Equal((RoleCallOutcome.Succeeded, RoleValidation.Valid), (call.Outcome, call.Validation)));

        // No cycle failed for it: a provider that does not answer is no fault of the service.
        Assert.All(
            new[] { Close, Close + Half, Close + (2 * Half) },
            close => Assert.Equal(0, application.SummaryOf(close)["Failed"].GetInt32()));
    }

    [Fact]
    public async Task A_provider_that_hangs_holds_back_no_cycle_that_does_not_use_it()
    {
        // GPT takes the call and does not answer, until the test lets it.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providers = new StandInSystem(async (request, cancellation) =>
        {
            if (ModelCalls.ProviderOf(request) != "gpt")
            {
                return await AnsweredAsync(request, cancellation);
            }

            await held.Task.WaitAsync(cancellation);

            return StandInSystem.Status(HttpStatusCode.ServiceUnavailable);
        });

        using var application = new CyclesApplication(Close, providers: providers);
        var (_, adas) = await RunningAsync(application, Ada, WithGpt);
        var (_, graces) = await RunningAsync(application, Grace, WithoutGpt, symbol: "SOLUSDT");

        var run = application.RunAsync(Close);

        try
        {
            // The cycle that does not use GPT is over and recorded while the other still waits.
            await Wait.UntilAsync(
                () => CyclesOfAsync(application, graces).GetAwaiter().GetResult().Count == 1, "the cycle that does not call the provider");

            Assert.Equal(CycleOutcome.Approved, Assert.Single(await CyclesOfAsync(application, graces)).Outcome);
            Assert.Empty(await CyclesOfAsync(application, adas));
            Assert.False(run.IsCompleted);
        }
        finally
        {
            held.SetResult();
        }

        await run.Within();

        Assert.Equal(CycleOutcome.NoDecision, Assert.Single(await CyclesOfAsync(application, adas)).Outcome);
    }

    [Fact]
    public async Task While_the_calls_to_a_provider_are_paused_the_cycles_that_need_it_end_without_waiting_for_it()
    {
        // GPT fails the first call that reaches it, and would never answer another. One failure
        // pauses its calls for forty minutes, and one cycle runs at a time with each provider:
        // a cycle that waited for GPT would hold the other back for as long.
        var first = true;
        var providers = new StandInSystem(async (request, cancellation) =>
        {
            if (ModelCalls.ProviderOf(request) != "gpt")
            {
                return await AnsweredAsync(request, cancellation);
            }

            if (first)
            {
                first = false;

                return StandInSystem.Status(HttpStatusCode.ServiceUnavailable);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);

            throw new InvalidOperationException("The provider never answers.");
        });

        using var application = new CyclesApplication(
            Close,
            maxPerProvider: 1,
            providers: providers,
            settings: file => file
                .With("Breakers:AiProvider:Failures", 1)
                .With("Breakers:AiProvider:PauseSeconds", 2400)
                .With("Breakers:AiProvider:LongestPauseSeconds", 2400));
        var (ada, adas) = await RunningAsync(application, Ada, WithGpt);
        var (_, graces) = await RunningAsync(application, Grace, WithoutGpt, symbol: "SOLUSDT");
        await application.SchedulerStartedAsync();

        await application.RunAsync(Close).Within();

        // The next close is within the pause. Both cycles are over in time: the one that needs
        // GPT was not made to wait for it, and gave its places with the other providers back.
        application.Clock.Set(Close + Half);
        await application.RunAsync(Close + Half).Within();

        Assert.Equal(1, providers.Calls.Count(call => call.Address.Host == "gpt.example"));
        Assert.Equal(
            [CycleOutcome.NoDecision, CycleOutcome.NoDecision], (await CyclesOfAsync(application, adas)).Select(cycle => cycle.Outcome));
        Assert.Equal(
            [CycleOutcome.Approved, CycleOutcome.Approved], (await CyclesOfAsync(application, graces)).Select(cycle => cycle.Outcome));

        // The record says that the call was not made, and why. The Sentiment Analyst of the same
        // cycle was called all the same, at its own provider: the two analysts are called at
        // the same time, and neither waits to see how the other ends.
        Assert.Equal(
            [RoleCallOutcome.Failed, RoleCallOutcome.ProviderPaused],
            (await CallsOfAsync(application, adas)).Where(call => call.Role == Role.MarketAnalyst).Select(call => call.Outcome));
        Assert.Equal(
            2,
            providers.Calls.Count(call => call.Address.Host == "grok.example"
                && call.Headers.Values.Any(header => header.Contains(Keys.Of(ada.ToString("N"), "grok"), StringComparison.Ordinal))));
    }

    /// <summary>
    /// What a provider answers a role with: a result of the schema of the role, which the model
    /// of the call names, in the form of the provider, as one that searched where it was offered
    /// a search.
    /// </summary>
    private static async Task<HttpResponseMessage> AnsweredAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        var provider = ModelCalls.ProviderOf(request);
        var body = await request.Content!.ReadAsStringAsync(cancellation);
        var asked = request.RequestUri + body;
        var role = Enum.GetValues<Role>().Single(role => asked.Contains(ModelOf(provider, role), StringComparison.Ordinal));
        var offered = body.Contains("\"tools\"", StringComparison.Ordinal);

        return StandInSystem.Json(
            HttpStatusCode.OK,
            offered ? ModelCalls.Searched(provider, TeamAnswers.Of(role)) : ModelCalls.Finished(provider, TeamAnswers.Of(role)));
    }

    /// <summary>The model of a role at a provider, which says the role: a provider may hold two.</summary>
    private static string ModelOf(string provider, Role role) => $"{provider}-{role.ToString().ToLowerInvariant()}-model";

    /// <summary>A user with an agent that runs, whose team is called through the gateway with the stored keys of the user.</summary>
    private static async Task<(Guid UserId, Guid AgentId)> RunningAsync(
        CyclesApplication application, string email, IReadOnlyDictionary<Role, string> team, string symbol = "BTCUSDT")
    {
        var user = await application.Application.RegisterVerifiedAsync(email);
        var agent = await application.Application.GiveAgentAsync(
            user.Id,
            Close.AddHours(-1),
            state: AgentState.Running,
            symbol: symbol,
            cause: AgentStateCause.UserStarted,
            team: false,
            timeframe: Timeframe.FifteenMinutes);

        foreach (var (role, provider) in team)
        {
            await application.Application.GiveRoleAsync(user.Id, agent, role, Close.AddHours(-1), provider, ModelOf(provider, role));
        }

        return (user.Id, agent);
    }

    /// <summary>The recorded cycles of an agent, in the order of their closes.</summary>
    private static async Task<IReadOnlyList<DecisionCycle>> CyclesOfAsync(CyclesApplication application, Guid agentId)
    {
        await using var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        var cycles = await context.Set<DecisionCycle>()
            .AsNoTracking()
            .Where(cycle => cycle.AgentId == agentId)
            .ToListAsync(TestContext.Current.CancellationToken);

        return [.. cycles.OrderBy(cycle => cycle.ClosedAt)];
    }

    /// <summary>The orders of an agent, of whatever kind: an agent that entered nothing has none.</summary>
    private static async Task<IReadOnlyList<Order>> OrdersOfAsync(CyclesApplication application, Guid agentId)
    {
        await using var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        return await context.Set<Order>()
            .AsNoTracking()
            .Where(order => order.AgentId == agentId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The recorded calls of the roles of an agent, in the order they were made in.</summary>
    private static async Task<IReadOnlyList<RoleOutput>> CallsOfAsync(CyclesApplication application, Guid agentId)
    {
        await using var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        var calls = await (
            from output in context.Set<RoleOutput>().AsNoTracking()
            join cycle in context.Set<DecisionCycle>().AsNoTracking() on output.CycleId equals cycle.Id
            where cycle.AgentId == agentId
            select output)
            .ToListAsync(TestContext.Current.CancellationToken);

        return [.. calls.OrderBy(call => call.CalledAt).ThenBy(call => call.Attempt)];
    }
}
