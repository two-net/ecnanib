using System.Buffers.Text;
using System.Net;
using System.Text;
using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.DecisionEngine;
using Ecnanib.Tests.Experience;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.RiskGuard;
using Ecnanib.WebUi;
using Ecnanib.WebUi.Exports;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Export;

/// <summary>
/// A user can export their trades and decisions as CSV (FR-RPT-4): two downloads, of the closed
/// positions and of the recorded cycles of every agent of theirs. An export holds only the data
/// of the user who is signed in (NFR-SEC-3) and no secret (FR-KEY-8), and each row says its mode,
/// so that a row of Demo is labeled as Demo (FR-BIN-11). See docs/export.md.
/// </summary>
/// <remarks>
/// An agent of these tests has a capital of 1000 USDT, trades BTCUSDT on the Spot market on 1H,
/// and is in Demo unless a test says otherwise. A cycle that a test gives it placed the orders
/// of a position of 0.01 that was entered at 100000 39 seconds after its close, and that its
/// stop-loss closed at 98990 half an hour later: it lost 10.7 in all, which is -1.07 R. A trade
/// that a test gives it has no orders. The clock stands at nine in the morning of a day, UTC.
/// </remarks>
public class CsvExportTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string Trades = WebUiModule.TradesExportPath;
    private const string Decisions = WebUiModule.DecisionsExportPath;

    private static readonly DateTimeOffset Morning = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_file_of_the_trades_has_a_row_for_each_closed_position_that_says_what_the_trade_list_says()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1), name: "Momentum");

        // A position that a cycle opened and its stop-loss closed, one whose result is not
        // recorded yet, and one that is open, which is no trade.
        var cycle = await application.GiveCycleAsync(ada.Id, agent, Morning.AddHours(-2));
        var unrecorded = await application.GiveTradeAsync(ada.Id, agent, Morning.AddMinutes(-5), recorded: false);
        await application.GiveTradeAsync(ada.Id, agent, Morning, closed: false);

        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        var file = await FileAsync(client, Trades);

        Assert.Equal(
            "position_id,agent_id,agent,mode,market,symbol,timeframe,side,quantity,entry_price,exit_price,stop_loss,take_profit," +
            "opened_at,closed_at,exit_reason,asset,net_result,fees,funding,result_in_r,held_seconds,estimate,entry_cycle_id," +
            "market_analyst_model,sentiment_analyst_model,strategist_model,risk_manager_model",
            string.Join(',', file.Columns));

        // The trades of the list of the agent, in its order: latest close first.
        var report = await application.AgentReports.OfAgentAsync(ada.Id, agent, tradesPage: 1, Cancellation);

        Assert.NotNull(report);
        Assert.Equal(2, file.Rows.Count);
        Assert.Equal(report.Trades.Trades.Select(trade => trade.PositionId.ToString()), file.Rows.Select(row => row["position_id"]));
        Assert.Equal(unrecorded.ToString(), file.Rows[0]["position_id"]);

        // The models that held the roles when the positions were opened, each as its provider and its identifier.
        var record = await application.RecordedCycles.FindAsync(ada.Id, agent, cycle, Cancellation);

        Assert.NotNull(record);

        var team = record.Calls.ToDictionary(call => call.Role, call => $"{call.ProviderName} {call.ModelIdentifier}");

        Assert.Equal(4, team.Count);

        // The one with orders says how much it held, the price that it was closed at, its
        // result, and the cycle that opened it, which is a row of the other file.
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["position_id"] = report.Trades.Trades[1].PositionId.ToString(),
                ["agent_id"] = agent.ToString(),
                ["agent"] = "Momentum",
                ["mode"] = "Demo",
                ["market"] = "Spot",
                ["symbol"] = "BTCUSDT",
                ["timeframe"] = "1H",
                ["side"] = "Long",
                ["quantity"] = "0.01",
                ["entry_price"] = "100000",
                ["exit_price"] = "98990",
                ["stop_loss"] = "99000",
                ["take_profit"] = "102000",
                ["opened_at"] = "2026-11-24T07:00:39Z",
                ["closed_at"] = "2026-11-24T07:30:39Z",
                ["exit_reason"] = "StopLoss",
                ["asset"] = "USDT",
                ["net_result"] = "-10.7",
                ["fees"] = "0.5",
                ["funding"] = "-0.1",
                ["result_in_r"] = "-1.07",
                ["held_seconds"] = "1800",
                ["estimate"] = "false",
                ["entry_cycle_id"] = cycle.ToString(),
                ["market_analyst_model"] = team[Role.MarketAnalyst],
                ["sentiment_analyst_model"] = team[Role.SentimentAnalyst],
                ["strategist_model"] = team[Role.Strategist],
                ["risk_manager_model"] = team[Role.RiskManager],
            },
            file.Rows[1]);

        // One that has no orders says neither how much it held nor what it was closed at, and
        // one whose result is not recorded says no result: an empty cell, and never a nought.
        var waiting = file.Rows[0];

        Assert.Equal(
            (string.Empty, "100000", string.Empty, "2026-11-24T07:55:00Z", "2026-11-24T08:55:00Z", "TakeProfit"),
            (waiting["quantity"], waiting["entry_price"], waiting["exit_price"], waiting["opened_at"], waiting["closed_at"], waiting["exit_reason"]));
        Assert.Equal(
            (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "false", string.Empty),
            (waiting["net_result"], waiting["fees"], waiting["funding"], waiting["result_in_r"], waiting["held_seconds"], waiting["estimate"],
                waiting["entry_cycle_id"]));
        Assert.Equal(("Demo", team[Role.Strategist]), (waiting["mode"], waiting["strategist_model"]));
    }

    [Fact]
    public async Task The_file_of_the_decisions_has_a_row_for_each_recorded_cycle_with_every_roles_answer_and_the_Risk_Guard_result()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1), name: "Momentum");

        // A cycle whose plan was approved and placed its orders, one that its Risk Manager
        // vetoed, and one that its user ran as a test.
        var approved = await application.GiveCycleAsync(ada.Id, agent, Morning.AddHours(-4));
        var vetoed = await application.GiveCycleAsync(ada.Id, agent, Morning.AddHours(-3), CycleOutcome.Vetoed, orders: false);
        var test = await application.GiveCycleAsync(ada.Id, agent, Morning.AddHours(-2), orders: false, kind: CycleKind.Test);

        // And one that kept no data package and no call, whose plan the Risk Guard rejected.
        var rejected = Guid.CreateVersion7();
        var rejectedAt = Morning.AddHours(-1);

        await application.CycleRecords.StoreAsync(
            new DecisionCycle(
                rejected, agent, ada.Id, TradingMode.Demo, rejectedAt, rejectedAt.AddSeconds(2), rejectedAt.AddSeconds(40), CycleOutcome.Approved),
            [],
            [GuardedCycles.Check(rejected, ada.Id, rejectedAt.AddSeconds(39), GuardCases.OpenLong with { GlobalHalt = true }, PlanAction.OpenLong)],
            Cancellation);

        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        var file = await FileAsync(client, Decisions);

        Assert.Equal(
            "cycle_id,agent_id,agent,mode,kind,market,symbol,timeframe,candle_closed_at,started_at,ended_at,outcome,no_entry_reason," +
            "market_analyst_model,market_analyst_call,market_analyst_answer," +
            "sentiment_analyst_model,sentiment_analyst_call,sentiment_analyst_answer," +
            "strategist_model,strategist_call,strategist_answer," +
            "risk_manager_model,risk_manager_call,risk_manager_answer," +
            "risk_guard_verdicts,risk_guard_failed_rules,risk_guard_quantity,risk_guard_leverage,orders",
            string.Join(',', file.Columns));

        // Latest close first, each once.
        Assert.Equal(
            new[] { rejected, test, vetoed, approved }.Select(id => id.ToString()),
            file.Rows.Select(row => row["cycle_id"]));

        var record = await application.RecordedCycles.FindAsync(ada.Id, agent, approved, Cancellation);

        Assert.NotNull(record);

        var team = record.Calls.ToDictionary(call => call.Role, call => $"{call.ProviderName} {call.ModelIdentifier}");
        var check = Assert.Single(record.Checks);

        // The cycle as its timeline shows it: what it was for, how it ended, the model and the
        // answer of every role as it was stored, what the Risk Guard made of the plan, and the
        // orders that it placed, each as it is now.
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["cycle_id"] = approved.ToString(),
                ["agent_id"] = agent.ToString(),
                ["agent"] = "Momentum",
                ["mode"] = "Demo",
                ["kind"] = "Scheduled",
                ["market"] = "Spot",
                ["symbol"] = "BTCUSDT",
                ["timeframe"] = "1H",
                ["candle_closed_at"] = "2026-11-24T05:00:00Z",
                ["started_at"] = "2026-11-24T05:00:02Z",
                ["ended_at"] = "2026-11-24T05:00:40Z",
                ["outcome"] = "Approved",
                ["no_entry_reason"] = string.Empty,
                ["market_analyst_model"] = team[Role.MarketAnalyst],
                ["market_analyst_call"] = "Succeeded",
                ["market_analyst_answer"] = TeamAnswers.Of(Role.MarketAnalyst),
                ["sentiment_analyst_model"] = team[Role.SentimentAnalyst],
                ["sentiment_analyst_call"] = "Succeeded",
                ["sentiment_analyst_answer"] = TeamAnswers.Of(Role.SentimentAnalyst),
                ["strategist_model"] = team[Role.Strategist],
                ["strategist_call"] = "Succeeded",
                ["strategist_answer"] = TeamAnswers.Of(Role.Strategist),
                ["risk_manager_model"] = team[Role.RiskManager],
                ["risk_manager_call"] = "Succeeded",
                ["risk_manager_answer"] = TeamAnswers.Of(Role.RiskManager),
                ["risk_guard_verdicts"] = "Accepted",
                ["risk_guard_failed_rules"] = string.Empty,
                ["risk_guard_quantity"] = CsvText.Number(check.Quantity),
                ["risk_guard_leverage"] = CsvText.Number((long?)check.Leverage),
                ["orders"] = "Entry:Filled;StopLoss:Filled",
            },
            file.Rows[3]);

        // An order size was computed for the plan, so the cells are not empty for nothing.
        Assert.NotNull(check.Quantity);
        Assert.NotEqual(string.Empty, file.Rows[3]["risk_guard_quantity"]);

        // A cycle that ended with a veto says so, and placed nothing.
        Assert.Equal(("Vetoed", "Scheduled", string.Empty), (file.Rows[2]["outcome"], file.Rows[2]["kind"], file.Rows[2]["orders"]));

        // A test cycle is among them, and says that it is one (FR-CYC-12).
        Assert.Equal(("Test", "Demo", string.Empty), (file.Rows[1]["kind"], file.Rows[1]["mode"], file.Rows[1]["orders"]));

        // A cycle that kept no data package says nothing of what it was for, and one that made
        // no call has no answer. What the Risk Guard rejected a plan for is named by its rule.
        var guarded = file.Rows[0];

        Assert.Equal((string.Empty, string.Empty, string.Empty), (guarded["market"], guarded["symbol"], guarded["timeframe"]));
        Assert.Equal(
            (string.Empty, string.Empty, string.Empty),
            (guarded["strategist_model"], guarded["strategist_call"], guarded["strategist_answer"]));
        Assert.Equal(("Rejected", "NoGlobalHalt:Failed", string.Empty), (guarded["risk_guard_verdicts"], guarded["risk_guard_failed_rules"], guarded["orders"]));
    }

    [Fact]
    public async Task Each_download_is_a_CSV_file_to_keep_that_no_browser_stores_and_the_account_page_and_the_dashboard_lead_to_it()
    {
        using var application = Application();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        foreach (var (path, name, columns) in new[]
        {
            (Trades, "trades-2026-11-24.csv", ExportFiles.TradeColumns),
            (Decisions, "decisions-2026-11-24.csv", ExportFiles.DecisionColumns),
        })
        {
            using var answer = await client.GetAsync(path, Cancellation);
            var bytes = await answer.Content.ReadAsByteArrayAsync(Cancellation);

            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.Equal("text/csv", answer.Content.Headers.ContentType?.MediaType);
            Assert.Equal("utf-8", answer.Content.Headers.ContentType?.CharSet);
            Assert.Equal("attachment", answer.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal(name, answer.Content.Headers.ContentDisposition?.FileName);
            Assert.True(answer.Headers.CacheControl?.NoStore, "What a user traded and decided is not to be kept by a browser or a proxy.");

            // The mark of UTF-8, so that a spreadsheet reads a name in any script right, and
            // then the line of the columns alone: a user who has traded nothing has a file all the same.
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            Assert.Equal($"{string.Join(',', columns)}\r\n", Encoding.UTF8.GetString(bytes[3..]));
        }

        // Each as a download: the browser asks for the file once and does not try to show it as a page.
        var account = await client.PageAsync("/account");
        var dashboard = await client.PageAsync(WebUiModule.DashboardPath);

        foreach (var page in new[] { account, dashboard })
        {
            Assert.Contains("<a href=\"/export/trades\" download>Download your trades (CSV)</a>", page);
            Assert.Contains("<a href=\"/export/decisions\" download>Download your decisions (CSV)</a>", page);
            Assert.Contains("each row says whether it is of Demo or of Live", page);
        }
    }

    [Fact]
    [ProvesOwnershipOf(
        "decision_cycles", "orders", "position_results", "position_roles", "positions", "risk_check_rules", "risk_checks", "role_outputs")]
    public async Task The_files_hold_only_what_is_of_the_user_who_is_signed_in_and_asking_for_them_writes_nothing()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var ofAda = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1), name: "Of Ada");
        var ofGrace = await application.GiveAgentAsync(grace.Id, Morning.AddDays(-1), name: "Of Grace");

        // Grace has more of everything than Ada.
        var adasCycle = await application.GiveCycleAsync(ada.Id, ofAda, Morning.AddHours(-3));
        var gracesCycle = await application.GiveCycleAsync(grace.Id, ofGrace, Morning.AddHours(-2));
        var gracesTest = await application.GiveCycleAsync(grace.Id, ofGrace, Morning.AddHours(-1), orders: false, kind: CycleKind.Test);
        var gracesTrade = await application.GiveTradeAsync(grace.Id, ofGrace, Morning.AddMinutes(-5));

        using var adasBrowser = application.CreateClient();
        using var gracesBrowser = application.CreateClient();
        await adasBrowser.SignInAsync(Ada);
        await gracesBrowser.SignInAsync(Grace);

        var adasBefore = application.RowsOf(ada.Id);
        var gracesBefore = application.RowsOf(grace.Id);

        var adasTrades = await FileAsync(adasBrowser, Trades);
        var adasDecisions = await FileAsync(adasBrowser, Decisions);
        var gracesTrades = await FileAsync(gracesBrowser, Trades);
        var gracesDecisions = await FileAsync(gracesBrowser, Decisions);

        // Each has their own, and all of it.
        Assert.Equal(["Of Ada"], adasTrades.Rows.Select(row => row["agent"]));
        Assert.Equal([adasCycle.ToString()], adasDecisions.Rows.Select(row => row["cycle_id"]));
        Assert.Equal([gracesTrade.ToString(), null], gracesTrades.Rows.Select(row => row["position_id"] == gracesTrade.ToString() ? row["position_id"] : null));
        Assert.Equal([gracesTest.ToString(), gracesCycle.ToString()], gracesDecisions.Rows.Select(row => row["cycle_id"]));

        // And nothing in Ada's is Grace's: not an agent, not its name, not a cycle, and not a position (NFR-SEC-3).
        foreach (var text in new[] { adasTrades.Text, adasDecisions.Text })
        {
            Assert.DoesNotContain("Grace", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(grace.Id.ToString(), text);
            Assert.DoesNotContain(ofGrace.ToString(), text);
            Assert.DoesNotContain(gracesCycle.ToString(), text);
            Assert.DoesNotContain(gracesTest.ToString(), text);
            Assert.All(gracesTrades.Rows, row => Assert.DoesNotContain(row["position_id"], text));
        }

        // A file is of whoever is signed in, whatever its address names.
        var asked = await FileAsync(adasBrowser, $"{Trades}?userId={grace.Id}&agent={ofGrace}");

        Assert.Equal(adasTrades.Rows.Select(row => row["position_id"]), asked.Rows.Select(row => row["position_id"]));

        // It only reads.
        Assert.Equal(adasBefore, application.RowsOf(ada.Id));
        Assert.Equal(gracesBefore, application.RowsOf(grace.Id));
    }

    [Theory]
    [InlineData(Trades, "%2Fexport%2Ftrades")]
    [InlineData(Decisions, "%2Fexport%2Fdecisions")]
    public async Task A_visitor_is_sent_to_sign_in_and_is_given_nothing(string path, string returnUrl)
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1), name: "Momentum");
        var cycle = await application.GiveCycleAsync(ada.Id, agent, Morning.AddHours(-2));

        using var visitor = application.CreateClient();

        var (asked, page) = await visitor.OpenAsync(path);

        Assert.Equal($"{SessionAuthentication.SignInPath}?returnUrl={returnUrl}", asked);
        Assert.DoesNotContain("Momentum", page);
        Assert.DoesNotContain(cycle.ToString(), page);
        Assert.DoesNotContain("position_id", page);
        Assert.DoesNotContain("cycle_id", page);
    }

    [Fact]
    public async Task Neither_file_holds_a_secret()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (ada, app, recoveryCodes) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        using var phone = application.CreateClient();
        await client.SignInAsync(Ada);
        await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));

        // Everything that the account can have of secrets is there: a session, a sign-in that
        // waits for a code, a reset link that is out, the key of two-factor authentication, the
        // recovery codes, and a key of an AI provider and of Binance. And the agent that uses
        // the keys has traded and decided.
        await phone.SignInAsync(Ada);
        await application.RequestResetAsync(Ada);
        var resetToken = AccountsApplication.TokenIn(application.Email.Sent[^1]);
        await application.GiveKeysAsync(ada.Id, clock.GetUtcNow());
        var agent = await application.GiveAgentAsync(ada.Id, clock.GetUtcNow(), name: "Momentum");
        await application.GiveCycleAsync(ada.Id, agent, Morning.AddHours(-2));

        var user = StoredUser.Of(application.Database, Ada)!;
        var twoFactor = StoredTwoFactor.Of(application.Database, Ada);
        byte[][] stored =
        [
            twoFactor.Secret!,
            app.Key,
            user.ResetTokenHash!,
            .. StoredSession.All(application.Database).Select(session => session.TokenHash),
            .. StoredPendingSignIn.All(application.Database).Select(pending => pending.TokenHash),
            .. StoredRecoveryCode.All(application.Database).Select(code => code.CodeHash),
            .. StoredAiCredential.All(application.Database).Select(key => key.EncryptedValue),
            .. StoredExchangeCredential.All(application.Database).Select(key => key.EncryptedValue),
        ];
        Assert.Equal(4 + 1 + TwoFactorRules.RecoveryCodeCount + 2, stored.Length);

        // The keys as their user typed them, which nothing stores as they are.
        var owner = ada.Id.ToString("N");
        string[] typed = [Keys.Of(owner, "claude"), Keys.Of(owner, "demo"), Keys.Of(owner, "seal")];

        foreach (var path in new[] { Trades, Decisions })
        {
            var file = await FileAsync(client, path);

            Assert.Single(file.Rows);

            // Nothing that would help someone in, in any of the forms that bytes are written in.
            Assert.DoesNotContain(user.PasswordHash, file.Text);
            Assert.DoesNotContain("argon2", file.Text, StringComparison.OrdinalIgnoreCase);
            Assert.All(stored, bytes =>
            {
                Assert.DoesNotContain(Convert.ToBase64String(bytes), file.Text);
                Assert.DoesNotContain(Base64Url.EncodeToString(bytes), file.Text);
                Assert.DoesNotContain(Convert.ToHexString(bytes), file.Text, StringComparison.OrdinalIgnoreCase);
            });
            Assert.DoesNotContain(app.KeyText, file.Text);
            Assert.DoesNotContain(resetToken, file.Text);
            Assert.All(recoveryCodes, code => Assert.DoesNotContain(code, file.Text));
            Assert.All(typed, key => Assert.DoesNotContain(key, file.Text));
            Assert.All(typed, key => Assert.DoesNotContain(key[^4..], file.Columns));

            // Nor a column of anything of that kind, nor of who the user is: what a file holds
            // is what the other tests list.
            Assert.All(file.Columns, column =>
            {
                Assert.DoesNotContain("hash", column, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("secret", column, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("token", column, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("key", column, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("password", column, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("email", column, StringComparison.OrdinalIgnoreCase);
            });
            Assert.DoesNotContain("example.com", file.Text);
            Assert.DoesNotContain(ada.Id.ToString(), file.Text);
        }
    }

    [Fact]
    public async Task Each_row_says_the_mode_that_it_was_of_and_a_row_of_Demo_stays_labeled_as_Demo_when_its_agent_is_switched_to_Live()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1), name: "Momentum");

        // What it did and decided in Demo.
        var inDemo = await application.GiveCycleAsync(ada.Id, agent, Morning.AddHours(-3));

        // Its user switches it to Live, and it trades and decides again.
        await using (var context = await application.Contexts.CreateDbContextAsync(Cancellation))
        {
            await context.Set<Agent>()
                .Where(stored => stored.Id == agent)
                .ExecuteUpdateAsync(row => row.SetProperty(stored => stored.Mode, TradingMode.Live), Cancellation);
        }

        var inLive = await application.GiveCycleAsync(ada.Id, agent, Morning.AddHours(-1));

        // And an agent that was always in Demo, beside it.
        var other = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1), name: "Practice");
        var ofOther = await application.GiveCycleAsync(ada.Id, other, Morning.AddHours(-2));

        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        var trades = await FileAsync(client, Trades);
        var decisions = await FileAsync(client, Decisions);

        // A row is of the mode that its agent was in then, whatever mode the agent is in now (FR-BIN-11).
        Assert.Equal(
            [(inLive.ToString(), "Momentum", "Live"), (ofOther.ToString(), "Practice", "Demo"), (inDemo.ToString(), "Momentum", "Demo")],
            decisions.Rows.Select(row => (row["cycle_id"], row["agent"], row["mode"])));
        Assert.Equal(
            [(inLive.ToString(), "Momentum", "Live"), (ofOther.ToString(), "Practice", "Demo"), (inDemo.ToString(), "Momentum", "Demo")],
            trades.Rows.Select(row => (row["entry_cycle_id"], row["agent"], row["mode"])));

        // No row is without the word, and a file adds nothing up: it has a row for each trade
        // and each decision, and no row of a total that would combine the two modes.
        Assert.All(trades.Rows.Concat(decisions.Rows), row => Assert.Contains(row["mode"], new[] { "Demo", "Live" }));
        Assert.Equal((3, 3), (trades.Rows.Count, decisions.Rows.Count));
    }

    [Fact]
    public async Task A_name_that_a_spreadsheet_would_work_out_is_written_as_words_and_one_with_a_comma_stays_one_cell()
    {
        const string Formula = "=HYPERLINK(\"https://example.org/steal\",\"Open me\"), Inc.";

        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1), name: Formula);
        await application.GiveCycleAsync(ada.Id, agent, Morning.AddHours(-2));

        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        foreach (var path in new[] { Trades, Decisions })
        {
            var file = await FileAsync(client, path);
            var row = Assert.Single(file.Rows);

            // An apostrophe before it, which a spreadsheet shows as text, and the name after it as it is.
            Assert.Equal($"'{Formula}", row["agent"]);

            // In the file it stands in quotes, with each quote of it written twice.
            Assert.Contains(",\"'=HYPERLINK(\"\"https://example.org/steal\"\",\"\"Open me\"\"), Inc.\",Demo,", file.Text);
        }
    }

    [Fact]
    public async Task More_rows_than_are_read_at_a_time_are_each_in_the_file_once_latest_first()
    {
        // More than one part, with rows of the same moment on both sides of where a part ends.
        const int Count = IExportedRecords.ChunkSize + 4;

        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1), name: "Momentum");

        var positions = Enumerable.Range(0, Count)
            .Select(index => new Position(
                Guid.CreateVersion7(),
                agent,
                ada.Id,
                TradingMode.Demo,
                Market.Spot,
                "BTCUSDT",
                "USDT",
                Timeframe.OneHour,
                PositionSide.Long,
                entryPrice: 100_000m,
                quantity: 0.01m,
                stopLoss: 99_000m,
                takeProfit: null,
                openedAt: Morning.AddDays(-2),
                closedAt: Morning.AddMinutes(-(index / 3)),
                ExitReason.TakeProfit,
                alertedAt: null,
                fundingDueAt: null))
            .ToList();
        var cycles = Enumerable.Range(0, Count)
            .Select(index => new DecisionCycle(
                Guid.CreateVersion7(),
                agent,
                ada.Id,
                TradingMode.Demo,
                closedAt: Morning.AddMinutes(-(index / 3)),
                startedAt: Morning.AddMinutes(-(index / 3)).AddSeconds(2),
                endedAt: Morning.AddMinutes(-(index / 3)).AddSeconds(40),
                CycleOutcome.Hold))
            .ToList();

        await using (var context = await application.Contexts.CreateDbContextAsync(Cancellation))
        {
            context.AddRange(positions);
            context.AddRange(cycles);
            await context.SaveChangesAsync(Cancellation);
        }

        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        var trades = await FileAsync(client, Trades);
        var decisions = await FileAsync(client, Decisions);

        foreach (var (file, id, at, planted) in new[]
        {
            (trades, "position_id", "closed_at", positions.Select(position => position.Id.ToString())),
            (decisions, "cycle_id", "candle_closed_at", cycles.Select(cycle => cycle.Id.ToString())),
        })
        {
            // Every one of them, and none twice.
            Assert.Equal(Count, file.Rows.Count);
            Assert.Equal(planted.Order(StringComparer.Ordinal), file.Rows.Select(row => row[id]).Order(StringComparer.Ordinal));

            // Latest first: a moment in this form is in the order of time as words are in the order of the alphabet.
            var moments = file.Rows.Select(row => row[at]).ToList();

            Assert.Equal(moments.OrderDescending(StringComparer.Ordinal), moments);
            Assert.Equal(("2026-11-24T09:00:00Z", CsvText.Time(Morning.AddMinutes(-((Count - 1) / 3)))), (moments[0], moments[^1]));
        }
    }

    [Fact]
    public async Task The_log_says_who_exported_what_by_id_and_holds_nothing_of_it()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-1), name: "Momentum");
        var cycle = await application.GiveCycleAsync(ada.Id, agent, Morning.AddHours(-2));

        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        Assert.Contains("Momentum", (await FileAsync(client, Trades)).Text);
        Assert.Contains(cycle.ToString(), (await FileAsync(client, Decisions)).Text);

        foreach (var template in new[] { "User {UserId} exported their trades", "User {UserId} exported their decisions" })
        {
            var exported = Assert.Single(application.Events(), written => written.Template == template);

            Assert.Equal("Ecnanib.DecisionEngine.ExportedRecords", exported.Area);
            Assert.Equal(ada.Id.ToString(), exported.Text("UserId"));
        }

        var log = application.LogText();

        Assert.DoesNotContain("Momentum", log);
        Assert.DoesNotContain("example.com", log);
        Assert.DoesNotContain(TeamAnswers.MarketReport, log);
    }

    private static AccountsApplication Application() => new(clock: new TestClock(Morning));

    /// <summary>Asks for a file as a browser that is signed in does, and reads it as a program reads CSV.</summary>
    private static async Task<CsvFile> FileAsync(HttpClient client, string path)
    {
        // Without the mark of UTF-8 at its start, which is no part of the first column.
        var text = (await client.PageAsync(path)).TrimStart('﻿');
        var lines = Lines(text);

        Assert.NotEmpty(lines);

        var columns = lines[0];

        Assert.Equal(columns.Length, columns.Distinct().Count());

        return new CsvFile(
            text,
            columns,
            [
                .. lines.Skip(1).Select(cells =>
                {
                    // Every line has a cell for every column.
                    Assert.Equal(columns.Length, cells.Length);

                    return columns.Zip(cells).ToDictionary(cell => cell.First, cell => cell.Second);
                }),
            ]);
    }

    /// <summary>
    /// The lines of a CSV file, each as its cells, by the rules that a program reads one by
    /// (RFC 4180): a cell in quotes may hold a comma and the end of a line, and a quote that is
    /// written twice in it is one quote.
    /// </summary>
    private static List<string[]> Lines(string text)
    {
        var lines = new List<string[]>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (quoted)
            {
                if (character != '"')
                {
                    cell.Append(character);
                }
                else if (index + 1 < text.Length && text[index + 1] == '"')
                {
                    cell.Append('"');
                    index++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (character == '"')
            {
                quoted = true;
            }
            else if (character == ',')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                cells.Add(cell.ToString());
                cell.Clear();
                lines.Add([.. cells]);
                cells.Clear();
                index++;
            }
            else
            {
                // The end of a line is a carriage return and a line feed, and neither stands alone outside quotes.
                Assert.NotEqual('\n', character);
                Assert.NotEqual('\r', character);
                cell.Append(character);
            }
        }

        // The last line ends as every line does, and no quote is left open.
        Assert.False(quoted);
        Assert.Empty(cells);
        Assert.Equal(0, cell.Length);

        return lines;
    }

    /// <summary>A file as it was read.</summary>
    /// <param name="Text">All of it, as text.</param>
    /// <param name="Columns">The names of its columns, in their order.</param>
    /// <param name="Rows">Its rows, each as the cell of each column.</param>
    private sealed record CsvFile(string Text, string[] Columns, IReadOnlyList<Dictionary<string, string>> Rows);
}
