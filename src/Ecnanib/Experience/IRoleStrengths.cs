using Ecnanib.Catalog;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Experience;

/// <summary>
/// The measure and the strength score of each model in each role, market, and timeframe, from
/// what one user's agents did (FR-EXP-3, FR-EXP-4). Demo and Live are kept and scored separately
/// (FR-EXP-9). See "The strength score" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// A slice is worked out each time it is asked for, and no aggregate is stored: from the scored
/// calls of the analysts, from the closed trades, which whatever keeps the positions hands over
/// through <see cref="IClosedTrades"/>, and from the shadow trades of the vetoed plans.
/// </para>
/// <para>
/// It takes the user by id, and not from a request: Experience may not use Accounts. Nothing of
/// what the agents of anyone else did is in an answer (NFR-SEC-3). It only reads.
/// </para>
/// <para>
/// <b>It takes its mode, and chooses none.</b> A slice is of one mode (FR-BIN-11). A screen
/// hands over the mode that it shows, and what recommends from the scores asks for each mode and
/// goes by <see cref="ExperienceBasis"/>, with the sample sizes of the slices.
/// </para>
/// </remarks>
public interface IRoleStrengths
{
    /// <summary>The slices of every model that something of the user counts for, in one mode.</summary>
    /// <param name="userId">The user who is signed in.</param>
    /// <param name="filter">Which slices are answered, and from how far back their samples are. Its mode is not gone by.</param>
    /// <param name="mode">The mode whose samples count.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<RoleStrengthView> OfUserAsync(Guid userId, TrackRecordFilter filter, TradingMode mode, CancellationToken cancellationToken);

    /// <summary>
    /// The slices of one model, in one mode. The role of the filter is not gone by: every role
    /// that the model has a sample in is answered.
    /// </summary>
    /// <param name="userId">The user who is signed in.</param>
    /// <param name="modelId">The id of the model in the catalog.</param>
    /// <param name="filter">Which slices are answered, and from how far back their samples are. Its mode and its role are not gone by.</param>
    /// <param name="mode">The mode whose samples count.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<RoleStrengthView> OfModelAsync(
        Guid userId, Guid modelId, TrackRecordFilter filter, TradingMode mode, CancellationToken cancellationToken);
}

/// <summary>The strength scores of a user's models, in one mode, with what a screen says beside them.</summary>
/// <param name="Mode">The mode whose samples the slices are of: one, and never both (FR-BIN-11).</param>
/// <param name="MinSamples">The sample size below which a slice has no score: <c>Experience:MinSamples</c>.</param>
/// <param name="Damping">The constant of the formula: <c>Experience:Damping</c>.</param>
/// <param name="Slices">Each slice that has a sample, by provider, by identifier, and then by role, market, and timeframe. Never by score: nothing is ranked.</param>
/// <param name="CallsAwaiting">How many calls of analysts are left out because the candles after them have not closed yet.</param>
/// <param name="CallsWithoutCandles">How many are left out because the candles after them could no longer be had.</param>
/// <param name="ShadowTradesOpen">How many shadow trades are left out because they have not ended yet.</param>
/// <param name="ShadowTradesWithoutResult">How many are left out because they ended with no result: not entered, rejected, or with no candles.</param>
public sealed record RoleStrengthView(
    TradingMode Mode,
    int MinSamples,
    int Damping,
    IReadOnlyList<RoleStrength> Slices,
    int CallsAwaiting,
    int CallsWithoutCandles,
    int ShadowTradesOpen,
    int ShadowTradesWithoutResult);

/// <summary>The strength of one model in one role, on one market and one timeframe: a slice (FR-EXP-4).</summary>
/// <param name="Model">The model, as the catalog has it.</param>
/// <param name="Role">The role.</param>
/// <param name="Market">The market.</param>
/// <param name="Timeframe">The timeframe.</param>
/// <param name="Measure">The measure of the role, with its sample size.</param>
/// <param name="Enough">Whether the slice has the minimum sample. One that has not shows "not enough data".</param>
/// <param name="Score">
/// The strength score, or null: for a slice below the minimum sample, and for one whose measure
/// cannot be said. A slice with no score is recommended from by nothing.
/// </param>
public sealed record RoleStrength(
    CatalogModel Model, Role Role, Market Market, Timeframe Timeframe, RoleMeasure Measure, bool Enough, decimal? Score)
{
    /// <summary>The slice of a measure: with its score where its sample is enough, and without one otherwise.</summary>
    /// <param name="model">The model.</param>
    /// <param name="role">The role.</param>
    /// <param name="market">The market.</param>
    /// <param name="timeframe">The timeframe.</param>
    /// <param name="measure">The measure of the role in the slice.</param>
    /// <param name="minSamples">The sample size below which a slice has no score: <c>Experience:MinSamples</c>.</param>
    /// <param name="damping">The constant of the formula: <c>Experience:Damping</c>.</param>
    public static RoleStrength Of(
        CatalogModel model, Role role, Market market, Timeframe timeframe, RoleMeasure measure, int minSamples, int damping)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(measure);

        var enough = StrengthScore.IsEnough(measure.Samples, minSamples);

        return new RoleStrength(
            model,
            role,
            market,
            timeframe,
            measure,
            enough,
            enough && measure.Value is { } value ? StrengthScore.Of(value, measure.Samples, damping) : null);
    }
}

/// <summary>Works out the strength scores from what the user that it is asked for has.</summary>
internal sealed class RoleStrengths(
    IDbContextFactory<EcnanibDbContext> contexts,
    IEnumerable<IClosedTrades> sources,
    ICatalogModels models,
    ExperienceSettings settings,
    TimeProvider clock) : IRoleStrengths
{
    private static readonly Role[] Analysts = [Role.MarketAnalyst, Role.SentimentAnalyst];
    private static readonly Role[] Traders = [Role.Strategist, Role.RiskManager];

    public Task<RoleStrengthView> OfUserAsync(Guid userId, TrackRecordFilter filter, TradingMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return OfAsync(userId, modelId: null, filter, mode, cancellationToken);
    }

    public Task<RoleStrengthView> OfModelAsync(
        Guid userId, Guid modelId, TrackRecordFilter filter, TradingMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // The page of a model says every role.
        return OfAsync(userId, modelId, filter with { Role = null }, mode, cancellationToken);
    }

    private async Task<RoleStrengthView> OfAsync(
        Guid userId, Guid? modelId, TrackRecordFilter filter, TradingMode mode, CancellationToken cancellationToken)
    {
        var from = filter.Period.Start(clock.GetUtcNow());
        var measures = new Dictionary<Slice, RoleMeasure>();
        var (callsAwaiting, callsWithoutCandles, tradesOpen, tradesWithoutResult) = (0, 0, 0, 0);

        bool Asked(Role role) => filter.Role is null || filter.Role == role;

        // The closed trades of the mode with a result in R: what a plan that was executed
        // produced, for the model that made it and for the model that approved it.
        var executed = Traders.Any(Asked)
            ? (await sources.OfUserAsync(userId, from, cancellationToken))
                .Where(trade => trade.Mode == mode
                    && trade.Result?.ResultInR is not null
                    && (filter.Market is null || trade.Market == filter.Market)
                    && (filter.Timeframe is null || trade.Timeframe == filter.Timeframe))
                .ToList()
            : [];

        IEnumerable<(Slice Slice, decimal R)> ExecutedFor(Role role) =>
            from trade in executed
            from held in trade.Roles
            where held.Role == role && (modelId is null || held.ModelId == modelId)
            select (new Slice(held.ModelId, role, trade.Market, trade.Timeframe), trade.Result!.ResultInR!.Value);

        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        {
            // The reads agree with each other: a row that is scored meanwhile is counted once.
            await using var snapshot = await context.BeginSnapshotAsync(cancellationToken);

            if (Analysts.Any(Asked))
            {
                var calls = context.Set<AnalystCall>().AsNoTracking().OwnedBy(userId).Where(call => call.Mode == mode);

                if (from is { } start)
                {
                    calls = calls.Where(call => call.ClosedAt >= start);
                }

                if (filter.Role is { } role)
                {
                    calls = calls.Where(call => call.Role == role);
                }

                if (filter.Market is { } market)
                {
                    calls = calls.Where(call => call.Market == market);
                }

                if (filter.Timeframe is { } timeframe)
                {
                    calls = calls.Where(call => call.Timeframe == timeframe);
                }

                if (modelId is { } model)
                {
                    calls = calls.Where(call => call.ModelId == model);
                }

                // An agent of a short timeframe makes a call every quarter hour, so the calls
                // are counted by the database, and not handed over one by one.
                var counted = await calls
                    .GroupBy(call => new { call.ModelId, call.Role, call.Market, call.Timeframe, call.Outcome })
                    .Select(slice => new { slice.Key, Calls = slice.Count() })
                    .ToListAsync(cancellationToken);

                callsAwaiting = counted.Where(slice => slice.Key.Outcome is null).Sum(slice => slice.Calls);
                callsWithoutCandles = counted.Where(slice => slice.Key.Outcome == CallOutcome.NoCandles).Sum(slice => slice.Calls);

                foreach (var slice in counted
                    .Where(slice => slice.Key.Outcome is CallOutcome.Matched or CallOutcome.Missed)
                    .GroupBy(slice => new Slice(slice.Key.ModelId, slice.Key.Role, slice.Key.Market, slice.Key.Timeframe)))
                {
                    measures[slice.Key] = RoleMeasures.OfAnalyst(
                        slice.Where(scored => scored.Key.Outcome == CallOutcome.Matched).Sum(scored => scored.Calls),
                        slice.Where(scored => scored.Key.Outcome == CallOutcome.Missed).Sum(scored => scored.Calls));
                }
            }

            if (Asked(Role.Strategist))
            {
                foreach (var slice in ExecutedFor(Role.Strategist).GroupBy(result => result.Slice, result => result.R))
                {
                    measures[slice.Key] = RoleMeasures.OfStrategist([.. slice]);
                }
            }

            if (Asked(Role.RiskManager))
            {
                var trades = context.Set<ShadowTrade>().AsNoTracking().OwnedBy(userId).Where(trade => trade.Mode == mode);

                if (from is { } start)
                {
                    trades = trades.Where(trade => trade.ClosedAt >= start);
                }

                if (filter.Market is { } market)
                {
                    trades = trades.Where(trade => trade.Market == market);
                }

                if (filter.Timeframe is { } timeframe)
                {
                    trades = trades.Where(trade => trade.Timeframe == timeframe);
                }

                if (modelId is { } model)
                {
                    trades = trades.Where(trade => trade.ModelId == model);
                }

                // A veto is rare beside a call, so the shadow trades are handed over as they are.
                var shadows = await trades
                    .Select(trade => new { trade.ModelId, trade.Market, trade.Timeframe, trade.Outcome, trade.ResultInR })
                    .ToListAsync(cancellationToken);

                tradesOpen = shadows.Count(trade => trade.Outcome is null);
                tradesWithoutResult = shadows.Count(trade => trade.Outcome is not null && trade.ResultInR is null);

                var approved = ExecutedFor(Role.RiskManager).ToLookup(result => result.Slice, result => result.R);
                var vetoed = shadows
                    .Where(trade => trade.ResultInR is not null)
                    .ToLookup(
                        trade => new Slice(trade.ModelId, Role.RiskManager, trade.Market, trade.Timeframe),
                        trade => trade.ResultInR!.Value);

                foreach (var slice in approved.Select(side => side.Key).Union(vetoed.Select(side => side.Key)))
                {
                    measures[slice] = RoleMeasures.OfRiskManager([.. approved[slice]], [.. vetoed[slice]]);
                }
            }
        }

        // A model says its provider, and its name. A model of the catalog is never deleted
        // (FR-CAT-5), so the one that held a role is found there for good.
        var catalog = (await models.ListAsync(cancellationToken))
            .SelectMany(provider => provider.Models)
            .ToDictionary(model => model.Id);

        return new RoleStrengthView(
            mode,
            settings.MinSamples,
            settings.Damping,
            [
                .. measures
                    .Where(slice => slice.Value.Samples > 0 && catalog.ContainsKey(slice.Key.ModelId))
                    .Select(slice => RoleStrength.Of(
                        catalog[slice.Key.ModelId],
                        slice.Key.Role,
                        slice.Key.Market,
                        slice.Key.Timeframe,
                        slice.Value,
                        settings.MinSamples,
                        settings.Damping))
                    .OrderBy(slice => slice.Model.Provider.Value, StringComparer.Ordinal)
                    .ThenBy(slice => slice.Model.Identifier, StringComparer.Ordinal)
                    .ThenBy(slice => slice.Role)
                    .ThenBy(slice => slice.Market)
                    .ThenBy(slice => slice.Timeframe),
            ],
            callsAwaiting,
            callsWithoutCandles,
            tradesOpen,
            tradesWithoutResult);
    }

    /// <summary>What a slice is of: one model in one role, on one market and one timeframe.</summary>
    private readonly record struct Slice(Guid ModelId, Role Role, Market Market, Timeframe Timeframe);
}
