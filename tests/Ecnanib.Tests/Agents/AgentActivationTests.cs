using System.Net.Http;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Persistence;
using Npgsql;

namespace Ecnanib.Tests.Agents;

/// <summary>FR-AGT-05, FR-AGT-10, FR-AGT-13, AC-13: what activating a Demo agent needs, and one agent per key, market and pair.</summary>
public sealed class AgentActivationTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => AgentApp.Cancellation;

    [Fact]
    public async Task A_ready_draft_becomes_active_and_the_activation_is_audited()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.CreateAsync(user.Id, AgentApp.Ready(user));

        var result = await app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Activated, result.Outcome);
        Assert.Empty(result.Problems!);
        Assert.Equal(AgentStatus.Active, result.Agent!.Status);
        Assert.Equal(AgentStatusReason.User, result.Agent.StatusReason);

        var record = (await app.AuditAsync(user.Id)).Last();
        Assert.Equal("AgentActivated", record.Action);
        Assert.Contains("Draft", record.Summary, StringComparison.Ordinal);
        Assert.Contains("Active", record.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Everything_a_draft_still_needs_is_named_at_once()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.CreateAsync(
            user.Id, AgentApp.Ready(user) with { ExchangeCredentialId = null, Roles = [], CapitalAllocation = null });

        var result = await app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Refused, result.Outcome);
        Assert.Equal(
            [
                new ActivationProblem(ActivationProblemKind.NoExchangeKey),
                new ActivationProblem(ActivationProblemKind.RoleUnassigned, AiRole.TechnicalAnalyst),
                new ActivationProblem(ActivationProblemKind.RoleUnassigned, AiRole.Strategist),
                new ActivationProblem(ActivationProblemKind.RoleUnassigned, AiRole.RiskCritic),
                new ActivationProblem(ActivationProblemKind.NoCapitalAllocation),
            ],
            result.Problems);
        Assert.Equal(AgentStatus.Draft, await app.StatusAsync(user.Id, agent.Id));
        Assert.DoesNotContain(await app.AuditAsync(user.Id), row => row.Action == "AgentActivated");
    }

    [Fact]
    public async Task The_exchange_key_must_be_valid_and_allowed_on_the_agents_market()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();

        app.BinanceAccepts(spot: true, futures: false);
        var spotOnly = await app.AddBinanceAsync(user.Id, "Spot only");
        var futures = await app.CreateAsync(user.Id, AgentApp.Ready(user, market: Market.Futures) with { ExchangeCredentialId = spotOnly });

        Assert.Equal(
            [new ActivationProblem(ActivationProblemKind.MarketNotEnabled)],
            (await app.Agents.ActivateAsync(user.Id, futures.Id, Cancellation)).Problems);

        // The key stops working.
        app.Binance.On(HttpMethod.Get, CredentialApp.SpotAccountPath, _ => CredentialApp.Refusal());
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        await app.Credentials.ValidateAsync(user.Id, spotOnly, Cancellation);
        var spot = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Spot") with { ExchangeCredentialId = spotOnly });

        Assert.Equal(
            [new ActivationProblem(ActivationProblemKind.ExchangeKeyNotValid)],
            (await app.Agents.ActivateAsync(user.Id, spot.Id, Cancellation)).Problems);

        // The key is deleted, which an agent that chose it can still be edited past.
        await app.Credentials.DeleteAsync(user.Id, spotOnly, Cancellation);

        Assert.Equal(
            [new ActivationProblem(ActivationProblemKind.ExchangeKeyMissing)],
            (await app.Agents.ActivateAsync(user.Id, spot.Id, Cancellation)).Problems);
        Assert.Equal(
            AgentSaveOutcome.Saved,
            (await app.Agents.UpdateAsync(user.Id, spot.Id, spot.Version, spot.Settings with { Name = "Renamed" }, Cancellation)).Outcome);
    }

    [Fact]
    public async Task Each_required_role_needs_an_active_model_one_of_the_users_valid_keys_can_use()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var model = new ModelRef(AgentApp.Provider, AgentApp.Model);
        var agent = await app.CreateAsync(
            user.Id,
            AgentApp.Ready(user) with
            {
                Roles =
                [
                    new(AiRole.TechnicalAnalyst, new ModelRef(AgentApp.Provider, "model-z")),
                    new(AiRole.Strategist, new ModelRef(AgentApp.Second, AgentApp.Model)),
                    new(AiRole.RiskCritic, model),
                ],
            });

        Assert.Equal(
            [
                new ActivationProblem(ActivationProblemKind.ModelNotAccessible, AiRole.TechnicalAnalyst),
                new ActivationProblem(ActivationProblemKind.ModelNotAccessible, AiRole.Strategist),
            ],
            (await app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation)).Problems);

        // The catalog stops calling the model Active.
        await Sql.ExecuteAsync(app.ConnectionString, $"UPDATE models SET status = 'Retired' WHERE model_id = '{AgentApp.Model}'", Cancellation);
        var ready = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Ready"));

        Assert.Equal(
            AgentRoles.Required.Select(role => new ActivationProblem(ActivationProblemKind.ModelNotActive, role)),
            (await app.Agents.ActivateAsync(user.Id, ready.Id, Cancellation)).Problems);
    }

    [Fact]
    public async Task A_model_only_an_invalid_key_can_use_doesnt_count()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.CreateAsync(user.Id, AgentApp.Ready(user));
        await Sql.ExecuteAsync(app.ConnectionString, $"UPDATE credentials SET status = 'Invalid' WHERE id = '{user.AiKey}'", Cancellation);

        var result = await app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation);

        Assert.Equal(
            AgentRoles.Required.Select(role => new ActivationProblem(ActivationProblemKind.ModelNotAccessible, role)),
            result.Problems);
    }

    [Fact]
    public async Task An_optional_role_or_a_fallback_that_cant_be_used_only_warns()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var model = new ModelRef(AgentApp.Provider, AgentApp.Model);
        var missing = new ModelRef(AgentApp.Second, AgentApp.Model);
        var agent = await app.CreateAsync(
            user.Id,
            AgentApp.Ready(user) with
            {
                Roles =
                [
                    new(AiRole.TechnicalAnalyst, model),
                    new(AiRole.SentimentAnalyst, missing),
                    new(AiRole.Strategist, model, missing),
                    new(AiRole.RiskCritic, model),
                ],
            });

        var result = await app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Activated, result.Outcome);
        Assert.Equal(
            [
                new ActivationWarning(ActivationWarningKind.OptionalModelUnavailable, AiRole.SentimentAnalyst),
                new ActivationWarning(ActivationWarningKind.FallbackUnavailable, AiRole.Strategist),
            ],
            result.Warnings);
    }

    /// <summary>
    /// FR-AGT-13, through Execution's port, which reads the balance from Binance: the capital of a key's active
    /// agents on one market, per quote asset, against what the account has available in that asset.
    /// </summary>
    [Fact]
    public async Task More_capital_than_the_account_has_is_a_warning_and_not_a_refusal()
    {
        await using var app = new AgentApp(postgres);
        app.Binance.On(
            HttpMethod.Get, "/api/v3/exchangeInfo", _ => FakeBinance.Json(BinanceSamples.SpotExchangeInfo().Replace("BREAK", "TRADING")));
        var user = await app.UserAsync();
        var reads = app.Binance.To(CredentialApp.SpotAccountPath).Count;

        // The Spot account has 250.12 USDT and 0.001 BTC available, and the Futures account 103.12345678 USDT.
        var spot = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Spot") with { CapitalAllocation = 200m });
        var futures = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Futures", Market.Futures) with { CapitalAllocation = 100m });
        var bitcoin = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Bitcoin") with { Symbol = "ETHBTC", CapitalAllocation = 0.01m });

        var withinBalance = await app.Agents.ActivateAsync(user.Id, spot.Id, Cancellation);
        var withinMargin = await app.Agents.ActivateAsync(user.Id, futures.Id, Cancellation);
        var beyond = await app.Agents.ActivateAsync(user.Id, bitcoin.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Activated, withinBalance.Outcome);
        Assert.Empty(withinBalance.Warnings!);
        Assert.Empty(withinMargin.Warnings!);
        Assert.Equal(ActivationOutcome.Activated, beyond.Outcome);
        Assert.Equal(
            [new ActivationWarning(ActivationWarningKind.CapitalExceedsBalance, Asset: "BTC", Allocated: 0.01m, Available: 0.001m)],
            beyond.Warnings);

        // The balance is read at most once a minute per key and market, because every user shares the weight.
        Assert.Equal(reads + 1, app.Binance.To(CredentialApp.SpotAccountPath).Count);

        var current = (await app.Agents.FindAsync(user.Id, spot.Id, Cancellation))!;
        await app.Agents.UpdateAsync(user.Id, spot.Id, current.Version, current.Settings with { CapitalAllocation = 300m }, Cancellation);
        await app.Agents.PauseAsync(user.Id, spot.Id, Cancellation);
        app.Clock.Advance(TimeSpan.FromMinutes(1));

        var resumed = await app.Agents.ActivateAsync(user.Id, spot.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Activated, resumed.Outcome);
        Assert.Equal(
            [new ActivationWarning(ActivationWarningKind.CapitalExceedsBalance, Asset: "USDT", Allocated: 300m, Available: 250.12m)],
            resumed.Warnings);
        Assert.Equal(reads + 2, app.Binance.To(CredentialApp.SpotAccountPath).Count);
    }

    /// <summary>AC-13.</summary>
    [Fact]
    public async Task Two_agents_cannot_be_active_on_the_same_key_market_and_pair()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        await app.ActiveAsync(user, "First");
        var second = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Second"));

        var result = await app.Agents.ActivateAsync(user.Id, second.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Refused, result.Outcome);
        Assert.Equal([new ActivationProblem(ActivationProblemKind.SlotTaken, OtherAgent: "First")], result.Problems);
        Assert.Equal(AgentStatus.Draft, await app.StatusAsync(user.Id, second.Id));
    }

    [Fact]
    public async Task A_paused_agent_keeps_its_slot_and_a_stopped_or_archived_one_frees_it()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var first = await app.ActiveAsync(user, "First");
        var second = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Second"));
        await app.Agents.PauseAsync(user.Id, first.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Refused, (await app.Agents.ActivateAsync(user.Id, second.Id, Cancellation)).Outcome);

        await app.Agents.StopAsync(user.Id, first.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Activated, (await app.Agents.ActivateAsync(user.Id, second.Id, Cancellation)).Outcome);

        // And back: First can't take the slot again until Second is archived.
        await app.Agents.PauseAsync(user.Id, second.Id, Cancellation);
        Assert.Equal(ActivationOutcome.Refused, (await app.Agents.ActivateAsync(user.Id, first.Id, Cancellation)).Outcome);
        await app.Agents.ArchiveAsync(user.Id, second.Id, Cancellation);
        Assert.Equal(ActivationOutcome.Activated, (await app.Agents.ActivateAsync(user.Id, first.Id, Cancellation)).Outcome);
    }

    [Fact]
    public async Task Another_market_or_another_key_is_another_slot()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        await app.ActiveAsync(user, "Spot");
        var secondKey = await app.AddBinanceAsync(user.Id, "Second account");

        await app.ActiveAsync(user, "Futures", Market.Futures);
        var other = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Other key") with { ExchangeCredentialId = secondKey });

        Assert.Equal(ActivationOutcome.Activated, (await app.Agents.ActivateAsync(user.Id, other.Id, Cancellation)).Outcome);
    }

    [Fact]
    public async Task Of_two_activations_at_once_on_one_slot_exactly_one_wins()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agents = new List<AgentRecord>();

        for (var number = 0; number < 4; number++)
        {
            agents.Add(await app.CreateAsync(user.Id, AgentApp.Ready(user, $"Agent {number}")));
        }

        var results = await Task.WhenAll(agents.Select(agent => app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation)));

        Assert.Single(results, result => result.Outcome == ActivationOutcome.Activated);
        Assert.Equal(3, results.Count(result => result.Problems is [{ Kind: ActivationProblemKind.SlotTaken }]));
    }

    [Fact]
    public async Task Moving_an_agent_that_holds_a_slot_onto_a_taken_pair_is_refused()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        await app.ActiveAsync(user, "Spot");
        var futures = await app.ActiveAsync(user, "Futures", Market.Futures);
        var paused = (await app.Agents.PauseAsync(user.Id, futures.Id, Cancellation)).Agent!;

        var result = await app.Agents.UpdateAsync(user.Id, paused.Id, paused.Version, paused.Settings with { Market = Market.Spot }, Cancellation);

        Assert.Equal(AgentSaveOutcome.SlotTaken, result.Outcome);
        Assert.Equal([new ActivationProblem(ActivationProblemKind.SlotTaken, OtherAgent: "Spot")], result.Problems);
    }

    /// <summary>The database refuses a second holder too, whatever the code above it does.</summary>
    [Fact]
    public async Task The_database_refuses_two_holders_of_one_slot()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        await app.ActiveAsync(user, "First");
        var second = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Second"));

        var exception = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsync(
            app.ConnectionString, $"UPDATE agents SET status = 'Paused' WHERE id = '{second.Id}'", Cancellation));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("ix_agents_trading_slot", exception.ConstraintName);
    }

    [Fact]
    public async Task An_active_agent_cant_be_edited_into_settings_it_couldnt_be_activated_with()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user);

        var result = await app.Agents.UpdateAsync(
            user.Id,
            agent.Id,
            agent.Version,
            agent.Settings with { CapitalAllocation = null, Roles = [.. agent.Settings.Roles.Where(role => role.Role != AiRole.RiskCritic)] },
            Cancellation);

        Assert.Equal(AgentSaveOutcome.WouldFailActivation, result.Outcome);
        Assert.Equal(
            [
                new ActivationProblem(ActivationProblemKind.RoleUnassigned, AiRole.RiskCritic),
                new ActivationProblem(ActivationProblemKind.NoCapitalAllocation),
            ],
            result.Problems);
    }
}
