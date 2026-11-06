using System.Net;
using System.Net.Http;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Agents;

/// <summary>FR-AGT-01, FR-AGT-02, FR-AGT-04, FR-AGT-12, DATA-R-04: creating, editing, cloning, archiving and deleting agents.</summary>
public sealed class AgentLifecycleTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => AgentApp.Cancellation;

    [Fact]
    public async Task A_new_agent_is_a_cleaned_draft_and_its_creation_is_audited()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();

        var agent = await app.CreateAsync(
            user.Id,
            AgentApp.Ready(user) with { Name = "  Scalper\u0007 ", Symbol = " btcusdt ", CustomInstructions = "Prefer \u0000quiet\nmarkets." });

        Assert.Equal("Scalper", agent.Settings.Name);
        Assert.Equal("BTCUSDT", agent.Settings.Symbol);
        Assert.Equal("USDT", agent.QuoteAsset);
        Assert.Equal("Prefer quiet\nmarkets.", agent.Settings.CustomInstructions);
        Assert.Equal(AgentStatus.Draft, agent.Status);
        Assert.Equal(TradingEnvironment.Demo, agent.Settings.Environment);
        Assert.Equal(RiskSettings.Default, agent.Settings.Risk);
        Assert.Equal(3, agent.Settings.Roles.Count);

        var record = Assert.Single(await app.AuditAsync(user.Id), row => row.Action == "AgentCreated");
        Assert.Equal("User", record.ActorKind);
        Assert.Equal("Agent", record.TargetType);
        Assert.Equal(agent.Id.ToString(), record.TargetId);
        Assert.Contains("Scalper", record.Summary, StringComparison.Ordinal);
        Assert.Contains("Test model-a", record.Summary, StringComparison.Ordinal);

        // Administrators can search the trail, and must not read what users write to their models (FR-ADM-01).
        Assert.DoesNotContain("quiet", record.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Names_are_unique_per_user_ignoring_case_until_the_agent_is_archived()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var first = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Scalper"));

        Assert.Equal(AgentSaveOutcome.NameTaken, (await app.Agents.CreateAsync(user.Id, AgentApp.Ready(user, "SCALPER"), Cancellation)).Outcome);

        // Another user's names are theirs.
        var other = await app.UserAsync();
        await app.CreateAsync(other.Id, AgentApp.Ready(other, "Scalper"));

        Assert.Equal(TransitionOutcome.Done, (await app.Agents.ArchiveAsync(user.Id, first.Id, Cancellation)).Outcome);
        await app.CreateAsync(user.Id, AgentApp.Ready(user, "scalper"));

        // Renaming onto a taken name is refused too.
        var second = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Swing"));
        var renamed = await app.Agents.UpdateAsync(user.Id, second.Id, second.Version, second.Settings with { Name = "Scalper" }, Cancellation);
        Assert.Equal(AgentSaveOutcome.NameTaken, renamed.Outcome);
    }

    [Fact]
    public async Task A_user_has_at_most_the_configured_number_of_agents_that_arent_archived()
    {
        await using var app = new AgentApp(postgres, new() { ["Agents:MaxAgentsPerUser"] = "2" });
        var user = await app.UserAsync();
        var first = await app.CreateAsync(user.Id, AgentApp.Ready(user, "One"));
        var second = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Two"));

        Assert.Equal(2, app.Agents.MaxAgentsPerUser);
        Assert.Equal(AgentSaveOutcome.LimitReached, (await app.Agents.CreateAsync(user.Id, AgentApp.Ready(user, "Three"), Cancellation)).Outcome);
        Assert.Equal(AgentSaveOutcome.LimitReached, (await app.Agents.CloneAsync(user.Id, first.Id, "One again", Cancellation)).Outcome);

        await app.Agents.ArchiveAsync(user.Id, second.Id, Cancellation);

        await app.CreateAsync(user.Id, AgentApp.Ready(user, "Three"));
    }

    [Fact]
    public async Task Creating_many_agents_at_once_never_passes_the_limit()
    {
        await using var app = new AgentApp(postgres, new() { ["Agents:MaxAgentsPerUser"] = "3" });
        var user = await app.UserAsync();

        var results = await Task.WhenAll(Enumerable.Range(1, 8).Select(number =>
            app.Agents.CreateAsync(user.Id, AgentApp.Ready(user, $"Agent {number}"), Cancellation)));

        Assert.Equal(3, results.Count(result => result.Outcome == AgentSaveOutcome.Saved));
        Assert.Equal(5, results.Count(result => result.Outcome == AgentSaveOutcome.LimitReached));
        Assert.Equal(3, (await app.Agents.ListAsync(user.Id, includeArchived: true, Cancellation)).Count);
    }

    [Theory]
    [InlineData(Market.Spot, "DOGEUSDT", AgentSaveOutcome.SymbolUnknown)]
    [InlineData(Market.Spot, "ETHBTC", AgentSaveOutcome.SymbolNotTrading)]
    [InlineData(Market.Futures, "ETHBTC", AgentSaveOutcome.SymbolUnknown)]
    public async Task The_pair_must_be_one_Binance_trades_on_the_market(Market market, string symbol, AgentSaveOutcome outcome)
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();

        var result = await app.Agents.CreateAsync(user.Id, AgentApp.Ready(user, market: market) with { Symbol = symbol }, Cancellation);

        Assert.Equal(outcome, result.Outcome);
        Assert.Empty(await app.Agents.ListAsync(user.Id, includeArchived: true, Cancellation));
    }

    [Fact]
    public async Task A_Futures_pair_must_be_a_perpetual_contract()
    {
        await using var app = new AgentApp(postgres);
        app.Binance.On(
            HttpMethod.Get, "/fapi/v1/exchangeInfo", _ => FakeBinance.Json(BinanceSamples.FuturesExchangeInfo.Replace("PERPETUAL", "CURRENT_QUARTER")));
        var user = await app.UserAsync();

        var result = await app.Agents.CreateAsync(user.Id, AgentApp.Ready(user, market: Market.Futures), Cancellation);

        Assert.Equal(AgentSaveOutcome.SymbolNotPerpetual, result.Outcome);
    }

    [Fact]
    public async Task A_pair_that_cant_be_checked_isnt_saved()
    {
        await using var app = new AgentApp(postgres);
        app.Binance.On(HttpMethod.Get, "/api/v3/exchangeInfo", _ => FakeBinance.Error(HttpStatusCode.ServiceUnavailable, -1001, "Internal error."));
        var user = await app.UserAsync();

        var result = await app.Agents.CreateAsync(user.Id, AgentApp.Ready(user), Cancellation);

        Assert.Equal(AgentSaveOutcome.SymbolUnverifiable, result.Outcome);
    }

    /// <summary>UC-04: an agent starts in Demo, and only switching makes it Live, never a new agent's settings or an edit.</summary>
    [Fact]
    public async Task Live_is_refused_on_creation_and_by_an_edit()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.CreateAsync(user.Id, AgentApp.Ready(user));

        var created = await app.Agents.CreateAsync(user.Id, AgentApp.Ready(user, "Live one") with { Environment = TradingEnvironment.Live }, Cancellation);
        var updated = await app.Agents.UpdateAsync(
            user.Id, agent.Id, agent.Version, agent.Settings with { Environment = TradingEnvironment.Live }, Cancellation);

        Assert.Equal(AgentSaveOutcome.EnvironmentNotEditable, created.Outcome);
        Assert.Equal(AgentSaveOutcome.EnvironmentNotEditable, updated.Outcome);
    }

    [Fact]
    public async Task Each_setting_out_of_its_range_is_named()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var model = new ModelRef(AgentApp.Provider, AgentApp.Model);

        var result = await app.Agents.CreateAsync(
            user.Id,
            AgentApp.Ready(user) with
            {
                Name = " \u0007 ",
                Description = new string('d', AgentLimits.DescriptionMaxLength + 1),
                Symbol = "BTC/USDT",
                Roles = [new(AiRole.Strategist, model), new(AiRole.Strategist, model, model)],
                CapitalAllocation = 0m,
                Risk = RiskSettings.Default with { MaxLeverage = 0, MaxStopDistanceAtr = 0.25m },
                Budget = new AiBudget(10m, 5m, null, null),
                Experience = new ExperienceMemory(true, AgentLimits.MaxReflectionsInPrompt + 1),
                CustomInstructions = new string('c', AgentLimits.CustomInstructionsMaxLength + 1),
            },
            Cancellation);

        Assert.Equal(AgentSaveOutcome.Invalid, result.Outcome);
        Assert.Equal(
            [
                AgentField.Name, AgentField.Description, AgentField.CustomInstructions, AgentField.Symbol, AgentField.Roles,
                AgentField.CapitalAllocation, AgentField.Risk, AgentField.Budget, AgentField.ReflectionsInPrompt,
            ],
            result.Fields);
        Assert.Equal([RiskSetting.MaxLeverage, RiskSetting.MaxStopDistanceAtr], result.RiskSettings);
    }

    [Fact]
    public async Task The_exchange_key_must_be_the_users_Binance_key_for_the_agents_environment()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var other = await app.UserAsync();

        var someoneElses = await app.Agents.CreateAsync(user.Id, AgentApp.Ready(user) with { ExchangeCredentialId = other.ExchangeKey }, Cancellation);
        var aiKey = await app.Agents.CreateAsync(user.Id, AgentApp.Ready(user) with { ExchangeCredentialId = user.AiKey }, Cancellation);
        var none = await app.Agents.CreateAsync(user.Id, AgentApp.Ready(user) with { ExchangeCredentialId = null }, Cancellation);

        Assert.Equal([AgentField.ExchangeCredential], someoneElses.Fields);
        Assert.Equal([AgentField.ExchangeCredential], aiKey.Fields);
        Assert.Equal(AgentSaveOutcome.Saved, none.Outcome);
    }

    [Fact]
    public async Task An_edit_saves_and_records_what_changed_and_a_change_of_execution_mode_on_its_own()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.CreateAsync(user.Id, AgentApp.Ready(user));

        var result = await app.Agents.UpdateAsync(
            user.Id,
            agent.Id,
            agent.Version,
            agent.Settings with { Name = "Patient", ExecutionMode = ExecutionMode.Autonomous, Risk = RiskSettings.Default with { MaxLeverage = 3 } },
            Cancellation);

        Assert.Equal(AgentSaveOutcome.Saved, result.Outcome);
        Assert.Equal("Patient", result.Agent!.Settings.Name);
        Assert.Equal(3, result.Agent.Settings.Risk.MaxLeverage);
        Assert.Equal(agent.Version + 1, result.Agent.Version);
        Assert.Equal(3, result.Agent.Settings.Roles.Count);

        var records = await app.AuditAsync(user.Id);
        var updated = Assert.Single(records, row => row.Action == "AgentUpdated");
        Assert.Contains("Scalper", updated.Summary, StringComparison.Ordinal);
        Assert.Contains("Patient", updated.Summary, StringComparison.Ordinal);
        Assert.Contains(AgentAuditFields.RiskField(RiskSetting.MaxLeverage), updated.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(AgentAuditFields.Symbol, updated.Summary, StringComparison.Ordinal);

        var mode = Assert.Single(records, row => row.Action == "AgentExecutionModeChanged");
        Assert.Contains("Confirm", mode.Summary, StringComparison.Ordinal);
        Assert.Contains("Autonomous", mode.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_from_an_older_version_changes_nothing()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.CreateAsync(user.Id, AgentApp.Ready(user));
        await app.Agents.UpdateAsync(user.Id, agent.Id, agent.Version, agent.Settings with { Name = "First" }, Cancellation);

        var stale = await app.Agents.UpdateAsync(user.Id, agent.Id, agent.Version, agent.Settings with { Name = "Second" }, Cancellation);

        Assert.Equal(AgentSaveOutcome.Changed, stale.Outcome);
        Assert.Equal("First", stale.Agent!.Settings.Name);
    }

    [Fact]
    public async Task A_clone_is_a_draft_with_the_same_settings_and_none_of_the_history()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var original = await app.ActiveAsync(user);

        var result = await app.Agents.CloneAsync(user.Id, original.Id, "Scalper (copy)", Cancellation);

        Assert.Equal(AgentSaveOutcome.Saved, result.Outcome);
        var clone = result.Agent!;
        Assert.NotEqual(original.Id, clone.Id);
        Assert.Equal(AgentStatus.Draft, clone.Status);
        Assert.Equal(original.Settings with { Name = "Scalper (copy)", Roles = clone.Settings.Roles }, clone.Settings);
        Assert.Equal(original.Settings.Roles, clone.Settings.Roles);

        var created = (await app.AuditAsync(user.Id)).Last(row => row.Action == "AgentCreated");
        Assert.Equal(clone.Id.ToString(), created.TargetId);
        Assert.Contains(AgentAuditFields.ClonedFrom, created.Summary, StringComparison.Ordinal);

        Assert.Equal(AgentSaveOutcome.NameTaken, (await app.Agents.CloneAsync(user.Id, original.Id, "scalper", Cancellation)).Outcome);
    }

    [Fact]
    public async Task An_archived_agent_can_be_cloned_or_deleted_for_good_and_nothing_else()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.CreateAsync(user.Id, AgentApp.Ready(user));

        Assert.Equal(TransitionOutcome.NotAllowed, (await app.Agents.DeleteAsync(user.Id, agent.Id, Cancellation)).Outcome);

        var archived = (await app.Agents.ArchiveAsync(user.Id, agent.Id, Cancellation)).Agent!;

        Assert.Equal(AgentStatus.Archived, archived.Status);
        Assert.Empty(await app.Agents.ListAsync(user.Id, includeArchived: false, Cancellation));
        Assert.Single(await app.Agents.ListAsync(user.Id, includeArchived: true, Cancellation));
        Assert.Equal(AgentSaveOutcome.Archived, (await app.Agents.UpdateAsync(user.Id, agent.Id, archived.Version, archived.Settings, Cancellation)).Outcome);
        Assert.Equal(ActivationOutcome.NotAllowed, (await app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation)).Outcome);
        Assert.Equal(AgentSaveOutcome.Saved, (await app.Agents.CloneAsync(user.Id, agent.Id, "Revived", Cancellation)).Outcome);

        Assert.Equal(TransitionOutcome.Done, (await app.Agents.DeleteAsync(user.Id, agent.Id, Cancellation)).Outcome);

        Assert.Null(await app.Agents.FindAsync(user.Id, agent.Id, Cancellation));
        Assert.Equal(0, await Sql.ScalarAsync<long>(
            app.ConnectionString, $"SELECT count(*) FROM agent_role_assignments WHERE agent_id = '{agent.Id}'", Cancellation));

        var deleted = (await app.AuditAsync(user.Id)).Last();
        Assert.Equal("AgentDeleted", deleted.Action);
        Assert.Contains("Scalper", deleted.Summary, StringComparison.Ordinal);
    }

    /// <summary>FR-AGT-04: the states and the moves between them, each audited with who made it and why.</summary>
    [Fact]
    public async Task Each_status_allows_only_its_own_changes_and_each_change_is_audited()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var id = (await app.CreateAsync(user.Id, AgentApp.Ready(user))).Id;

        // Draft
        Assert.Equal(TransitionOutcome.NotAllowed, (await app.Agents.PauseAsync(user.Id, id, Cancellation)).Outcome);
        Assert.Equal(TransitionOutcome.NotAllowed, (await app.Agents.StopAsync(user.Id, id, Cancellation)).Outcome);
        Assert.Equal(ActivationOutcome.Activated, (await app.Agents.ActivateAsync(user.Id, id, Cancellation)).Outcome);

        // Active
        Assert.Equal(ActivationOutcome.NotAllowed, (await app.Agents.ActivateAsync(user.Id, id, Cancellation)).Outcome);
        Assert.Equal(TransitionOutcome.NotAllowed, (await app.Agents.ArchiveAsync(user.Id, id, Cancellation)).Outcome);
        Assert.Equal(TransitionOutcome.NotAllowed, (await app.Agents.DeleteAsync(user.Id, id, Cancellation)).Outcome);
        Assert.Equal(TransitionOutcome.Done, (await app.Agents.PauseAsync(user.Id, id, Cancellation)).Outcome);

        // Paused
        Assert.Equal(TransitionOutcome.NotAllowed, (await app.Agents.PauseAsync(user.Id, id, Cancellation)).Outcome);
        Assert.Equal(ActivationOutcome.Activated, (await app.Agents.ActivateAsync(user.Id, id, Cancellation)).Outcome);
        Assert.Equal(TransitionOutcome.Done, (await app.Agents.StopAsync(user.Id, id, Cancellation)).Outcome);

        // Stopped. The kill switch still closes what it holds (FR-AGT-08), and it stays Stopped.
        Assert.Equal(TransitionOutcome.NotAllowed, (await app.Agents.StopAsync(user.Id, id, Cancellation)).Outcome);
        Assert.Equal(TransitionOutcome.NotAllowed, (await app.Agents.PauseAsync(user.Id, id, Cancellation)).Outcome);
        Assert.Equal(KillOutcome.NothingOpen, (await app.Agents.StopAndCloseAsync(user.Id, id, null, Cancellation)).Outcome);
        Assert.Equal(AgentStatus.Stopped, await app.StatusAsync(user.Id, id));
        Assert.Equal(TransitionOutcome.Done, (await app.Agents.ArchiveAsync(user.Id, id, Cancellation)).Outcome);
        Assert.Equal(AgentStatus.Archived, await app.StatusAsync(user.Id, id));

        // Archived
        Assert.Equal(KillOutcome.NotAllowed, (await app.Agents.StopAndCloseAsync(user.Id, id, null, Cancellation)).Outcome);

        var changes = (await app.AuditAsync(user.Id)).Where(row => row.Action.StartsWith("Agent", StringComparison.Ordinal)).ToList();

        Assert.Equal(
            ["AgentCreated", "AgentActivated", "AgentPaused", "AgentActivated", "AgentStopped", "AgentKillSwitch", "AgentArchived"],
            changes.Select(row => row.Action));
        Assert.All(changes, row => Assert.Equal("User", row.ActorKind));
        Assert.All(changes.Skip(1), row => Assert.Contains("\"Reason\"", row.Summary, StringComparison.Ordinal));
        Assert.Contains("Paused", changes[3].Summary, StringComparison.Ordinal);
    }
}
