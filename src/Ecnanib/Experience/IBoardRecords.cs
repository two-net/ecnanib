using Ecnanib.Catalog;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.Experience;

/// <summary>
/// The platform-wide board: the same track record as a user's own, of each AI provider and of
/// each model, aggregated across all users (FR-EXP-10). A slice is shown only when at least 5
/// users and 50 trades contribute, by the numbers of the configuration file. See "The
/// platform-wide board" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It takes no user, and answers with none.</b> A record of the board is what the trades of
/// many users add up to. Nothing in an answer says a user, an agent, a position, a balance, or
/// one trade, and a record that too few contribute to is not in the answer at all.
/// </para>
/// <para>
/// <b>It is worked out from the trades each time it is asked for,</b> as the record of a user
/// is: nothing of it is stored. So a user who leaves the board is in no record from the next
/// time on, and neither are the trades of an agent or of an account that is deleted.
/// </para>
/// <para>
/// <b>A trade counts as it does in the record of a user,</b> by the rule of
/// <see cref="CountedTrades"/>, and the measures are those of <see cref="TrackRecord"/>. Demo and
/// Live are never added up (FR-EXP-9, FR-BIN-11).
/// </para>
/// </remarks>
public interface IBoardRecords
{
    /// <summary>
    /// The record of every provider and of every model that enough users and trades contribute
    /// to, in one mode, from the trades of everyone who has not left the board.
    /// </summary>
    /// <param name="filter">Which trades count: the same choices as for the record of a user.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<BoardView> OfEveryoneAsync(TrackRecordFilter filter, CancellationToken cancellationToken);
}

/// <summary>
/// What a view of the board is of, which the screen says beside the records. It says no number
/// of trades: only a record that is shown says how many it counts.
/// </summary>
/// <param name="Mode">The mode whose trades the records are of: one, and never both (FR-BIN-11).</param>
/// <param name="ModeChosen">Whether the mode was chosen by the rule, because the filter named none (FR-EXP-9).</param>
/// <param name="ClosedFrom">The moment from which a trade counts by when it was closed, or null for every trade.</param>
/// <param name="MinUsers">The fewest users that must contribute to a record for it to be shown: <c>Experience:Board:MinUsers</c>.</param>
/// <param name="MinTrades">The fewest trades that must: <c>Experience:Board:MinTrades</c>.</param>
public sealed record BoardScope(TradingMode Mode, bool ModeChosen, DateTimeOffset? ClosedFrom, int MinUsers, int MinTrades);

/// <summary>The records of the board, in one mode.</summary>
/// <param name="Scope">What the records are of.</param>
/// <param name="Providers">Each provider that enough users and trades contribute to, in the order of the providers' codes.</param>
/// <param name="Models">Each model that enough contribute to, by provider and then by identifier.</param>
public sealed record BoardView(
    BoardScope Scope,
    IReadOnlyList<ProviderTrackRecord> Providers,
    IReadOnlyList<ModelTrackRecord> Models);

/// <summary>Works out the records of the board from the trades of everyone who contributes.</summary>
internal sealed class BoardRecords(
    IEnumerable<IBoardTrades> sources,
    ICatalogModels models,
    ExperienceSettings settings,
    TimeProvider clock) : IBoardRecords
{
    public async Task<BoardView> OfEveryoneAsync(TrackRecordFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var closedFrom = filter.Period.Start(clock.GetUtcNow());

        // Of everyone who has not left the board: whoever keeps the trades leaves the others out.
        var contributed = await sources.OfContributorsAsync(closedFrom, cancellationToken);
        var contributions = contributed.ToDictionary(trade => trade.Trade.PositionId);

        // For whom a trade counts is the rule of the record of a user: the board shows the same
        // track record.
        var counting = CountedTrades.Of(
            contributed.Select(trade => trade.Trade), filter, await models.ByIdAsync(cancellationToken));

        // A slice is held to the rule by itself: a record that too few contribute to is not
        // worked out, and is in no answer.
        bool Shown(IReadOnlyList<ClosedTrade> trades) =>
            BoardSlice.IsShown(trades.Select(trade => contributions[trade.PositionId]), settings.Board);

        BoardView Of(TradingMode mode)
        {
            var ofMode = counting.Where(counted => counted.Trade.Mode == mode).ToList();

            return new BoardView(
                new BoardScope(mode, filter.Mode is null, closedFrom, settings.Board.MinUsers, settings.Board.MinTrades),
                [
                    .. ofMode
                        .ByProvider()
                        .Where(provider => Shown(provider.Trades))
                        .Select(provider => new ProviderTrackRecord(
                            provider.Provider, provider.ProviderName, TrackRecord.Of(provider.Trades, settings.MinSamples))),
                ],
                [
                    .. ofMode
                        .ByModel()
                        .Where(model => Shown(model.Trades))
                        .Select(model => new ModelTrackRecord(model.Model, TrackRecord.Of(model.Trades, settings.MinSamples))),
                ]);
        }

        // A record is of one mode (FR-BIN-11): the one that was asked for.
        if (filter.Mode is { } asked)
        {
            return Of(asked);
        }

        // Told none, the board shows Live where it has a record to show, and otherwise Demo
        // (FR-EXP-9): what is enough for the board is one record that may be shown. The trades
        // of a model are among those of its provider, so a mode with no provider to show has no
        // model to show either, and the choice says no more than asking for each mode does.
        var (live, demo) = (Of(TradingMode.Live), Of(TradingMode.Demo));

        return ExperienceBasis.Choose(live.Providers.Count, demo.Providers.Count, minSamples: 1).Mode == TradingMode.Live
            ? live
            : demo;
    }
}
