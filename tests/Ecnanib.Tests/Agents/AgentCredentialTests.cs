using System.Net;
using System.Net.Http;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Ai;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Persistence;
using Market = Ecnanib.Kernel.Market;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// FR-KEY-10 and FR-KEY-11: deleting a key that agents use pauses them, and so does a key that stops
/// working when it's checked again, on a schedule or on demand. An outage pauses nothing.
/// </summary>
public sealed class AgentCredentialTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => AgentApp.Cancellation;

    [Fact]
    public async Task Deleting_an_exchange_key_says_which_agents_use_it_and_pauses_the_active_ones()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var active = await app.ActiveAsync(user, "Active");
        var draft = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Draft"));

        var users = await app.Agents.ListUsingCredentialAsync(user.Id, user.ExchangeKey, Cancellation);

        Assert.Equal(
            [new AgentUse(active.Id, "Active", AgentStatus.Active, true), new AgentUse(draft.Id, "Draft", AgentStatus.Draft, false)],
            users);

        var checks = await app.FinishedAsync("agent-credential-check");
        Assert.True(await app.Credentials.DeleteAsync(user.Id, user.ExchangeKey, Cancellation));
        await app.UntilCredentialCheckAsync(checks);

        var paused = (await app.Agents.FindAsync(user.Id, active.Id, Cancellation))!;
        Assert.Equal(AgentStatus.Paused, paused.Status);
        Assert.Equal(AgentStatusReason.CredentialDeleted, paused.StatusReason);
        Assert.Equal(AgentStatus.Draft, await app.StatusAsync(user.Id, draft.Id));

        var record = (await app.AuditAsync(user.Id)).Last(row => row.Action == "AgentPaused");
        Assert.Equal("System", record.ActorKind);
        Assert.Null(record.ActorId);
        Assert.Equal(active.Id.ToString(), record.TargetId);
        Assert.Contains("CredentialDeleted", record.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_AI_key_matters_only_while_its_the_users_one_valid_key_for_the_provider()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user);

        Assert.Equal([agent.Id], (await app.Agents.ListUsingCredentialAsync(user.Id, user.AiKey, Cancellation)).Select(use => use.Id));

        var second = await app.AddAiAsync(user.Id, key: CredentialApp.OtherAiKey);

        Assert.Empty(await app.Agents.ListUsingCredentialAsync(user.Id, user.AiKey, Cancellation));

        var checks = await app.FinishedAsync("agent-credential-check");
        await app.Credentials.DeleteAsync(user.Id, user.AiKey, Cancellation);
        await app.UntilCredentialCheckAsync(checks);

        Assert.Equal(AgentStatus.Active, await app.StatusAsync(user.Id, agent.Id));

        checks = await app.FinishedAsync("agent-credential-check");
        await app.Credentials.DeleteAsync(user.Id, second, Cancellation);
        await app.UntilCredentialCheckAsync(checks);

        var paused = (await app.Agents.FindAsync(user.Id, agent.Id, Cancellation))!;
        Assert.Equal(AgentStatus.Paused, paused.Status);
        Assert.Equal(AgentStatusReason.AiKeyUnavailable, paused.StatusReason);
    }

    [Fact]
    public async Task Checking_keys_again_on_schedule_pauses_the_agents_of_a_key_that_stopped_working_and_says_so()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user);
        app.Binance.On(HttpMethod.Get, CredentialApp.SpotAccountPath, _ => CredentialApp.Refusal());
        app.Binance.On(HttpMethod.Get, CredentialApp.FuturesAccountPath, _ => CredentialApp.Refusal());
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        var checks = await app.FinishedAsync("agent-credential-check");

        Assert.Equal(JobStatus.Succeeded, await app.RunJobAsync("CredentialRevalidationJob"));
        await app.UntilCredentialCheckAsync(checks);

        Assert.Equal(CredentialStatus.Invalid, (await app.Credentials.FindAsync(user.Id, user.ExchangeKey, Cancellation))!.Status);

        var paused = (await app.Agents.FindAsync(user.Id, agent.Id, Cancellation))!;
        Assert.Equal(AgentStatus.Paused, paused.Status);
        Assert.Equal(AgentStatusReason.CredentialInvalid, paused.StatusReason);

        var validated = (await app.AuditAsync(user.Id)).Single(row => row.Action == "CredentialValidated" && row.TargetId == user.ExchangeKey.ToString());
        Assert.Equal("System", validated.ActorKind);

        Assert.Equal(["CredentialInvalid"], await NotificationsAsync(app, user.Id));
        Assert.Equal(
            "/credentials",
            await Sql.ScalarAsync<string>(app.ConnectionString, $"SELECT link FROM notifications WHERE user_id = '{user.Id}'", Cancellation));
    }

    [Fact]
    public async Task A_key_that_lost_a_market_pauses_only_the_agents_on_that_market()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var spot = await app.ActiveAsync(user, "Spot");
        var futures = await app.ActiveAsync(user, "Futures", Market.Futures);
        app.BinanceAccepts(spot: true, futures: false);
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        var checks = await app.FinishedAsync("agent-credential-check");

        var result = await app.Credentials.ValidateAsync(user.Id, user.ExchangeKey, Cancellation);
        await app.UntilCredentialCheckAsync(checks);

        Assert.Equal(CredentialStatus.Valid, result.Credential!.Status);
        Assert.False(result.Credential.FuturesEnabled);
        Assert.Equal(AgentStatus.Active, await app.StatusAsync(user.Id, spot.Id));

        var paused = (await app.Agents.FindAsync(user.Id, futures.Id, Cancellation))!;
        Assert.Equal(AgentStatus.Paused, paused.Status);
        Assert.Equal(AgentStatusReason.MarketNotEnabled, paused.StatusReason);
        Assert.Equal(["CredentialInvalid"], await NotificationsAsync(app, user.Id));
    }

    [Fact]
    public async Task An_AI_key_that_stopped_working_pauses_the_agents_with_a_role_on_its_provider()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user);
        app.Ai.On(HttpMethod.Get, CredentialApp.ModelsPath, _ => FakeAiProvider.Error(HttpStatusCode.Unauthorized, "authentication_error", "invalid key"));
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        var checks = await app.FinishedAsync("agent-credential-check");

        await app.RunJobAsync("CredentialRevalidationJob");
        await app.UntilCredentialCheckAsync(checks);

        var paused = (await app.Agents.FindAsync(user.Id, agent.Id, Cancellation))!;
        Assert.Equal(AgentStatus.Paused, paused.Status);
        Assert.Equal(AgentStatusReason.AiKeyUnavailable, paused.StatusReason);
    }

    [Fact]
    public async Task An_outage_changes_no_key_and_pauses_no_agent()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user);
        app.Binance.On(
            HttpMethod.Get, CredentialApp.SpotAccountPath, _ => FakeBinance.Error(HttpStatusCode.ServiceUnavailable, -1001, "Internal error."));
        app.Binance.On(
            HttpMethod.Get, CredentialApp.FuturesAccountPath, _ => FakeBinance.Error(HttpStatusCode.ServiceUnavailable, -1001, "Internal error."));
        app.Ai.On(HttpMethod.Get, CredentialApp.ModelsPath, _ => FakeAiProvider.Error(HttpStatusCode.ServiceUnavailable, "overloaded_error", "overloaded"));
        app.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(JobStatus.Succeeded, await app.RunJobAsync("CredentialRevalidationJob"));
        await app.RunJobAsync("AgentPrerequisitesJob");

        Assert.Equal(CredentialStatus.Valid, (await app.Credentials.FindAsync(user.Id, user.ExchangeKey, Cancellation))!.Status);
        Assert.Equal(CredentialStatus.Valid, (await app.Credentials.FindAsync(user.Id, user.AiKey, Cancellation))!.Status);
        Assert.Equal(AgentStatus.Active, await app.StatusAsync(user.Id, agent.Id));
        Assert.Empty(await NotificationsAsync(app, user.Id));
    }

    /// <summary>An in-process event is lost with the process, and the hourly check is the recovery path.</summary>
    [Fact]
    public async Task The_hourly_check_pauses_agents_whose_key_stopped_working_without_anyone_hearing()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var other = await app.UserAsync();
        var agent = await app.ActiveAsync(user);
        var unaffected = await app.ActiveAsync(other);
        await Sql.ExecuteAsync(app.ConnectionString, $"UPDATE credentials SET status = 'Invalid' WHERE id = '{user.ExchangeKey}'", Cancellation);

        Assert.Equal(AgentStatus.Active, await app.StatusAsync(user.Id, agent.Id));
        Assert.Equal(JobStatus.Succeeded, await app.RunJobAsync("AgentPrerequisitesJob"));

        Assert.Equal(AgentStatus.Paused, await app.StatusAsync(user.Id, agent.Id));
        Assert.Equal(AgentStatus.Active, await app.StatusAsync(other.Id, unaffected.Id));
        Assert.Equal(
            "Users 2, paused 1, failed 0",
            await Sql.ScalarAsync<string>(
                app.ConnectionString, "SELECT details FROM job_runs WHERE job_name LIKE 'test-AgentPrerequisitesJob-%'", Cancellation));
    }

    private static Task<List<string>> NotificationsAsync(AgentApp app, Guid userId) =>
        Sql.ColumnAsync(app.ConnectionString, $"SELECT type FROM notifications WHERE user_id = '{userId}' ORDER BY id", Cancellation);
}
