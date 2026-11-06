using System.Net;
using System.Net.Http;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Tests.Email;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// UC-04, FR-AGT-05, FR-AUTH-03, FR-AUTH-05, FR-AUD-01: switching a paused or stopped Demo agent to Live. The users are
/// real accounts, because the email and two-factor checks read them.
/// </summary>
public sealed class AgentLiveSwitchTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => AgentApp.Cancellation;

    [Fact]
    public async Task A_paused_agent_switches_to_its_Live_key_and_becomes_active_and_both_changes_are_audited()
    {
        await using var app = new AgentApp(postgres);
        var owner = await LiveUserAsync(app);
        using var _ = owner.Visitor;
        var agent = await PausedAsync(app, owner.User);
        var records = (await app.AuditAsync(owner.User.Id)).Count;

        var result = await app.Agents.SwitchToLiveAsync(owner.User.Id, agent.Id, Request(agent, owner.LiveKey), Cancellation);

        Assert.Equal(LiveSwitchOutcome.Switched, result.Outcome);
        var live = result.Agent!;
        Assert.Equal(TradingEnvironment.Live, live.Settings.Environment);
        Assert.Equal(owner.LiveKey, live.Settings.ExchangeCredentialId);
        Assert.Equal((AgentStatus.Active, AgentStatusReason.User), (live.Status, live.StatusReason));
        Assert.Equal("USDT", live.QuoteAsset);
        Assert.Equal(agent.Version + 1, live.Version);

        var audit = (await app.AuditAsync(owner.User.Id)).Skip(records).ToList();
        Assert.Equal(["AgentSwitchedToLive", "AgentActivated"], audit.Select(row => row.Action));
        Assert.All(audit, row => Assert.Equal(("User", agent.Id.ToString()), (row.ActorKind, row.TargetId)));

        var switched = audit[0].Summary;
        Assert.Contains("Demo account", switched, StringComparison.Ordinal);
        Assert.Contains("Live account", switched, StringComparison.Ordinal);
        Assert.Contains(AgentAuditFields.AcknowledgedRiskDisclosure, switched, StringComparison.Ordinal);
        Assert.Contains(LegalDocuments.RiskDisclosureVersion, switched, StringComparison.Ordinal);
        Assert.Contains(AgentAuditFields.AcknowledgedExecutionMode, switched, StringComparison.Ordinal);
        Assert.Contains(nameof(ExecutionMode.Confirm), switched, StringComparison.Ordinal);
        Assert.Contains("Paused", audit[1].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stopped_agent_switches_too_and_a_Live_agent_then_pauses_and_resumes_without_the_gate()
    {
        await using var app = new AgentApp(postgres);
        var owner = await LiveUserAsync(app);
        using var _ = owner.Visitor;
        var agent = await app.ActiveAsync(owner.User);
        await app.Agents.StopAsync(owner.User.Id, agent.Id, Cancellation);
        agent = (await app.Agents.FindAsync(owner.User.Id, agent.Id, Cancellation))!;

        var result = await app.Agents.SwitchToLiveAsync(owner.User.Id, agent.Id, Request(agent, owner.LiveKey), Cancellation);

        Assert.Equal(LiveSwitchOutcome.Switched, result.Outcome);
        Assert.Equal(TransitionOutcome.Done, (await app.Agents.PauseAsync(owner.User.Id, agent.Id, Cancellation)).Outcome);

        var resumed = await app.Agents.ActivateAsync(owner.User.Id, agent.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Activated, resumed.Outcome);
        Assert.Equal(TradingEnvironment.Live, resumed.Agent!.Settings.Environment);
    }

    /// <summary>UC-04 starts from an agent the user paused or stopped. A Draft has never run in Demo.</summary>
    [Fact]
    public async Task Only_a_paused_or_stopped_Demo_agent_can_switch()
    {
        await using var app = new AgentApp(postgres);
        var owner = await LiveUserAsync(app);
        using var _ = owner.Visitor;
        // The Live one first, so it leaves the Demo key's slot for the one in Error.
        var live = await PausedAsync(app, owner.User, "Live");
        Assert.Equal(LiveSwitchOutcome.Switched, (await app.Agents.SwitchToLiveAsync(owner.User.Id, live.Id, Request(live, owner.LiveKey), Cancellation)).Outcome);
        await app.Agents.PauseAsync(owner.User.Id, live.Id, Cancellation);
        var errored = await PausedAsync(app, owner.User, "Errored");
        await Sql.ExecuteAsync(app.ConnectionString, $"UPDATE agents SET status = 'Error', version = version + 1 WHERE id = '{errored.Id}'", Cancellation);
        var active = await app.ActiveAsync(owner.User, "Active", Market.Futures);
        var draft = await app.CreateAsync(owner.User.Id, AgentApp.Ready(owner.User, "Draft"));
        var archived = await app.CreateAsync(owner.User.Id, AgentApp.Ready(owner.User, "Archived"));
        await app.Agents.ArchiveAsync(owner.User.Id, archived.Id, Cancellation);
        var records = (await app.AuditAsync(owner.User.Id)).Count;

        foreach (var id in new[] { draft.Id, active.Id, archived.Id, errored.Id, live.Id })
        {
            var agent = (await app.Agents.FindAsync(owner.User.Id, id, Cancellation))!;
            var result = await app.Agents.SwitchToLiveAsync(owner.User.Id, id, Request(agent, owner.LiveKey), Cancellation);

            Assert.Equal(LiveSwitchOutcome.NotAllowed, result.Outcome);
            Assert.Equal(Snapshot(agent), Snapshot(result.Agent!));
        }

        Assert.Equal(records, (await app.AuditAsync(owner.User.Id)).Count);
    }

    [Fact]
    public async Task An_agent_with_open_positions_or_orders_doesnt_switch()
    {
        var exposure = new FakeExposure();
        await using var app = new AgentApp(postgres, exposure: exposure);
        var owner = await LiveUserAsync(app);
        using var _ = owner.Visitor;
        var agent = await PausedAsync(app, owner.User);
        exposure.Exposed.Add(agent.Id);

        var result = await app.Agents.SwitchToLiveAsync(owner.User.Id, agent.Id, Request(agent, owner.LiveKey), Cancellation);

        Assert.Equal(LiveSwitchOutcome.OpenPositions, result.Outcome);
        Assert.True((await app.Agents.FindAsync(owner.User.Id, agent.Id, Cancellation))!.IsDemo);
    }

    [Fact]
    public async Task Both_acknowledgements_are_needed_and_without_them_nothing_changes()
    {
        await using var app = new AgentApp(postgres);
        var owner = await LiveUserAsync(app);
        using var _ = owner.Visitor;
        var agent = await PausedAsync(app, owner.User);
        var records = (await app.AuditAsync(owner.User.Id)).Count;

        var result = await app.Agents.SwitchToLiveAsync(
            owner.User.Id, agent.Id, Request(agent, owner.LiveKey) with { RiskDisclosureAcknowledged = false, ExecutionModeAcknowledged = false }, Cancellation);

        Assert.Equal(LiveSwitchOutcome.Refused, result.Outcome);
        Assert.Equal(
            [ActivationProblemKind.RiskDisclosureNotAcknowledged, ActivationProblemKind.ExecutionModeNotAcknowledged],
            result.Problems!.Select(problem => problem.Kind));
        Assert.Equal(Snapshot(agent), Snapshot((await app.Agents.FindAsync(owner.User.Id, agent.Id, Cancellation))!));
        Assert.Equal(records, (await app.AuditAsync(owner.User.Id)).Count);
    }

    /// <summary>The user agreed to what the page showed, so a change since then needs showing again.</summary>
    [Fact]
    public async Task A_change_since_the_page_showed_the_agent_or_the_Risk_Disclosure_switches_nothing()
    {
        await using var app = new AgentApp(postgres);
        var owner = await LiveUserAsync(app);
        using var _ = owner.Visitor;
        var agent = await PausedAsync(app, owner.User);
        var request = Request(agent, owner.LiveKey);

        var oldVersion = await app.Agents.SwitchToLiveAsync(owner.User.Id, agent.Id, request with { Version = agent.Version - 1 }, Cancellation);
        var otherMode = await app.Agents.SwitchToLiveAsync(owner.User.Id, agent.Id, request with { ExecutionMode = ExecutionMode.Autonomous }, Cancellation);
        var oldDisclosure = await app.Agents.SwitchToLiveAsync(owner.User.Id, agent.Id, request with { RiskDisclosureVersion = "draft-0" }, Cancellation);

        Assert.All([oldVersion, otherMode, oldDisclosure], result => Assert.Equal(LiveSwitchOutcome.Changed, result.Outcome));
        Assert.True((await app.Agents.FindAsync(owner.User.Id, agent.Id, Cancellation))!.IsDemo);
    }

    [Fact]
    public async Task The_Live_key_must_be_the_users_valid_Live_key_for_the_market()
    {
        await using var app = new AgentApp(postgres);
        var owner = await LiveUserAsync(app);
        using var _ = owner.Visitor;
        app.BinanceAccepts(spot: false);
        var futuresOnly = await app.AddBinanceAsync(owner.User.Id, "Futures only", TradingEnvironment.Live);
        app.BinanceAccepts();
        var agent = await PausedAsync(app, owner.User);

        Assert.Equal([ActivationProblemKind.NoExchangeKey], await ProblemsAsync(app, owner, agent, null));
        Assert.Equal([ActivationProblemKind.ExchangeKeyMissing], await ProblemsAsync(app, owner, agent, owner.User.ExchangeKey));
        Assert.Equal([ActivationProblemKind.ExchangeKeyMissing], await ProblemsAsync(app, owner, agent, owner.User.AiKey));
        Assert.Equal([ActivationProblemKind.MarketNotEnabled], await ProblemsAsync(app, owner, agent, futuresOnly));
    }

    /// <summary>
    /// The pair is checked against Binance's Live symbols, which Spot reads from the market-data server, not against the
    /// Demo exchange's, which the agent was saved with.
    /// </summary>
    [Fact]
    public async Task The_pair_is_checked_on_the_Live_exchange()
    {
        await using var app = new AgentApp(postgres);
        var owner = await LiveUserAsync(app);
        using var _ = owner.Visitor;
        var agent = await PausedAsync(app, owner.User);
        app.Binance.On(
            HttpMethod.Get,
            "/api/v3/exchangeInfo",
            request => request.Host == FakeBinance.MarketData
                ? FakeBinance.Error(HttpStatusCode.ServiceUnavailable, -1001, "Internal error.")
                : FakeBinance.Json(BinanceSamples.SpotExchangeInfo()));

        var problems = await ProblemsAsync(app, owner, agent, owner.LiveKey);

        Assert.Equal([ActivationProblemKind.SymbolUnverifiable], problems);
        Assert.Contains(app.Binance.To("/api/v3/exchangeInfo"), request => request.Host == FakeBinance.MarketData);
    }

    /// <summary>FR-AGT-10: the Live key's account is shared too.</summary>
    [Fact]
    public async Task Another_agent_on_the_Live_key_and_pair_holds_the_slot()
    {
        await using var app = new AgentApp(postgres);
        var owner = await LiveUserAsync(app);
        using var _ = owner.Visitor;
        var first = await PausedAsync(app, owner.User, "First");
        Assert.Equal(LiveSwitchOutcome.Switched, (await app.Agents.SwitchToLiveAsync(owner.User.Id, first.Id, Request(first, owner.LiveKey), Cancellation)).Outcome);
        var second = await PausedAsync(app, owner.User, "Second");

        var result = await app.Agents.SwitchToLiveAsync(owner.User.Id, second.Id, Request(second, owner.LiveKey), Cancellation);

        Assert.Equal(LiveSwitchOutcome.Refused, result.Outcome);
        var problem = Assert.Single(result.Problems!);
        Assert.Equal((ActivationProblemKind.SlotTaken, "First"), (problem.Kind, problem.OtherAgent));
    }

    /// <summary>FR-RISK-08: a loss limit's pause lasts its period, and switching doesn't end it early.</summary>
    [Fact]
    public async Task A_loss_limit_pause_under_way_keeps_the_agent_from_switching()
    {
        await using var app = new AgentApp(postgres);
        var owner = await LiveUserAsync(app);
        using var _ = owner.Visitor;
        var agent = await PausedAsync(app, owner.User);
        var until = app.Clock.GetUtcNow().AddHours(3);
        await Sql.ExecuteAsync(
            app.ConnectionString, $"UPDATE agents SET loss_limit_until = '{until:O}', version = version + 1 WHERE id = '{agent.Id}'", Cancellation);
        agent = (await app.Agents.FindAsync(owner.User.Id, agent.Id, Cancellation))!;

        var result = await app.Agents.SwitchToLiveAsync(owner.User.Id, agent.Id, Request(agent, owner.LiveKey), Cancellation);

        Assert.Equal(LiveSwitchOutcome.Refused, result.Outcome);
        Assert.Equal([ActivationProblemKind.LossLimitPause], result.Problems!.Select(problem => problem.Kind));
    }

    /// <summary>FR-AUTH-03: with email on, only a verified address can switch an agent to Live.</summary>
    [Fact]
    public async Task With_email_on_a_verified_address_is_needed()
    {
        var email = new CapturedEmail();
        await using var app = new AgentApp(postgres, AccountApp.EmailOn(), services: email.ReplaceSender);
        var owner = await LiveUserAsync(app);
        using var _ = owner.Visitor;
        var agent = await PausedAsync(app, owner.User);

        Assert.Equal([ActivationProblemKind.EmailNotVerified], await ProblemsAsync(app, owner, agent, owner.LiveKey));

        (await owner.Visitor.VerifyEmailAsync(email.LastPathTo(owner.Email))).Dispose();

        Assert.Equal(
            LiveSwitchOutcome.Switched, (await app.Agents.SwitchToLiveAsync(owner.User.Id, agent.Id, Request(agent, owner.LiveKey), Cancellation)).Outcome);
    }

    /// <summary>FR-AGT-05: the configuration can require two-factor for Live, which then needs it turned on, and a code.</summary>
    [Fact]
    public async Task When_the_configuration_requires_two_factor_it_must_be_on()
    {
        await using var app = new AgentApp(postgres, new Dictionary<string, string?> { ["Security:RequireTwoFactorForLive"] = "true" });
        var owner = await LiveUserAsync(app);
        using var _ = owner.Visitor;
        var agent = await PausedAsync(app, owner.User);

        Assert.True(app.Agents.TwoFactorRequiredForLive);
        Assert.Equal([ActivationProblemKind.TwoFactorRequired], await ProblemsAsync(app, owner, agent, owner.LiveKey));

        var key = await TwoFactorSteps.StartAsync(owner.Visitor);
        await TwoFactorSteps.EnableAsync(owner.Visitor, key, app.Clock);

        var result = await app.Agents.SwitchToLiveAsync(
            owner.User.Id, agent.Id, Request(agent, owner.LiveKey) with { TwoFactorCode = app.Clock.NextCode(key) }, Cancellation);

        Assert.Equal(LiveSwitchOutcome.Switched, result.Outcome);
    }

    /// <summary>
    /// FR-AUTH-05: with two-factor on, switching needs a code, and nothing changes without a right one. The code is
    /// checked last, so a refusal for anything else leaves it to use.
    /// </summary>
    [Fact]
    public async Task With_two_factor_on_a_code_is_needed_and_a_refusal_for_something_else_doesnt_use_it()
    {
        await using var app = new AgentApp(postgres);
        var owner = await LiveUserAsync(app);
        using var _ = owner.Visitor;
        var key = await TwoFactorSteps.StartAsync(owner.Visitor);
        await TwoFactorSteps.EnableAsync(owner.Visitor, key, app.Clock);
        var agent = await PausedAsync(app, owner.User);
        var records = (await app.AuditAsync(owner.User.Id)).Count;

        var missing = await app.Agents.SwitchToLiveAsync(owner.User.Id, agent.Id, Request(agent, owner.LiveKey), Cancellation);
        var wrong = await app.Agents.SwitchToLiveAsync(owner.User.Id, agent.Id, Request(agent, owner.LiveKey) with { TwoFactorCode = "000000" }, Cancellation);

        Assert.Equal((LiveSwitchOutcome.TwoFactorRefused, (TwoFactorCheck?)TwoFactorCheck.Refused), (missing.Outcome, missing.TwoFactor));
        Assert.Equal((LiveSwitchOutcome.TwoFactorRefused, (TwoFactorCheck?)TwoFactorCheck.Refused), (wrong.Outcome, wrong.TwoFactor));
        Assert.Equal(records, (await app.AuditAsync(owner.User.Id)).Count);

        var code = app.Clock.NextCode(key);
        var unacknowledged = await app.Agents.SwitchToLiveAsync(
            owner.User.Id, agent.Id, Request(agent, owner.LiveKey) with { TwoFactorCode = code, RiskDisclosureAcknowledged = false }, Cancellation);

        Assert.Equal(LiveSwitchOutcome.Refused, unacknowledged.Outcome);

        var right = await app.Agents.SwitchToLiveAsync(owner.User.Id, agent.Id, Request(agent, owner.LiveKey) with { TwoFactorCode = code }, Cancellation);

        Assert.Equal(LiveSwitchOutcome.Switched, right.Outcome);
    }

    [Fact]
    public async Task A_Live_agent_keeps_its_environment_through_an_edit_and_its_clone_is_a_Demo_draft()
    {
        await using var app = new AgentApp(postgres);
        var owner = await LiveUserAsync(app);
        using var _ = owner.Visitor;
        var agent = await PausedAsync(app, owner.User);
        var live = (await app.Agents.SwitchToLiveAsync(owner.User.Id, agent.Id, Request(agent, owner.LiveKey), Cancellation)).Agent!;

        var renamed = await app.Agents.UpdateAsync(owner.User.Id, live.Id, live.Version, live.Settings with { Name = "Renamed" }, Cancellation);
        var backToDemo = await app.Agents.UpdateAsync(
            owner.User.Id, live.Id, renamed.Agent!.Version, renamed.Agent.Settings with { Environment = TradingEnvironment.Demo }, Cancellation);
        var demoKey = await app.Agents.UpdateAsync(
            owner.User.Id, live.Id, renamed.Agent.Version, renamed.Agent.Settings with { ExchangeCredentialId = owner.User.ExchangeKey }, Cancellation);
        var clone = await app.Agents.CloneAsync(owner.User.Id, live.Id, "Renamed (copy)", Cancellation);

        Assert.Equal(AgentSaveOutcome.Saved, renamed.Outcome);
        Assert.Equal((TradingEnvironment.Live, owner.LiveKey), (renamed.Agent.Settings.Environment, renamed.Agent.Settings.ExchangeCredentialId));
        Assert.Equal(AgentSaveOutcome.EnvironmentNotEditable, backToDemo.Outcome);
        Assert.Equal([AgentField.ExchangeCredential], demoKey.Fields);
        Assert.Equal(AgentSaveOutcome.Saved, clone.Outcome);
        Assert.Equal((AgentStatus.Draft, TradingEnvironment.Demo, (Guid?)null), (clone.Agent!.Status, clone.Agent.Settings.Environment, clone.Agent.Settings.ExchangeCredentialId));
    }

    /// <summary>A Demo agent that ran, paused by its user, ready for UC-04.</summary>
    private static async Task<AgentRecord> PausedAsync(AgentApp app, AgentApp.User user, string name = "Scalper")
    {
        var agent = await app.ActiveAsync(user, name);
        var paused = await app.Agents.PauseAsync(user.Id, agent.Id, Cancellation);

        Assert.Equal(TransitionOutcome.Done, paused.Outcome);

        return paused.Agent!;
    }

    /// <summary>What a switch would change, as values: the version moves with every change.</summary>
    private static (int Version, AgentStatus Status, TradingEnvironment Environment, Guid? Key) Snapshot(AgentRecord agent) =>
        (agent.Version, agent.Status, agent.Settings.Environment, agent.Settings.ExchangeCredentialId);

    /// <summary>What the page sends when the user has ticked both boxes, with two-factor off.</summary>
    private static LiveSwitchRequest Request(AgentRecord agent, Guid? key) =>
        new(agent.Version, key, agent.Settings.ExecutionMode, LegalDocuments.RiskDisclosureVersion, true, true, null);

    private static async Task<IEnumerable<ActivationProblemKind>> ProblemsAsync(AgentApp app, LiveUser owner, AgentRecord agent, Guid? key)
    {
        var result = await app.Agents.SwitchToLiveAsync(owner.User.Id, agent.Id, Request(agent, key), Cancellation);

        Assert.Equal(LiveSwitchOutcome.Refused, result.Outcome);

        return result.Problems!.Select(problem => problem.Kind);
    }

    /// <summary>
    /// An account, signed in, with a valid Binance Demo key, an AI key and a valid Binance Live key, added before any
    /// two-factor a test turns on, because adding an exchange key would need a code after.
    /// </summary>
    internal static async Task<LiveUser> LiveUserAsync(AgentApp app)
    {
        var visitor = Visitor.To(app.App);
        var email = await visitor.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
        var user = new AgentApp.User(userId, await app.AddBinanceAsync(userId), await app.AddAiAsync(userId));
        var live = await app.AddBinanceAsync(userId, "Live account", TradingEnvironment.Live);

        return new LiveUser(user, live, email, visitor);
    }

    internal sealed record LiveUser(AgentApp.User User, Guid LiveKey, string Email, Visitor Visitor);
}
