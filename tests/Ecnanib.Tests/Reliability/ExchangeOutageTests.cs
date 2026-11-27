using Ecnanib.ExchangeGateway;
using Ecnanib.Notifications;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.DecisionEngine;
using Ecnanib.Tests.RiskGuard;

namespace Ecnanib.Tests.Reliability;

/// <summary>
/// A Binance outage pauses entries and raises an alert (NFR-REL-4), from one end to the other:
/// the gateway's own watch hears of the calls that Binance leaves without an answer, what places
/// the orders asks it, and the users whose agents run there are told. See "A Binance outage" in
/// docs/reliability.md.
/// </summary>
public class ExchangeOutageTests
{
    private const string NotEntered = "No entry was placed for agent {AgentId} for the close at {ClosedAt}: {NoEntryReason}";

    [Fact]
    public async Task While_binance_is_out_no_entry_is_placed_its_users_are_told_and_entries_go_on_when_it_answers()
    {
        // What places the orders, on a Binance of the test's, with the gateway's own watch on
        // outages. In the file of this test three calls in a row without an answer are one.
        using var application = new OrdersApplication(watchesOutages: true, settings: file => file.With("Binance:OutageFailures", 3));
        var agent = await application.GiveAgentAsync();
        var outages = application.Application.ExchangeOutages;
        var down = true;

        application.Binance.Instead = _ => down ? throw new HttpRequestException("The connection was refused.") : null;

        // Three reads of market data, some seconds apart, as the cycles of a close make them:
        // the candles of three lengths. Binance leaves each without an answer.
        var lengths = new[] { CandleInterval.FifteenMinutes, CandleInterval.OneHour, CandleInterval.FourHours };

        for (var read = 0; read < lengths.Length; read++)
        {
            application.Clock.Set(OrdersApplication.Start.AddSeconds(10 * read));

            var candles = await application.Application.MarketData.GetCandlesAsync(
                Market.Spot, TradingMode.Demo, "BTCUSDT", lengths[read], TestContext.Current.CancellationToken);

            Assert.Equal(ExchangeCallOutcome.Failed, candles.Outcome);
        }

        // That was the third call in a row without an answer: Binance is out on Spot in Demo,
        // where the agent trades, and on no other market.
        var began = OrdersApplication.Start.AddSeconds(20);

        Assert.True(outages.EntriesPaused(Market.Spot, TradingMode.Demo));
        Assert.Equal(
            [(Market.Spot, TradingMode.Demo)],
            outages.Answers().Where(answers => answers.EntriesPaused).Select(answers => (answers.Market, answers.Environment)));
        Assert.Equal(began, outages.Answers().Single(answers => answers.EntriesPaused).OutageSince);

        // A plan that the Risk Guard accepted is not entered for the outage, and nothing is
        // asked of Binance for it.
        var sent = application.Binance.System.Calls.Count;

        application.Clock.Set(began.AddSeconds(30));
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        Assert.Equal(["ExchangeOutage"], Reasons(application));
        Assert.Empty(application.Binance.Asked);
        Assert.Equal(sent, application.Binance.System.Calls.Count);

        // A minute and a half into the outage its user is told, once.
        application.Clock.Set(began.AddSeconds(89));
        await application.Application.TellOfOutagesAsync();

        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));

        application.Clock.Set(began.AddSeconds(90));
        await application.Application.TellOfOutagesAsync();
        await application.Application.TellOfOutagesAsync();

        var told = Assert.Single(await application.NotificationsOfAsync(agent.UserId));

        Assert.Equal((NotificationType.ExchangeOutage, null, TradingMode.Demo), (told.Type, told.AgentId, told.Mode));
        Assert.StartsWith("Binance has not answered on Spot (Demo) since 10:15 UTC.", told.Content, StringComparison.Ordinal);

        // Binance is back. A read that is made anyway finds it, as the reading of its clocks
        // does every minute: the first answer ends the outage, and the user is told that too.
        down = false;
        application.Clock.Set(began.AddMinutes(5));

        var answered = await application.Application.MarketData.GetCandlesAsync(
            Market.Spot, TradingMode.Demo, "BTCUSDT", CandleInterval.OneDay, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeCallOutcome.Succeeded, answered.Outcome);

        Assert.False(outages.EntriesPaused(Market.Spot, TradingMode.Demo));

        await application.Application.TellOfOutagesAsync();

        var all = (await application.NotificationsOfAsync(agent.UserId)).OrderBy(said => said.CreatedAt).ToList();

        Assert.Equal(2, all.Count);
        Assert.All(all, said => Assert.Equal((NotificationType.ExchangeOutage, TradingMode.Demo), (said.Type, said.Mode)));
        Assert.StartsWith("Binance has not answered on Spot (Demo)", all[0].Content, StringComparison.Ordinal);
        Assert.StartsWith("Binance answers again on Spot (Demo).", all[1].Content, StringComparison.Ordinal);

        // And the same plan is entered, and protected by its pair of resting orders.
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        Assert.Equal(2, application.Binance.Asked.Count);
        Assert.True(Assert.Single(await application.PositionsOfAsync(agent.Id)).StopLoss > 0m);
    }

    /// <summary>Why each plan was not entered, in the order of the plans.</summary>
    private static IReadOnlyList<string?> Reasons(OrdersApplication application) =>
        [.. application.Application.Events().Where(said => said.Template == NotEntered).Select(said => said.Text("NoEntryReason"))];
}
