using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// The rules about agents that hold whatever page or job is at work. A user has at most one
/// active agent, in any state other than Draft or Stopped, for each combination of Binance
/// credential, market, and symbol (FR-AGT-9): the database holds to it. The timeframe and the
/// market of an agent are named as what goes by them names them (FR-AGT-3, FR-AGT-4).
/// </summary>
public class AgentRuleTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AgentState.Draft, false)]
    [InlineData(AgentState.Running, true)]
    [InlineData(AgentState.Paused, true)]
    [InlineData(AgentState.Halted, true)]
    [InlineData(AgentState.AttentionRequired, true)]
    [InlineData(AgentState.Stopped, false)]
    public void An_agent_is_active_in_every_state_but_Draft_and_Stopped(AgentState state, bool active)
    {
        Assert.Equal(active, AgentStates.IsActive(state));

        // Its market, symbol, and timeframe change only while it is a draft, or is stopped
        // with nothing open (FR-AGT-8): never while it is active, whatever it holds.
        Assert.Equal(state is AgentState.Draft or AgentState.Stopped, AgentStates.StructureCanChange(state, holdsPositionOrOrders: false));
        Assert.Equal(state is AgentState.Draft, AgentStates.StructureCanChange(state, holdsPositionOrOrders: true));
    }

    [Fact]
    public void The_states_are_the_ones_of_the_specification()
    {
        // Pinned, as SRS 3.3 lists them: a state that is added is active until it is said not
        // to be, here and in the index that holds a user to one active agent.
        Assert.Equal(
            ["Draft", "Running", "Paused", "Halted", "AttentionRequired", "Stopped"],
            Enum.GetNames<AgentState>());
    }

    [Fact]
    public async Task A_user_has_at_most_one_active_agent_for_each_credential_market_and_symbol()
    {
        using var application = new AccountsApplication();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);

        // The index holds whatever changes a state, so the test stores the rows itself, past
        // what starts an agent, which asks first. An agent runs on Spot in Demo with BTCUSDT.
        await application.GiveAgentAsync(ada.Id, Morning, name: "Running", state: AgentState.Running);

        // Its user has any number of drafts and stopped agents for the same three.
        await application.GiveAgentAsync(ada.Id, Morning, name: "Draft", state: AgentState.Draft);
        await application.GiveAgentAsync(ada.Id, Morning, name: "Another draft", state: AgentState.Draft);
        await application.GiveAgentAsync(ada.Id, Morning, name: "Stopped", state: AgentState.Stopped);
        await application.GiveAgentAsync(ada.Id, Morning, name: "Stopped earlier", state: AgentState.Stopped);

        // A second active one is refused by the database, in whichever state it is active.
        foreach (var state in new[] { AgentState.Running, AgentState.Paused, AgentState.Halted, AgentState.AttentionRequired })
        {
            var refused = await Assert.ThrowsAsync<DbUpdateException>(
                () => application.GiveAgentAsync(ada.Id, Morning, name: $"Also {state}", state: state));

            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(refused.InnerException).SqlState);
        }

        // So is a draft that is made active while the other still is.
        var draft = StoredAgent.Named(application.Database, "Draft");
        var started = Assert.Throws<PostgresException>(
            () => application.Database.Execute($"UPDATE agents SET state = 'Running' WHERE id = '{draft.Id}'"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, started.SqlState);

        // Another symbol, another market, or the credential of the other environment is another combination.
        await application.GiveAgentAsync(ada.Id, Morning, name: "Other symbol", state: AgentState.Running, symbol: "SOLUSDT");
        await application.GiveAgentAsync(ada.Id, Morning, name: "Other market", state: AgentState.Paused, market: Market.Futures);
        await application.GiveAgentAsync(ada.Id, Morning, name: "Other credential", state: AgentState.Running, mode: TradingMode.Live);

        // And another user has a credential of their own.
        await application.GiveAgentAsync(grace.Id, Morning, name: "Running", state: AgentState.Running);

        // Once the active agent is stopped, another takes its place.
        var running = StoredAgent.Of(application.Database, ada.Id).Single(agent => agent.Name == "Running");
        application.Database.Execute($"UPDATE agents SET state = 'Stopped' WHERE id = '{running.Id}'");
        application.Database.Execute($"UPDATE agents SET state = 'Running' WHERE id = '{draft.Id}'");

        Assert.Equal(
            ["Draft", "Other credential", "Other market", "Other symbol"],
            StoredAgent.Of(application.Database, ada.Id)
                .Where(agent => agent.State is not ("Draft" or "Stopped"))
                .Select(agent => agent.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_database_holds_a_user_to_one_agent_of_a_name()
    {
        using var application = new AccountsApplication();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);

        await application.GiveAgentAsync(ada.Id, Morning, name: "Swing");

        // The pages ask before they store. Whatever stores an agent without asking is refused
        // by the database (FR-AGT-1).
        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => application.GiveAgentAsync(ada.Id, Morning, name: "Swing"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(refused.InnerException).SqlState);

        await application.GiveAgentAsync(grace.Id, Morning, name: "Swing");

        Assert.Equal(2, StoredAgent.All(application.Database).Count);
    }

    [Fact]
    public void The_timeframes_are_named_as_what_goes_by_them_names_them()
    {
        // The four of the specification, by their codes (FR-AGT-3).
        Assert.Equal(["15m", "1H", "4H", "1D"], Timeframes.All.Select(timeframe => timeframe.Code()));
        Assert.All(Timeframes.All, timeframe => Assert.Equal(timeframe, Timeframes.WithCode(timeframe.Code())));
        Assert.All(new[] { null, "", "1h", "15M", "1W", "OneHour", " 1H" }, code => Assert.Null(Timeframes.WithCode(code)));

        // The cycle schedule and the data window: the gateway reads candles of each of them,
        // under the same name. It reads the week as well, for context alone.
        Assert.Equal(
            [.. Enum.GetNames<Timeframe>(), nameof(CandleInterval.OneWeek)],
            Enum.GetNames<CandleInterval>());

        // The stale limit of each is in the configuration file, under the same name.
        Assert.Equal(
            Enum.GetNames<Timeframe>(),
            typeof(StaleSecondsSettings).GetProperties().Select(property => property.Name));

        // The prompt variant: a template is seeded for each, under its code.
        Assert.Equal(
            Timeframes.All.Select(timeframe => timeframe.Code()).Order(StringComparer.Ordinal),
            SeededSubjectsOf(PromptTemplateKind.Timeframe));
    }

    [Fact]
    public void The_markets_are_named_as_what_goes_by_them_names_them()
    {
        // The exchange interface: the gateway takes the market of an agent as it is, for every
        // call. The prompt variant, which says which actions the market allows: a template is
        // seeded for each market, under its name (FR-AGT-4).
        Assert.Equal(["Spot", "Futures"], Enum.GetNames<Market>());
        Assert.Equal(Enum.GetNames<Market>().Order(StringComparer.Ordinal), SeededSubjectsOf(PromptTemplateKind.Market));
    }

    /// <summary>
    /// The subjects that the seeded prompt templates of a kind are for, in order: of the
    /// templates in use, since a template that was edited is seeded in every version it had.
    /// </summary>
    private static IReadOnlyList<string> SeededSubjectsOf(PromptTemplateKind kind)
    {
        using var context = ModelOnly.Context();

        // The rows that are seeded are in the model as it is designed, and not in the one that
        // the application reads at run time.
        var model = context.GetService<IDesignTimeModel>().Model;

        return
        [
            .. model.FindEntityType(typeof(PromptTemplate))!.GetSeedData()
                .Where(row => (PromptTemplateKind)row[nameof(PromptTemplate.Kind)]! == kind)
                .Where(row => (bool)row[nameof(PromptTemplate.IsActive)]!)
                .Select(row => (string)row[nameof(PromptTemplate.Subject)]!)
                .Order(StringComparer.Ordinal),
        ];
    }
}
