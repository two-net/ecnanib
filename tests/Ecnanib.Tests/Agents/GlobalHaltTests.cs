using System.Net;
using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.DecisionEngine;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.RiskGuard;
using Ecnanib.Tests.Settings;
using Ecnanib.WebUi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// An operator switches the global halt on or off. While it is on, no agent opens a new
/// position, and open positions are still managed and protected (FR-OPS-3). The halt is stored in
/// the database, and not in the configuration file (NFR-CFG-6). Every switch is recorded in the
/// audit trail, the pages of an operator show nothing of any user (FR-OPS-5), and a user who is
/// not an operator reaches none of it (NFR-SEC-3). See "The global halt" in docs/risk-guard.md,
/// and "Operators" in docs/ownership.md.
/// </summary>
public class GlobalHaltTests
{
    private const string Ada = "ada@example.com";
    private const string Olive = "olive@example.com";

    private const string HaltPage = WebUiModule.GlobalHaltPath;
    private const string OnForm = "halt-on";
    private const string OffForm = "halt-off";

    private const string OnButton = "<button type=\"submit\">Switch the global halt on</button>";
    private const string OffButton = "<button type=\"submit\">Switch the global halt off</button>";
    private const string IsOff = "The global halt is off.";

    private const string Target = "runtime-flag:global-halt";

    private const string SwitchedOnTemplate = "User {UserId} switched the global halt on: no agent opens a new position until it is off";
    private const string SwitchedOffTemplate = "User {UserId} switched the global halt off: agents open positions again";
    private const string RefusedSwitchTemplate = "User {UserId} was refused switching the global halt, which is for an operator";
    private const string RefusedPageTemplate = "User {UserId} was refused {Path}, which is not for them";

    private static readonly DateTimeOffset Morning = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A close of a candle of 15 minutes, and of no longer one.</summary>
    private static readonly DateTimeOffset Quarter = new(2026, 11, 18, 10, 15, 0, TimeSpan.Zero);

    [Fact]
    [ShowsAuditOf(AuditAction.GlobalHaltSwitchedOn)]
    public async Task An_operator_switches_the_global_halt_on_and_it_is_stored_recorded_and_shown()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock, host: host => host.From("203.0.113.7"));
        using var client = application.CreateClient();
        var olive = await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);

        // The screen of an operator leads to the page, which says what the halt does, that it
        // is off, and has the one button. Nothing is stored for a halt that was never switched.
        Assert.Matches($"<a href=\"{HaltPage}\"[^>]*>Global halt</a>", await client.PageAsync(WebUiModule.OperatorStatusPath));

        var before = await client.PageAsync(HaltPage);

        Assert.Contains("<h1>Global halt</h1>", before);
        Assert.Contains("While the global halt is on, no agent opens a new position. Open positions are still managed and protected.", before);
        Assert.Contains(IsOff, before);
        Assert.Contains(OnButton, before);
        Assert.DoesNotContain(OffButton, before);
        Assert.Empty(StoredFlag.All(application.Database));
        Assert.False((await application.GlobalHalt.StateAsync(TestContext.Current.CancellationToken)).On);

        clock.Set(Morning.AddMinutes(5));
        var (path, page) = await client.SubmitAsync(HaltPage, OnForm);

        // She is back on the page, which says that the halt is on, since when, and has the
        // button that switches it off.
        Assert.Equal(HaltPage, path);
        Assert.Contains("The global halt is on, since 26 November 2026, 09:05 UTC.", page);
        Assert.Contains(OffButton, page);
        Assert.DoesNotContain(OnButton, page);
        Assert.DoesNotContain(IsOff, page);

        // It is a row of the database, with who switched it and when (NFR-CFG-6).
        Assert.Equal(
            [new StoredFlag(StoredFlag.GlobalHalt, Value: true, olive.Id, Morning.AddMinutes(5))],
            StoredFlag.All(application.Database));
        Assert.Equal(
            new GlobalHaltState(On: true, Morning.AddMinutes(5)),
            await application.GlobalHalt.StateAsync(TestContext.Current.CancellationToken));

        // And an event of the audit trail: who, what, when, and from where (FR-OPS-5).
        var recorded = Assert.Single(StoredAuditEvent.All(application.Database), audited => audited.Target == Target);

        Assert.Equal(
            (olive.Id, "GlobalHaltSwitchedOn", Morning.AddMinutes(5), "203.0.113.7"),
            (recorded.UserId, recorded.Action, recorded.OccurredAt, recorded.Origin));

        // The log says it by the id of the account, and no more.
        var said = Assert.Single(application.Events(), written => written.Template == SwitchedOnTemplate);

        Assert.Equal(olive.Id.ToString(), said.Text("UserId"));

        // Nothing that is stored says who is an operator: the event says what its user did.
        Assert.DoesNotContain("operator", application.EverythingStored(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [ShowsAuditOf(AuditAction.GlobalHaltSwitchedOff)]
    public async Task An_operator_switches_the_global_halt_off_again_and_that_is_recorded_as_well()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock, host: host => host.From("203.0.113.7"));
        using var client = application.CreateClient();
        var olive = await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);

        await client.SubmitAsync(HaltPage, OnForm);

        clock.Set(Morning.AddMinutes(10));
        var (path, page) = await client.SubmitAsync(HaltPage, OffForm);

        // The page says that it is off, and no longer since when it was on.
        Assert.Equal(HaltPage, path);
        Assert.Contains(IsOff, page);
        Assert.Contains(OnButton, page);
        Assert.DoesNotContain(OffButton, page);
        Assert.DoesNotContain("since", page, StringComparison.Ordinal);

        // The one row of the flag is overwritten: it is state, and the trail has the history.
        Assert.Equal(
            [new StoredFlag(StoredFlag.GlobalHalt, Value: false, olive.Id, Morning.AddMinutes(10))],
            StoredFlag.All(application.Database));
        Assert.Equal(
            new GlobalHaltState(On: false, Since: null),
            await application.GlobalHalt.StateAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            [
                (olive.Id, "GlobalHaltSwitchedOn", Morning, "203.0.113.7"),
                (olive.Id, "GlobalHaltSwitchedOff", Morning.AddMinutes(10), "203.0.113.7"),
            ],
            StoredAuditEvent.All(application.Database)
                .Where(audited => audited.Target == Target)
                .Select(audited => (audited.UserId, audited.Action, audited.OccurredAt, audited.Origin!)));

        Assert.Single(application.Events(), written => written.Template == SwitchedOffTemplate);
    }

    [Fact]
    public async Task A_form_that_is_sent_twice_or_for_what_the_halt_already_is_switches_and_records_nothing_more()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock);
        using var client = application.CreateClient();
        var olive = await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);

        var token = Browser.AntiforgeryTokenIn(await client.PageAsync(HaltPage));

        // Switched off while it is off: a halt that was never switched has no row, and gets
        // none for it. She is led back to the page, which says what the halt is.
        var (path, page) = await client.PostAsync(HaltPage, OffForm, token);

        Assert.Equal(HaltPage, path);
        Assert.Contains(IsOff, page);
        Assert.Empty(StoredFlag.All(application.Database));
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), audited => audited.Target == Target);

        // Switched on twice, as a button that is pressed twice sends its form: the first one
        // switches, and the second finds the halt on, and changes nothing of when it was.
        await client.PostAsync(HaltPage, OnForm, token);

        clock.Set(Morning.AddMinutes(1));
        (path, page) = await client.PostAsync(HaltPage, OnForm, token);

        Assert.Equal(HaltPage, path);
        Assert.Contains("The global halt is on, since 26 November 2026, 09:00 UTC.", page);
        Assert.Equal(
            [new StoredFlag(StoredFlag.GlobalHalt, Value: true, olive.Id, Morning)],
            StoredFlag.All(application.Database));

        var recorded = Assert.Single(StoredAuditEvent.All(application.Database), audited => audited.Target == Target);

        Assert.Equal(("GlobalHaltSwitchedOn", Morning), (recorded.Action, recorded.OccurredAt));
        Assert.Single(application.Events(), written => written.Template == SwitchedOnTemplate);
    }

    [Fact]
    public async Task A_user_who_is_not_an_operator_reaches_neither_the_page_nor_the_switch()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The page is refused, and she is told no more than that there is no page for her.
        using (var refused = await client.GetAsync(HaltPage, TestContext.Current.CancellationToken))
        {
            var said = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("<h1>Page not found</h1>", said);
            Assert.DoesNotContain("Global halt", said);
            Assert.DoesNotContain("halt-on", said);
        }

        // So is a form that she posts there, with a token of a page of her own: she is refused
        // before anything looks at the form, and nothing is switched.
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync("/account"));

        foreach (var form in new[] { OnForm, OffForm })
        {
            using var refused = await client.SendFormAsync(HaltPage, form, token);

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Assert.Empty(StoredFlag.All(application.Database));
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), audited => audited.Target == Target);
        Assert.False((await application.GlobalHalt.StateAsync(TestContext.Current.CancellationToken)).On);

        // Each refusal is in the log, by her id and the path. She stays signed in.
        var refusals = application.Events().Where(written => written.Template == RefusedPageTemplate).ToList();

        Assert.Equal(3, refusals.Count);
        Assert.All(refusals, written => Assert.Equal((ada.Id.ToString(), HaltPage), (written.Text("UserId"), written.Text("Path"))));
        Assert.Contains("Sign out", await client.PageAsync("/account"));
    }

    [Fact]
    public async Task A_visitor_is_sent_to_sign_in_and_switches_nothing()
    {
        using var application = Application();
        using var visitor = application.CreateClient();

        Assert.Equal(
            SessionAuthentication.PageWith(SessionAuthentication.SignInPath, HaltPage), (await visitor.OpenAsync(HaltPage)).Address);

        // A form that a visitor posts is not looked at: they are sent to sign in.
        var (path, _) = await visitor.PostAsync(
            HaltPage, OnForm, Browser.AntiforgeryTokenIn(await visitor.PageAsync(SessionAuthentication.SignInPath)));

        Assert.Equal(SessionAuthentication.SignInPath, path);
        Assert.Empty(StoredFlag.All(application.Database));
    }

    [Fact]
    public async Task A_form_without_the_token_of_its_page_switches_nothing()
    {
        using var application = Application();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);

        // From a page of another site, which has no token of this one (NFR-SEC-5).
        using var answer = await client.SendFormAsync(HaltPage, OnForm, "not-the-token");

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Empty(StoredFlag.All(application.Database));
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), audited => audited.Target == Target);
    }

    [Fact]
    public async Task What_switches_the_halt_asks_again_whether_the_user_of_its_request_is_an_operator()
    {
        // Something that reaches the switch past its page, as nothing of the application does:
        // it asks for the halt to be switched on for whoever sent the request.
        const string direct = "/switches-the-halt-directly";

        using var application = Application(host: host => host.HandleAtTheEnd(async request =>
        {
            if (!request.Request.Path.StartsWithSegments(direct))
            {
                return false;
            }

            var switched = await request.RequestServices.GetRequiredService<IGlobalHaltSwitch>().SwitchAsync(request, on: true);
            await request.Response.WriteAsync(switched ? "Switched" : "Refused");

            return true;
        }));
        using var visitor = application.CreateClient();
        using var user = application.CreateClient();
        using var anOperator = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var olive = await application.RegisterVerifiedAsync(Olive);
        await user.SignInAsync(Ada);
        await anOperator.SignInAsync(Olive);

        // Nobody who is not an operator switches it, signed in or not (NFR-SEC-3).
        Assert.Equal("Refused", await visitor.PageAsync(direct));
        Assert.Equal("Refused", await user.PageAsync(direct));
        Assert.Empty(StoredFlag.All(application.Database));
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), audited => audited.Target == Target);

        var refusals = application.Events().Where(written => written.Template == RefusedSwitchTemplate).ToList();

        Assert.Equal(2, refusals.Count);
        Assert.Equal("Warning", refusals[1].Level);
        Assert.Equal(ada.Id.ToString(), refusals[1].Text("UserId"));

        // An operator does, and it is recorded where it is switched, whatever asked for it.
        Assert.Equal("Switched", await anOperator.PageAsync(direct));
        Assert.True(Assert.Single(StoredFlag.All(application.Database)).Value);
        Assert.Equal(
            (olive.Id, "GlobalHaltSwitchedOn"),
            Assert.Single(
                StoredAuditEvent.All(application.Database).Select(audited => (audited.UserId, audited.Action)),
                audited => audited.Action.StartsWith("GlobalHalt", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("close")]
    public async Task While_the_halt_of_an_operator_is_on_no_agent_opens_a_position_and_an_open_one_is_still_managed(string what)
    {
        // The halt as the application keeps it, and no stand-in: an operator switches it on her
        // page, and the cycle of an agent of another user is checked with it (FR-OPS-3).
        using var cycles = new CyclesApplication(
            Quarter,
            buildsPackages: true,
            holdings: what == "open" ? NothingHeld() : PlanAgents.Holding(PlanAgents.Long),
            settings: Operators);
        using var client = cycles.Application.CreateClient();
        await cycles.Application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);
        await client.SubmitAsync(HaltPage, OnForm);

        var agent = cycles.Agents!.Give(Guid.NewGuid());
        cycles.Models.Says(Role.Strategist, what == "open" ? TeamAnswers.Plan() : TeamAnswers.Close);

        await cycles.RunAsync(Quarter).Within();

        var check = cycles.Records.Of(agent).Check;

        if (what == "open")
        {
            // The team decided to open, and the one rule that stands in the way is the halt.
            Assert.Equal(GuardVerdict.Rejected, check.Check.Verdict);
            Assert.Equal([GuardRule.NoGlobalHalt], RulesThat(check, RuleOutcome.Failed));
            Assert.Empty(RulesThat(check, RuleOutcome.Unknown));
        }
        else
        {
            // A plan that closes reduces risk, and goes on: the halt is not asked of it.
            Assert.Equal(GuardVerdict.Accepted, check.Check.Verdict);
            Assert.Empty(RulesThat(check, RuleOutcome.Failed));
            Assert.DoesNotContain(GuardRule.NoGlobalHalt, RulesThat(check, RuleOutcome.Passed));
        }
    }

    [Fact]
    public async Task Once_the_halt_is_switched_off_agents_open_positions_again()
    {
        using var cycles = new CyclesApplication(Quarter, buildsPackages: true, holdings: NothingHeld(), settings: Operators);
        using var client = cycles.Application.CreateClient();
        await cycles.Application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);
        await client.SubmitAsync(HaltPage, OnForm);
        await client.SubmitAsync(HaltPage, OffForm);

        var agent = cycles.Agents!.Give(Guid.NewGuid());
        cycles.Models.Says(Role.Strategist, TeamAnswers.Plan());

        await cycles.RunAsync(Quarter).Within();

        var check = cycles.Records.Of(agent).Check;

        Assert.Equal(GuardVerdict.Accepted, check.Check.Verdict);
        Assert.Contains(GuardRule.NoGlobalHalt, RulesThat(check, RuleOutcome.Passed));
    }

    [Fact]
    public async Task The_halt_is_still_on_after_the_application_starts_again()
    {
        var clock = new TestClock(Morning);
        TestDatabase database;

        using (var first = Application(clock))
        {
            using var client = first.CreateClient();
            await first.RegisterVerifiedAsync(Olive);
            await client.SignInAsync(Olive);
            await client.SubmitAsync(HaltPage, OnForm);

            database = first.Database;
        }

        // The same database, after a restart: the halt is a row of it, and no setting of the
        // file, which says nothing of a halt (NFR-CFG-6).
        clock.Set(Morning.AddDays(1));
        using var application = Application(clock, database);
        using var again = application.CreateClient();
        await again.SignInAsync(Olive);

        Assert.Equal(
            new GlobalHaltState(On: true, Morning), await application.GlobalHalt.StateAsync(TestContext.Current.CancellationToken));
        Assert.Contains("The global halt is on, since 26 November 2026, 09:00 UTC.", await again.PageAsync(HaltPage));
    }

    [Fact]
    public async Task The_halt_stays_as_it_is_when_the_account_of_whoever_switched_it_is_deleted()
    {
        var clock = new TestClock(Morning);
        TestDatabase database;
        Guid oliveId;

        using (var first = Application(clock))
        {
            using var client = first.CreateClient();
            oliveId = (await first.RegisterVerifiedAsync(Olive)).Id;
            await client.SignInAsync(Olive);
            await client.SubmitAsync(HaltPage, OnForm);

            database = first.Database;
        }

        // Her address is taken off the list of the file, and the application starts again: she
        // is a user like any other now, and deletes her account as anyone does.
        using var application = Application(clock, database, operators: false);
        using var again = application.CreateClient();
        await again.SignInAsync(Olive);
        await again.ConfirmIdentityAsync();

        Assert.Equal(WebUiModule.AccountDeletedPath, (await again.DeleteAccountAsync()).Path);
        Assert.Null(StoredUser.Of(database, Olive));

        // The halt is on for every user, and does not end with one account. Its row keeps
        // nothing of hers, and nothing else in the database does (FR-ACC-9).
        Assert.Equal([new StoredFlag(StoredFlag.GlobalHalt, Value: true, ChangedBy: null, Morning)], StoredFlag.All(database));
        Assert.True((await application.GlobalHalt.StateAsync(TestContext.Current.CancellationToken)).On);
        Assert.DoesNotContain(oliveId.ToString(), application.EverythingStored());
        Assert.DoesNotContain(Olive, application.EverythingStored());
    }

    [Fact]
    public async Task The_pages_of_an_operator_show_nothing_of_any_user()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock);
        using var client = application.CreateClient();

        // Another user, with stored keys and two-factor authentication: what an operator must
        // never be shown (FR-OPS-5).
        var (ada, _, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        await application.GiveKeysAsync(ada.Id, clock.GetUtcNow());

        var olive = await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);

        // The pages of an operator, as they open, and the page of the halt in both of its
        // states, as a switch leaves it.
        var seen = new List<string>
        {
            await client.PageAsync(WebUiModule.OperatorStatusPath),
            await client.PageAsync(HaltPage),
            (await client.SubmitAsync(HaltPage, OnForm)).Page,
            (await client.SubmitAsync(HaltPage, OffForm)).Page,
        };

        // Each is the page that was asked for, and none holds anything of the other user: not
        // her address, not her id, and no key, masked or not. The page of the halt does not say
        // who switched it either: not even the id of the operator herself.
        Assert.All(seen, page => Assert.Contains("<h1", page));
        Assert.All(seen, page =>
        {
            Assert.DoesNotContain(Ada, page);
            Assert.DoesNotContain(ada.Id.ToString(), page);
            Assert.DoesNotContain(ada.Id.ToString("N"), page);
            Assert.DoesNotContain("••••", page);
            Assert.DoesNotContain(olive.Id.ToString(), page);
        });
    }

    /// <summary>The application with one operator in its configuration file, or with none.</summary>
    private static AccountsApplication Application(
        TestClock? clock = null, TestDatabase? database = null, bool operators = true, Action<IWebHostBuilder>? host = null) =>
        new(clock: clock, database: database, settings: operators ? Operators : NoOperators, host: host);

    private static SettingsJson Operators(SettingsJson file) =>
        file.WithJson("Application:Operators", $"[ \"{Olive}\" ]");

    private static SettingsJson NoOperators(SettingsJson file) =>
        file.WithJson("Application:Operators", "[]");

    /// <summary>What an agent holds that has nothing open, and has lost nothing.</summary>
    private static StandInHoldings NothingHeld() =>
        new(new AgentHoldings(Equity: 1000m, Position: null, Orders: [], EquityAtDayStart: 1000m, PeakEquity: 1000m));

    /// <summary>The rules of a check that have an outcome, in the order of the rules.</summary>
    private static IReadOnlyList<GuardRule> RulesThat(RiskCheckRecord check, RuleOutcome outcome) =>
        [.. check.Rules.Where(rule => rule.Outcome == outcome).Select(rule => rule.Rule)];
}
