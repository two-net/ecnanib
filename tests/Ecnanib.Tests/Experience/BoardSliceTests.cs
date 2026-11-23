using Ecnanib.Experience;
using Ecnanib.Platform.Settings;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// A slice of the platform-wide board is shown only when at least 5 users and 50 trades
/// contribute (FR-EXP-10). The rule is held to the numbers here, without a database. See "The
/// platform-wide board" in docs/experience.md.
/// </summary>
public class BoardSliceTests
{
    /// <summary>What the specification names, which is what the tracked file has.</summary>
    private static readonly ExperienceBoardSettings Board = new(MinUsers: 5, MinTrades: 50);

    [Theory]
    [InlineData(4, 50, false)]
    [InlineData(5, 49, false)]
    [InlineData(5, 50, true)]
    [InlineData(4, 500, false)]
    [InlineData(49, 49, false)]
    [InlineData(12, 240, true)]
    public void A_slice_is_shown_only_when_at_least_5_users_and_50_trades_contribute(int users, int trades, bool shown)
    {
        Assert.Equal(shown, BoardSlice.IsShown(TradesOf(users, trades), Board));
    }

    [Fact]
    public void One_user_shows_nothing_however_many_trades_are_theirs()
    {
        Assert.False(BoardSlice.IsShown(TradesOf(users: 1, trades: 5000), Board));

        // And nothing is shown of no trade at all.
        Assert.False(BoardSlice.IsShown([], Board));
    }

    [Fact]
    public void A_user_is_counted_once_however_many_of_the_trades_are_theirs()
    {
        // One user made 46 of the 50 trades, and four others one each: five users contribute.
        var one = Guid.CreateVersion7();
        var slice = Enumerable.Range(0, 46).Select(minute => Of(one, minute))
            .Concat(Enumerable.Range(46, 4).Select(minute => Of(Guid.CreateVersion7(), minute)))
            .ToList();

        Assert.True(BoardSlice.IsShown(slice, Board));

        // Without one of the four there are four users, and 49 trades.
        Assert.False(BoardSlice.IsShown(slice[..^1], Board));
    }

    [Fact]
    public void A_trade_that_is_in_no_measure_contributes_nothing_and_neither_does_its_user()
    {
        var users = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7()).ToList();

        // 49 trades with a result in R, and one of each kind that a record does not count: a
        // result that is not recorded yet, and a result that says no R.
        var counted = Enumerable.Range(0, 49).Select(minute => Of(users[minute % 5], minute)).ToList();

        Assert.False(BoardSlice.IsShown(
            [.. counted, new ContributedTrade(users[0], Trades.Awaiting(49)), new ContributedTrade(users[1], Trades.Closed(50, resultInR: null))],
            Board));

        // 50 counted trades of four users: the fifth has a trade, and none that is counted.
        var ofFour = Enumerable.Range(0, 50).Select(minute => Of(users[minute % 4], minute)).ToList();

        Assert.False(BoardSlice.IsShown([.. ofFour, new ContributedTrade(users[4], Trades.Awaiting(50))], Board));
        Assert.True(BoardSlice.IsShown([.. ofFour, Of(users[4], 50)], Board));
    }

    [Fact]
    public void The_numbers_are_those_of_the_configuration_file()
    {
        var small = new ExperienceBoardSettings(MinUsers: 2, MinTrades: 3);

        Assert.True(BoardSlice.IsShown(TradesOf(users: 2, trades: 3), small));
        Assert.False(BoardSlice.IsShown(TradesOf(users: 2, trades: 2), small));
        Assert.False(BoardSlice.IsShown(TradesOf(users: 1, trades: 3), small));

        // The same trades are too few by the numbers of the tracked file.
        Assert.False(BoardSlice.IsShown(TradesOf(users: 2, trades: 3), Board));
    }

    /// <summary>Some counted trades of some users, dealt out in turn: every user has one where there are enough.</summary>
    private static List<ContributedTrade> TradesOf(int users, int trades)
    {
        var ids = Enumerable.Range(0, users).Select(_ => Guid.CreateVersion7()).ToList();

        return [.. Enumerable.Range(0, trades).Select(minute => Of(ids[minute % users], minute))];
    }

    /// <summary>A counted trade of a user, which made what it risked.</summary>
    private static ContributedTrade Of(Guid userId, int minute) => new(userId, Trades.Closed(minute, resultInR: 1m));
}
