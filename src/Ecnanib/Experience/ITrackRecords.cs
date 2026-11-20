using Ecnanib.Catalog;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.Experience;

/// <summary>
/// The track record of each AI provider and of each model, from the closed trades of one user,
/// filterable by role, market, timeframe, mode, and period (FR-EXP-2). Demo and Live are kept
/// and scored separately (FR-EXP-9). See docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// A record is worked out from the trades each time it is asked for: nothing of it is stored.
/// So it always says what the stored positions say, and a trade that is deleted with its agent
/// is in no record any more.
/// </para>
/// <para>
/// It takes the user by id, and not from a request: Experience may not use Accounts. Whatever
/// serves the request hands over the user who is signed in, and nothing of the trades of anyone
/// else is in an answer (NFR-SEC-3). It only reads.
/// </para>
/// </remarks>
public interface ITrackRecords
{
    /// <summary>
    /// The track record of every provider and of every model that a trade of the user counts
    /// for, in one mode.
    /// </summary>
    /// <param name="userId">The user who is signed in.</param>
    /// <param name="filter">Which trades count.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<TrackRecordView> OfUserAsync(Guid userId, TrackRecordFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// The track record of one model from the trades of the user, in one mode: in each role, and
    /// in any role. The role of the filter is not gone by: every role is answered. A model that
    /// held no role in a trade of the user has records of no trades.
    /// </summary>
    /// <param name="userId">The user who is signed in.</param>
    /// <param name="modelId">The id of the model in the catalog.</param>
    /// <param name="filter">Which trades count.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<ModelTrackRecordView> OfModelAsync(Guid userId, Guid modelId, TrackRecordFilter filter, CancellationToken cancellationToken);
}

/// <summary>Which trades a track record counts (FR-EXP-2). What is null is not chosen, and leaves nothing out.</summary>
/// <param name="Role">
/// The role. A trade then counts for the model that held this role, and for its provider.
/// Without one it counts once for each model of its team, and once for each provider.
/// </param>
/// <param name="Market">The market that a trade was traded on.</param>
/// <param name="Timeframe">The timeframe that its agent worked on.</param>
/// <param name="Mode">
/// The mode. A record is of one mode, and never of both (FR-BIN-11): where none is chosen, it is
/// Live where there is enough Live data, and otherwise Demo (FR-EXP-9).
/// </param>
/// <param name="Period">How far back a trade counts, by when it was closed.</param>
public sealed record TrackRecordFilter(Role? Role, Market? Market, Timeframe? Timeframe, TradingMode? Mode, TrackPeriod Period)
{
    /// <summary>The filter that chooses nothing: every trade, of the mode that the rule chooses.</summary>
    public static TrackRecordFilter None { get; } = new(null, null, null, null, TrackPeriod.AllTime);
}

/// <summary>How far back a trade counts for a track record, by when it was closed.</summary>
public enum TrackPeriod
{
    /// <summary>Every trade.</summary>
    AllTime,

    /// <summary>The trades that were closed in the last 7 days.</summary>
    Last7Days,

    /// <summary>The trades that were closed in the last 30 days.</summary>
    Last30Days,

    /// <summary>The trades that were closed in the last 90 days.</summary>
    Last90Days,

    /// <summary>The trades that were closed in the last 365 days.</summary>
    Last365Days,
}

/// <summary>What the periods of a track record are.</summary>
public static class TrackPeriods
{
    /// <summary>The periods, in the order a choice lists them: every trade first, and then the shortest.</summary>
    public static IReadOnlyList<TrackPeriod> All { get; } = Enum.GetValues<TrackPeriod>();

    /// <summary>How many days a period reaches back, or null for the one that has no start.</summary>
    public static int? Days(this TrackPeriod period) => period switch
    {
        TrackPeriod.AllTime => null,
        TrackPeriod.Last7Days => 7,
        TrackPeriod.Last30Days => 30,
        TrackPeriod.Last90Days => 90,
        TrackPeriod.Last365Days => 365,
        _ => throw new ArgumentOutOfRangeException(nameof(period), period, "The period has no length."),
    };

    /// <summary>
    /// The moment from which a trade counts for a period, itself included, or null for the one
    /// that has no start. A day is 24 hours back from now, and no calendar day.
    /// </summary>
    public static DateTimeOffset? Start(this TrackPeriod period, DateTimeOffset now) =>
        period.Days() is { } days ? now.ToUniversalTime().AddDays(-days) : null;
}

/// <summary>What a view of track records is of, which a screen says beside the records.</summary>
/// <param name="Mode">The mode whose trades the records are of: one, and never both (FR-BIN-11).</param>
/// <param name="ModeChosen">Whether the mode was chosen by the rule of <see cref="ExperienceBasis"/>, because the filter named none (FR-EXP-9).</param>
/// <param name="LiveTrades">How many trades the view would count in Live.</param>
/// <param name="DemoTrades">How many trades the view would count in Demo.</param>
/// <param name="MinSamples">The sample size below which a record is a small sample, and Live data is not enough: <c>Experience:MinSamples</c>.</param>
/// <param name="ClosedFrom">The moment from which a trade counts by when it was closed, or null for every trade.</param>
/// <param name="AwaitingResult">How many closed trades of the mode are left out because their result is not recorded yet.</param>
/// <param name="WithoutR">How many closed trades of the mode are left out because their result says no R.</param>
public sealed record TrackRecordScope(
    TradingMode Mode,
    bool ModeChosen,
    int LiveTrades,
    int DemoTrades,
    int MinSamples,
    DateTimeOffset? ClosedFrom,
    int AwaitingResult,
    int WithoutR)
{
    /// <summary>How many trades the view counts, in its mode.</summary>
    public int Trades => Mode == TradingMode.Live ? LiveTrades : DemoTrades;
}

/// <summary>The track records of a user's providers and models, in one mode.</summary>
/// <param name="Scope">What the records are of.</param>
/// <param name="Providers">Each provider that a counted trade counts for, in the order of the providers' codes.</param>
/// <param name="Models">Each model that a counted trade counts for, by provider and then by identifier.</param>
public sealed record TrackRecordView(
    TrackRecordScope Scope,
    IReadOnlyList<ProviderTrackRecord> Providers,
    IReadOnlyList<ModelTrackRecord> Models);

/// <summary>The track record of one provider: of the trades that a model of it counts for.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="ProviderName">Its name as users read it.</param>
/// <param name="Record">Its record.</param>
public sealed record ProviderTrackRecord(AiProviderCode Provider, string ProviderName, TrackRecord Record);

/// <summary>The track record of one model.</summary>
/// <param name="Model">The model, as the catalog has it.</param>
/// <param name="Record">Its record.</param>
public sealed record ModelTrackRecord(CatalogModel Model, TrackRecord Record);

/// <summary>The track record of one model by role, in one mode.</summary>
/// <param name="Scope">What the records are of. Its trades are those in which the model held any role.</param>
/// <param name="Roles">The record of the model in any role, and then in each role, in the order of the roles.</param>
public sealed record ModelTrackRecordView(TrackRecordScope Scope, IReadOnlyList<RoleTrackRecord> Roles);

/// <summary>The track record of a model in one role.</summary>
/// <param name="Role">The role, or null for any role: a trade in which the model held more than one counts once.</param>
/// <param name="Record">Its record.</param>
public sealed record RoleTrackRecord(Role? Role, TrackRecord Record);

/// <summary>Works out track records from the closed trades of the user that it is asked for.</summary>
internal sealed class TrackRecords(
    IEnumerable<IClosedTrades> sources,
    ICatalogModels models,
    ExperienceSettings settings,
    TimeProvider clock) : ITrackRecords
{
    public async Task<TrackRecordView> OfUserAsync(Guid userId, TrackRecordFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var closedFrom = filter.Period.Start(clock.GetUtcNow());
        var trades = await sources.OfUserAsync(userId, closedFrom, cancellationToken);

        // A model says its provider, and its name. A model of the catalog is never deleted
        // (FR-CAT-5), so the one that held a role is found there for good.
        var catalog = (await models.ListAsync(cancellationToken))
            .SelectMany(provider => provider.Models)
            .ToDictionary(model => model.Id);

        // The models that a trade counts for: the one that held the chosen role, or each model
        // of its team once. A trade with none of them is in no record, in either mode.
        var counting = trades
            .Where(trade => Matches(trade, filter))
            .Select(trade => (
                Trade: trade,
                Models: trade.Roles
                    .Where(role => filter.Role is null || role.Role == filter.Role)
                    .Select(role => catalog.GetValueOrDefault(role.ModelId))
                    .OfType<CatalogModel>()
                    .DistinctBy(model => model.Id)
                    .ToList()))
            .Where(counted => counted.Models.Count > 0)
            .ToList();

        var scope = Scope(filter, closedFrom, [.. counting.Select(counted => counted.Trade)]);
        var ofMode = counting.Where(counted => counted.Trade.Mode == scope.Mode).ToList();

        return new TrackRecordView(
            scope,
            [
                .. ofMode
                    .SelectMany(counted => counted.Models
                        .DistinctBy(model => model.Provider)
                        .Select(model => (model.Provider, model.ProviderName, counted.Trade)))
                    .GroupBy(counted => (counted.Provider, counted.ProviderName), counted => counted.Trade)
                    .Select(provider => new ProviderTrackRecord(
                        provider.Key.Provider, provider.Key.ProviderName, TrackRecord.Of(provider, settings.MinSamples)))
                    .Where(provider => provider.Record.Trades > 0)
                    .OrderBy(provider => provider.Provider.Value, StringComparer.Ordinal),
            ],
            [
                .. ofMode
                    .SelectMany(counted => counted.Models.Select(model => (Model: model, counted.Trade)))
                    .GroupBy(counted => counted.Model.Id)
                    .Select(model => new ModelTrackRecord(
                        model.First().Model, TrackRecord.Of(model.Select(counted => counted.Trade), settings.MinSamples)))
                    .Where(model => model.Record.Trades > 0)
                    .OrderBy(model => model.Model.Provider.Value, StringComparer.Ordinal)
                    .ThenBy(model => model.Model.Identifier, StringComparer.Ordinal),
            ]);
    }

    public async Task<ModelTrackRecordView> OfModelAsync(
        Guid userId, Guid modelId, TrackRecordFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var closedFrom = filter.Period.Start(clock.GetUtcNow());

        // The trades in which the model held a role, whichever: the page of a model says every role.
        var held = (await sources.OfUserAsync(userId, closedFrom, cancellationToken))
            .Where(trade => Matches(trade, filter) && trade.Roles.Any(role => role.ModelId == modelId))
            .ToList();

        var scope = Scope(filter, closedFrom, held);
        var ofMode = held.Where(trade => trade.Mode == scope.Mode).ToList();

        return new ModelTrackRecordView(
            scope,
            [
                new RoleTrackRecord(null, TrackRecord.Of(ofMode, settings.MinSamples)),
                .. Enum.GetValues<Role>().Select(role => new RoleTrackRecord(
                    role,
                    TrackRecord.Of(
                        ofMode.Where(trade => trade.Roles.Any(of => of.Role == role && of.ModelId == modelId)),
                        settings.MinSamples))),
            ]);
    }

    /// <summary>Whether a trade is of the market and the timeframe of a filter. Its period is asked of the source, and its mode is decided after.</summary>
    private static bool Matches(ClosedTrade trade, TrackRecordFilter filter) =>
        (filter.Market is null || trade.Market == filter.Market)
        && (filter.Timeframe is null || trade.Timeframe == filter.Timeframe);

    /// <summary>
    /// What a view is of, from the trades that it would count in either mode: the mode that the
    /// filter names, or Live where it has enough trades, and otherwise Demo (FR-EXP-9).
    /// </summary>
    private TrackRecordScope Scope(TrackRecordFilter filter, DateTimeOffset? closedFrom, IReadOnlyList<ClosedTrade> trades)
    {
        int Counted(TradingMode mode) => trades.Count(trade => trade.Mode == mode && trade.Result?.ResultInR is not null);

        var (live, demo) = (Counted(TradingMode.Live), Counted(TradingMode.Demo));
        var mode = filter.Mode ?? ExperienceBasis.Choose(live, demo, settings.MinSamples).Mode;

        return new TrackRecordScope(
            mode,
            filter.Mode is null,
            live,
            demo,
            settings.MinSamples,
            closedFrom,
            trades.Count(trade => trade.Mode == mode && trade.Result is null),
            trades.Count(trade => trade.Mode == mode && trade.Result is { ResultInR: null }));
    }
}
