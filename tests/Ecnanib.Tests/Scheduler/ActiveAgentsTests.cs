using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Scheduler;

/// <summary>FR-CYC-01: the scheduler runs every user's Active agents of a timeframe, and no others.</summary>
public sealed class ActiveAgentsTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Only_active_agents_of_the_timeframe_are_listed()
    {
        await using var app = new AgentApp(postgres);
        var first = await app.UserAsync();
        var second = await app.UserAsync();

        var hourly = await app.ActiveAsync(first);
        var quarterly = await ActivateAsync(app, first, AgentApp.Ready(first, "Quarterly", Market.Futures) with { Timeframe = Timeframe.FifteenMinutes });
        var otherUsers = await app.ActiveAsync(second);
        var paused = await app.ActiveAsync(second, "Paused", Market.Futures);
        Assert.Equal(TransitionOutcome.Done, (await app.Agents.PauseAsync(second.Id, paused.Id, AgentApp.Cancellation)).Outcome);
        await app.CreateAsync(first.Id, AgentApp.Ready(first, "Draft"));

        var listed = app.App.Services.GetRequiredService<IActiveAgents>();

        Assert.Equal(
            new[] { new ActiveAgent(hourly.Id, first.Id), new ActiveAgent(otherUsers.Id, second.Id) }.OrderBy(agent => agent.Id),
            (await listed.ListAsync(Timeframe.OneHour, AgentApp.Cancellation)).OrderBy(agent => agent.Id));
        Assert.Equal(
            [new ActiveAgent(quarterly.Id, first.Id)],
            await listed.ListAsync(Timeframe.FifteenMinutes, AgentApp.Cancellation));
        Assert.Empty(await listed.ListAsync(Timeframe.OneDay, AgentApp.Cancellation));
    }

    private static async Task<AgentRecord> ActivateAsync(AgentApp app, AgentApp.User user, AgentSettings settings)
    {
        var agent = await app.CreateAsync(user.Id, settings);
        var result = await app.Agents.ActivateAsync(user.Id, agent.Id, AgentApp.Cancellation);

        Assert.Equal(ActivationOutcome.Activated, result.Outcome);

        return result.Agent!;
    }
}
