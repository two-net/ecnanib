using System.Collections;
using System.Reflection;
using Ecnanib.Experience;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// A platform-wide board shows the same track record aggregated across all users. A slice is
/// shown only when at least 5 users and 50 trades contribute. It exposes no user identity,
/// balance, or individual trade, and users can opt out of contributing (FR-EXP-10). See "The
/// platform-wide board" in docs/experience.md.
/// </summary>
/// <remarks>
/// The file of these tests takes 3 users and 6 trades as what must contribute, and 3 trades as
/// the minimum sample, so that a test has enough of them with a few. The numbers of the
/// specification are held by <see cref="BoardSliceTests"/>. The clock stands at the morning of
/// the tests, and the trades were closed before it.
/// </remarks>
public class BoardRecordsTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string Gemini = "gemini-test-model";
    private const string OtherClaude = "claude-other-model";

    // Between two hours, so that the job that checks the stored keys is not due at the start.
    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 9, 20, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_board_shows_the_same_track_record_of_the_trades_of_all_users()
    {
        using var application = Application();
        var team = BoardContributors.TeamOf(await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning));

        // Three users with two trades each of the same team. Those of the first made 2 R each,
        // those of the second lost 1 R each, and those of the third made 1 R each.
        await application.GiveContributorsAsync(users: 1, tradesEach: 2, Morning.AddHours(-3), team, resultInR: 2m, first: 1);
        await application.GiveContributorsAsync(users: 1, tradesEach: 2, Morning.AddHours(-2), team, resultInR: -1m, first: 2);
        await application.GiveContributorsAsync(users: 1, tradesEach: 2, Morning.AddHours(-1), team, resultInR: 1m, first: 3);

        var view = await application.BoardRecords.OfEveryoneAsync(TrackRecordFilter.None, Cancellation);

        var provider = Assert.Single(view.Providers);
        var model = Assert.Single(view.Models);

        Assert.Equal(("claude", "Claude"), (provider.Provider.Value, provider.ProviderName));
        Assert.Equal(StoredAgentRows.TeamModel, model.Model.Identifier);

        // It is the record that the same trades give anywhere: the measures are those of the
        // record of a user (FR-EXP-2), of the trades of all three.
        var trades = (await application.BoardTrades.OfContributorsAsync(null, Cancellation)).Select(trade => trade.Trade).ToList();

        Assert.Equal(6, trades.Count);
        Assert.Equal(TrackRecord.Of(trades, minSamples: 3), provider.Record);
        Assert.Equal(provider.Record, model.Record);

        // By hand: 4 of the 6 won, the total is 4 R, and it ran 2, 4, 3, 2, 3, 4, so it fell by
        // 2 R at the most. Each trade was held for an hour.
        Assert.Equal(
            (6, 4, 2, 4m, 2m, TimeSpan.FromHours(1), false),
            (provider.Record.Trades, provider.Record.Wins, provider.Record.Losses, provider.Record.TotalR,
                provider.Record.LargestDrawdownR, provider.Record.AverageHoldingTime, provider.Record.SmallSample));

        // It says its mode, that the rule chose it, and what must contribute, by the file.
        Assert.Equal(new BoardScope(TradingMode.Demo, ModeChosen: true, ClosedFrom: null, MinUsers: 3, MinTrades: 6), view.Scope);
    }

    [Fact]
    public async Task A_record_is_shown_only_when_enough_users_and_enough_trades_contribute_to_it()
    {
        using var application = Application();
        var claude = BoardContributors.TeamOf(await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning));
        var gemini = BoardContributors.TeamOf(await application.GiveModelAsync("gemini", Gemini, Morning));

        // Claude: three users with one trade each, which is enough users, and 3 trades of the 6.
        var ofClaude = await application.GiveContributorsAsync(users: 3, tradesEach: 1, Morning.AddHours(-5), claude);

        // Gemini: two users with three trades each, which is enough trades, and 2 users of the 3.
        await application.GiveContributorsAsync(users: 2, tradesEach: 3, Morning.AddHours(-4), gemini, first: 4);

        var view = await application.BoardRecords.OfEveryoneAsync(TrackRecordFilter.None, Cancellation);

        // Neither is shown, though five users and nine trades are on the board in all: each
        // record is held to the rule by itself.
        Assert.Empty(view.Providers);
        Assert.Empty(view.Models);

        // Three more trades of one of the three: 6 trades of 3 users.
        await application.GiveTradesAsync(ofClaude[0].User.Id, ofClaude[0].AgentId, count: 3, Morning.AddHours(-3), claude);

        view = await application.BoardRecords.OfEveryoneAsync(TrackRecordFilter.None, Cancellation);

        // The record of Claude is there, and nothing of the answer says that Gemini has one.
        Assert.Equal([("claude", 6, 6m)], Providers(view));
        Assert.Equal([(StoredAgentRows.TeamModel, 6, 6m)], Models(view));
    }

    [Fact]
    public async Task A_model_is_held_to_the_rule_by_itself_whatever_its_provider_shows()
    {
        using var application = Application();
        var one = BoardContributors.TeamOf(await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning));
        var other = BoardContributors.TeamOf(await application.GiveModelAsync("claude", OtherClaude, Morning));

        // Two models of Claude, each with two users who have two trades: Claude has 8 trades of
        // 4 users, and each of its models 4 trades of 2.
        await application.GiveContributorsAsync(users: 2, tradesEach: 2, Morning.AddHours(-5), one);
        await application.GiveContributorsAsync(users: 2, tradesEach: 2, Morning.AddHours(-4), other, first: 3);

        var view = await application.BoardRecords.OfEveryoneAsync(TrackRecordFilter.None, Cancellation);

        Assert.Equal([("claude", 8, 8m)], Providers(view));
        Assert.Empty(view.Models);

        // A third user of the other model: it has 6 trades of 3 users now, and the first still 4 of 2.
        await application.GiveContributorsAsync(users: 1, tradesEach: 2, Morning.AddHours(-3), other, first: 5);

        view = await application.BoardRecords.OfEveryoneAsync(TrackRecordFilter.None, Cancellation);

        Assert.Equal([("claude", 10, 10m)], Providers(view));
        Assert.Equal([(OtherClaude, 6, 6m)], Models(view));
    }

    [Fact]
    public async Task Demo_and_Live_are_never_added_up_and_the_board_says_which_it_shows()
    {
        using var application = Application();
        var team = BoardContributors.TeamOf(await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning));

        // Three users with one trade in each mode: three trades in Demo and three in Live, which
        // is six in all, and enough in neither.
        var contributors = await application.GiveContributorsAsync(users: 3, tradesEach: 1, Morning.AddHours(-6), team);

        foreach (var (user, agent) in contributors)
        {
            await application.GiveTradesAsync(user.Id, agent, count: 1, Morning.AddHours(-5), team, mode: TradingMode.Live);
        }

        var view = await application.BoardRecords.OfEveryoneAsync(TrackRecordFilter.None, Cancellation);

        // Told no mode, with nothing to show in Live, it is of Demo, and says that the rule chose it.
        Assert.Empty(view.Providers);
        Assert.Equal((TradingMode.Demo, true), (view.Scope.Mode, view.Scope.ModeChosen));

        // One more trade each in Demo: Demo has 6 trades of 3 users, and Live still 3.
        foreach (var (user, agent) in contributors)
        {
            await application.GiveTradesAsync(user.Id, agent, count: 1, Morning.AddHours(-4), team);
        }

        view = await application.BoardRecords.OfEveryoneAsync(TrackRecordFilter.None, Cancellation);

        Assert.Equal([("claude", 6, 6m)], Providers(view));
        Assert.Equal((TradingMode.Demo, true), (view.Scope.Mode, view.Scope.ModeChosen));

        // Asked for Live, it shows Live, which has nothing to show, and says that it was asked.
        var live = await application.BoardRecords.OfEveryoneAsync(TrackRecordFilter.None with { Mode = TradingMode.Live }, Cancellation);

        Assert.Empty(live.Providers);
        Assert.Empty(live.Models);
        Assert.Equal((TradingMode.Live, false), (live.Scope.Mode, live.Scope.ModeChosen));

        // One more trade each in Live, which lost: Live has a record to show, so the board shows
        // Live where it is told no mode (FR-EXP-9). The trades of Demo are in none of its sums.
        foreach (var (user, agent) in contributors)
        {
            await application.GiveTradesAsync(user.Id, agent, count: 1, Morning.AddHours(-3), team, resultInR: -1m, mode: TradingMode.Live);
        }

        view = await application.BoardRecords.OfEveryoneAsync(TrackRecordFilter.None, Cancellation);

        Assert.Equal([("claude", 6, 0m)], Providers(view));
        Assert.Equal((TradingMode.Live, true), (view.Scope.Mode, view.Scope.ModeChosen));

        var demo = await application.BoardRecords.OfEveryoneAsync(TrackRecordFilter.None with { Mode = TradingMode.Demo }, Cancellation);

        Assert.Equal([("claude", 6, 6m)], Providers(demo));
        Assert.Equal((TradingMode.Demo, false), (demo.Scope.Mode, demo.Scope.ModeChosen));
    }

    [Fact]
    public async Task The_filter_counts_the_trades_as_it_does_for_the_record_of_a_user()
    {
        using var application = Application();
        var claude = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);
        var gemini = await application.GiveModelAsync("gemini", Gemini, Morning);

        // On Spot, on the hour, some hours ago: a model of Gemini is the Strategist, and a model
        // of Claude holds the other roles. Each trade made 1 R.
        await application.GiveContributorsAsync(
            users: 3,
            tradesEach: 2,
            Morning.AddHours(-3),
            [.. Enum.GetValues<Role>().Select(role => (role, role == Role.Strategist ? gemini : claude))]);

        // On Futures, on 4 hours, ten days ago: the model of Claude holds every role. Each lost 1 R.
        await application.GiveContributorsAsync(
            users: 3,
            tradesEach: 2,
            Morning.AddDays(-10),
            BoardContributors.TeamOf(claude),
            resultInR: -1m,
            market: Market.Futures,
            timeframe: Timeframe.FourHours,
            first: 4);

        async Task<BoardView> BoardAsync(TrackRecordFilter filter) =>
            await application.BoardRecords.OfEveryoneAsync(filter, Cancellation);

        // With no choice, a trade counts once for every provider of its team: by provider, and
        // never by result.
        Assert.Equal([("claude", 12, 0m), ("gemini", 6, 6m)], Providers(await BoardAsync(TrackRecordFilter.None)));
        Assert.Equal([(StoredAgentRows.TeamModel, 12, 0m), (Gemini, 6, 6m)], Models(await BoardAsync(TrackRecordFilter.None)));

        // With a role, it counts for the model that held the role.
        Assert.Equal(
            [("claude", 6, -6m), ("gemini", 6, 6m)],
            Providers(await BoardAsync(TrackRecordFilter.None with { Role = Role.Strategist })));
        Assert.Equal(
            [("claude", 12, 0m)],
            Providers(await BoardAsync(TrackRecordFilter.None with { Role = Role.RiskManager })));

        // A market, and a timeframe.
        Assert.Equal(
            [("claude", 6, 6m), ("gemini", 6, 6m)],
            Providers(await BoardAsync(TrackRecordFilter.None with { Market = Market.Spot })));
        Assert.Equal(
            [("claude", 6, -6m)],
            Providers(await BoardAsync(TrackRecordFilter.None with { Market = Market.Futures })));
        Assert.Equal(
            [("claude", 6, -6m)],
            Providers(await BoardAsync(TrackRecordFilter.None with { Timeframe = Timeframe.FourHours })));

        // A period reaches back from now, and the view says from when.
        var week = await BoardAsync(TrackRecordFilter.None with { Period = TrackPeriod.Last7Days });

        Assert.Equal([("claude", 6, 6m), ("gemini", 6, 6m)], Providers(week));
        Assert.Equal(Morning.AddDays(-7), week.Scope.ClosedFrom);

        // Choices that leave too few are not shown: the trades of Futures of the last week.
        Assert.Empty((await BoardAsync(TrackRecordFilter.None with { Market = Market.Futures, Period = TrackPeriod.Last7Days })).Providers);
    }

    [Fact]
    public async Task The_trades_of_a_user_who_left_the_board_are_left_out_at_once_and_counted_again_when_they_come_back()
    {
        using var application = Application();
        var team = BoardContributors.TeamOf(await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning));

        // Four users with two trades each: 8 trades.
        var contributors = await application.GiveContributorsAsync(users: 4, tradesEach: 2, Morning.AddHours(-3), team);
        var (third, fourth) = (contributors[2].User.Id, contributors[3].User.Id);

        async Task<BoardView> BoardAsync() => await application.BoardRecords.OfEveryoneAsync(TrackRecordFilter.None, Cancellation);

        Assert.Equal([("claude", 8, 8m)], Providers(await BoardAsync()));
        Assert.True(await application.BoardContribution.OfUserAsync(fourth, Cancellation));

        // The fourth leaves the board: 6 trades of 3 users are left, and counted.
        await application.BoardContribution.SetAsync(fourth, contributes: false, Cancellation);

        Assert.False(await application.BoardContribution.OfUserAsync(fourth, Cancellation));
        Assert.Equal([("claude", 6, 6m)], Providers(await BoardAsync()));
        Assert.Equal([(StoredAgentRows.TeamModel, 6, 6m)], Models(await BoardAsync()));

        // The third leaves as well: 4 trades of 2 users are left, which is not shown.
        await application.BoardContribution.SetAsync(third, contributes: false, Cancellation);

        Assert.Empty((await BoardAsync()).Providers);
        Assert.Empty((await BoardAsync()).Models);

        // Their own records are as they were: leaving the board changes nothing of them.
        var own = await application.TrackRecords.OfUserAsync(third, TrackRecordFilter.None, Cancellation);

        Assert.Equal(2, Assert.Single(own.Providers).Record.Trades);

        // And each comes back, with every trade that they have.
        await application.BoardContribution.SetAsync(third, contributes: true, Cancellation);

        Assert.Equal([("claude", 6, 6m)], Providers(await BoardAsync()));

        await application.BoardContribution.SetAsync(fourth, contributes: true, Cancellation);

        Assert.Equal([("claude", 8, 8m)], Providers(await BoardAsync()));
        Assert.True(await application.BoardContribution.OfUserAsync(fourth, Cancellation));
    }

    [Fact]
    [ProvesOwnershipOf("board_opt_outs")]
    public async Task What_one_user_chooses_about_the_board_changes_nothing_of_another()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);

        async Task<(bool Ada, bool Grace)> ContributeAsync() => (
            await application.BoardContribution.OfUserAsync(ada.Id, Cancellation),
            await application.BoardContribution.OfUserAsync(grace.Id, Cancellation));

        // Every user contributes until they choose otherwise, and nothing is stored of that.
        Assert.Equal((true, true), await ContributeAsync());
        Assert.Empty(application.RowsOf(ada.Id)["board_opt_outs"]);

        // Ada leaves: the row is hers, and Grace has none.
        await application.BoardContribution.SetAsync(ada.Id, contributes: false, Cancellation);

        Assert.Equal((false, true), await ContributeAsync());
        Assert.Single(application.RowsOf(ada.Id)["board_opt_outs"]);
        Assert.Empty(application.RowsOf(grace.Id)["board_opt_outs"]);

        // Grace comes back to a board that she never left. It is her own row that is asked for,
        // which there is not: Ada's stays.
        await application.BoardContribution.SetAsync(grace.Id, contributes: true, Cancellation);

        Assert.Equal((false, true), await ContributeAsync());

        // Grace leaves, and Ada comes back: each changes their own choice, and no other.
        await application.BoardContribution.SetAsync(grace.Id, contributes: false, Cancellation);
        await application.BoardContribution.SetAsync(ada.Id, contributes: true, Cancellation);

        Assert.Equal((true, false), await ContributeAsync());
        Assert.Empty(application.RowsOf(ada.Id)["board_opt_outs"]);
        Assert.Single(application.RowsOf(grace.Id)["board_opt_outs"]);

        // A user that there is not contributes nothing, and has left nothing.
        Assert.True(await application.BoardContribution.OfUserAsync(Guid.CreateVersion7(), Cancellation));
    }

    [Fact]
    public async Task A_choice_that_is_made_twice_changes_nothing_the_second_time()
    {
        var clock = new TestClock(Morning);

        using var application = Application(clock);
        var user = await application.RegisterVerifiedAsync(Ada);

        // Two forms that are sent at the same time: both are answered, and one row is written.
        await Task.WhenAll(
            application.BoardContribution.SetAsync(user.Id, contributes: false, Cancellation),
            application.BoardContribution.SetAsync(user.Id, contributes: false, Cancellation));

        var left = Assert.Single(application.RowsOf(user.Id)["board_opt_outs"]);

        // Leaving again later keeps when the user left.
        clock.Set(Morning.AddHours(1));
        await application.BoardContribution.SetAsync(user.Id, contributes: false, Cancellation);

        Assert.Equal([left], application.RowsOf(user.Id)["board_opt_outs"]);

        // The log says each change once, and no more of the user than their id.
        Assert.Single(application.Events(), written => written.Template == "User {UserId} left the platform-wide board");

        // And coming back twice leaves no row, and fails nothing.
        await application.BoardContribution.SetAsync(user.Id, contributes: true, Cancellation);
        await application.BoardContribution.SetAsync(user.Id, contributes: true, Cancellation);

        Assert.Empty(application.RowsOf(user.Id)["board_opt_outs"]);
        Assert.True(await application.BoardContribution.OfUserAsync(user.Id, Cancellation));
        Assert.Single(application.Events(), written => written.Template == "User {UserId} contributes to the platform-wide board again");
    }

    [Fact]
    public async Task The_trades_of_an_agent_that_is_deleted_leave_the_board()
    {
        using var application = Application();
        var team = BoardContributors.TeamOf(await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning));
        var contributors = await application.GiveContributorsAsync(users: 3, tradesEach: 2, Morning.AddHours(-3), team);

        Assert.Equal([("claude", 6, 6m)], Providers(await application.BoardRecords.OfEveryoneAsync(TrackRecordFilter.None, Cancellation)));

        // The board is worked out from the positions that are stored, and nothing of it is kept:
        // without the agent of one of the three, 4 trades of 2 users are left.
        await application.DeleteAgentAsync(contributors[0].AgentId);

        Assert.Empty((await application.BoardRecords.OfEveryoneAsync(TrackRecordFilter.None, Cancellation)).Providers);
    }

    [Fact]
    public void An_answer_of_the_board_can_say_no_user_no_agent_and_no_trade()
    {
        // Everything that an answer can hold, by the types of its members: a record of the
        // board is the record of a provider or of a model, and what the view is of.
        var held = new HashSet<Type>();
        var open = new Queue<Type>([typeof(BoardView)]);

        while (open.TryDequeue(out var type))
        {
            // What a list holds, and what may be nothing.
            type = Nullable.GetUnderlyingType(type) ?? type;

            if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
            {
                foreach (var of in type.GetGenericArguments())
                {
                    open.Enqueue(of);
                }

                continue;
            }

            if (type.Namespace?.StartsWith("Ecnanib", StringComparison.Ordinal) != true || !held.Add(type))
            {
                continue;
            }

            foreach (var member in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                open.Enqueue(member.PropertyType);
            }
        }

        // The view, what it is of, the two kinds of row, the record, and what names a provider
        // and a model of the catalog, which is the same for everyone.
        Assert.Contains(typeof(TrackRecord), held);
        Assert.DoesNotContain(typeof(ContributedTrade), held);
        Assert.DoesNotContain(typeof(ClosedTrade), held);
        Assert.DoesNotContain(typeof(TradeResult), held);

        var members = held
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(member => (Type: type, Member: member)))
            .ToList();

        // The one id that an answer holds is that of a model of the catalog.
        Assert.Equal(
            ["CatalogModel.Id"],
            members
                .Where(held => (Nullable.GetUnderlyingType(held.Member.PropertyType) ?? held.Member.PropertyType) == typeof(Guid))
                .Select(held => $"{held.Type.Name}.{held.Member.Name}"));

        // And no member is named for a user, an agent, a position, a balance, or what was traded.
        Assert.DoesNotContain(members, held => new[] { "UserId", "Agent", "Position", "Owner", "Email", "Balance", "Symbol" }
            .Any(word => held.Member.Name.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The application, with numbers that a test reaches with a few users and trades.</summary>
    private static AccountsApplication Application(TestClock? clock = null) =>
        AgentsApplication.With(
            new StandInMarketData(),
            clock ?? new TestClock(Morning),
            settings: file => file
                .With("Experience:MinSamples", 3)
                .With("Experience:Board:MinUsers", 3)
                .With("Experience:Board:MinTrades", 6));

    private static IEnumerable<(string Code, int Trades, decimal? TotalR)> Providers(BoardView view) =>
        view.Providers.Select(provider => (provider.Provider.Value, provider.Record.Trades, provider.Record.TotalR));

    private static IEnumerable<(string Identifier, int Trades, decimal? TotalR)> Models(BoardView view) =>
        view.Models.Select(model => (model.Model.Identifier, model.Record.Trades, model.Record.TotalR));
}
