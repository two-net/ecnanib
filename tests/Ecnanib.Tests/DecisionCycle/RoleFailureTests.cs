using System.Net;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Ai;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.DecisionCycle;

/// <summary>
/// FR-ROLE-06: a role that fails moves to its fallback model, an optional role that fails on both is skipped, and a
/// required one makes the cycle hold. UC-02's alternate flows A1 and A2.
/// </summary>
public sealed class RoleFailureTests(PostgresFixture postgres)
{
    /// <summary>Not retried by the adapter, so a test needn't wait for retries.</summary>
    private static HttpResponseMessage Failure() =>
        FakeAiProvider.Error(HttpStatusCode.BadRequest, "invalid_request_error", "The model can't take this request.");

    /// <summary>UC-02 A1: the Sentiment Analyst fails on its model and on its fallback, and the cycle goes on without it.</summary>
    [Fact]
    public async Task An_optional_role_that_fails_on_both_models_is_skipped()
    {
        await using var app = new CycleApp(postgres);
        app.Replies[AiRole.SentimentAnalyst] = _ => Failure();
        var (user, agent) = await app.ActiveAgentAsync(WithSentimentFallingBackToSecond, AddSecondKeyAsync(app));

        await app.RunAsync(user, agent);

        Assert.Equal(["ReadyToExecute"], await app.CyclesAsync(agent.Id));
        Assert.Contains("SentimentAnalyst Skipped fallback", await app.RolesAsync(user.Id, agent.Id));

        var calls = await app.CallsAsync(agent.Id);
        Assert.Contains("SentimentAnalyst Test Failed InvalidRequest", calls);
        Assert.Contains("SentimentAnalyst Second Failed InvalidRequest fallback", calls);

        // Without its output, the Strategist sees only the Technical Analyst's.
        var message = Assert.Single(await app.CallColumnAsync(agent.Id, AiRole.Strategist, "message"));
        Assert.DoesNotContain("source=\"SentimentAnalyst\"", message, StringComparison.Ordinal);
        Assert.Contains("source=\"TechnicalAnalyst\"", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_role_whose_model_fails_answers_on_its_fallback()
    {
        await using var app = new CycleApp(postgres);
        app.Replies[AiRole.TechnicalAnalyst] = request =>
            request.Host == AiHarness.SecondHost ? CycleApp.Reply(Council.ContractSamples.Technical) : Failure();
        var (user, agent) = await app.ActiveAgentAsync(
            (user, settings) => settings with
            {
                Roles =
                [
                    .. settings.Roles.Where(role => role.Role != AiRole.TechnicalAnalyst),
                    new RoleAssignment(AiRole.TechnicalAnalyst, Model(AgentApp.Provider), Model(AgentApp.Second)),
                ],
            },
            AddSecondKeyAsync(app));

        await app.RunAsync(user, agent);

        Assert.Equal(["ReadyToExecute"], await app.CyclesAsync(agent.Id));
        Assert.Contains("TechnicalAnalyst Done fallback", await app.RolesAsync(user.Id, agent.Id));
    }

    /// <summary>UC-02 A2: the Strategist breaks its contract twice, so the cycle holds, and the trace has both violations.</summary>
    [Fact]
    public async Task A_strategist_that_breaks_its_contract_twice_makes_the_cycle_hold()
    {
        await using var app = new CycleApp(postgres);
        app.Replies[AiRole.Strategist] = _ => CycleApp.Reply("""{"action":"BUY"}""");
        var (user, agent) = await app.ActiveAgentAsync();

        await app.RunAsync(user, agent);

        Assert.Equal(["Hold RoleFailed"], await app.CyclesAsync(agent.Id));
        Assert.Equal(["TechnicalAnalyst Done", "Strategist Failed", "RiskCritic Skipped"], await app.RolesAsync(user.Id, agent.Id));
        Assert.Equal(["1", "2"], await app.CallColumnAsync(agent.Id, AiRole.Strategist, "attempt"));
        Assert.Equal(["BrokeContract", "BrokeContract"], await app.CallColumnAsync(agent.Id, AiRole.Strategist, "status"));
        Assert.All(
            await app.CallColumnAsync(agent.Id, AiRole.Strategist, "violations"),
            violations => Assert.Contains("$", violations, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_technical_analyst_that_fails_makes_the_cycle_hold()
    {
        await using var app = new CycleApp(postgres);
        app.Replies[AiRole.TechnicalAnalyst] = _ => Failure();
        var (user, agent) = await app.ActiveAgentAsync();

        await app.RunAsync(user, agent);

        Assert.Equal(["Hold RoleFailed"], await app.CyclesAsync(agent.Id));
        Assert.Equal(["TechnicalAnalyst Failed", "Strategist Skipped", "RiskCritic Skipped"], await app.RolesAsync(user.Id, agent.Id));
    }

    /// <summary>The Risk Critic is required for a trade action, so a failure there holds rather than trading unchecked.</summary>
    [Fact]
    public async Task A_risk_critic_that_fails_makes_the_cycle_hold()
    {
        await using var app = new CycleApp(postgres);
        app.Replies[AiRole.RiskCritic] = _ => Failure();
        var (user, agent) = await app.ActiveAgentAsync();

        await app.RunAsync(user, agent);

        Assert.Equal(["Hold RoleFailed"], await app.CyclesAsync(agent.Id));
        Assert.Null(await app.EvaluationAsync(agent.Id));
    }

    private static ModelRef Model(string provider) => new(provider, AgentApp.Model);

    private static AgentSettings WithSentimentFallingBackToSecond(AgentApp.User user, AgentSettings settings) => settings with
    {
        Roles = [.. settings.Roles, new RoleAssignment(AiRole.SentimentAnalyst, Model(AgentApp.Provider), Model(AgentApp.Second))],
    };

    private static Func<AgentApp.User, Task> AddSecondKeyAsync(CycleApp app) =>
        user => app.Agents.AddAiAsync(user.Id, AgentApp.Second);
}
