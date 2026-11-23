using Ecnanib.Catalog;
using Ecnanib.Shared;

namespace Ecnanib.Experience;

/// <summary>A trade that a filter counts, with the models that it counts for.</summary>
/// <param name="Trade">The trade.</param>
/// <param name="Models">The models that it counts for, each once. There is one at the least.</param>
internal sealed record CountedTrade(ClosedTrade Trade, IReadOnlyList<CatalogModel> Models);

/// <summary>The trades that count for one provider.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="ProviderName">Its name as users read it.</param>
/// <param name="Trades">The trades, each once.</param>
internal sealed record ProviderTrades(AiProviderCode Provider, string ProviderName, IReadOnlyList<ClosedTrade> Trades);

/// <summary>The trades that count for one model.</summary>
/// <param name="Model">The model, as the catalog has it.</param>
/// <param name="Trades">The trades, each once.</param>
internal sealed record ModelTrades(CatalogModel Model, IReadOnlyList<ClosedTrade> Trades);

/// <summary>
/// For whom a trade counts (FR-EXP-2), in one place: the track records of a user and the records
/// of the platform-wide board go by the same rule, so that the board shows the same track record
/// (FR-EXP-10). It is a rule only: it reads nothing. See "For whom a trade counts" in
/// docs/experience.md.
/// </summary>
internal static class CountedTrades
{
    /// <summary>
    /// Every model of the catalog by its id. A model says its provider, and its name. A model of
    /// the catalog is never deleted (FR-CAT-5), so the one that held a role is found there for good.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, CatalogModel>> ByIdAsync(
        this ICatalogModels models, CancellationToken cancellationToken) =>
        (await models.ListAsync(cancellationToken))
            .SelectMany(provider => provider.Models)
            .ToDictionary(model => model.Id);

    /// <summary>
    /// The trades of the market and the timeframe of a filter, each with the models that it
    /// counts for: the one that held the chosen role, or each model of its team once. A trade
    /// with none of them is in no record, in either mode. The period of the filter is asked of
    /// the source, and its mode is decided after.
    /// </summary>
    public static IReadOnlyList<CountedTrade> Of(
        IEnumerable<ClosedTrade> trades, TrackRecordFilter filter, IReadOnlyDictionary<Guid, CatalogModel> catalog) =>
        [
            .. trades
                .Where(trade => Matches(trade, filter))
                .Select(trade => new CountedTrade(
                    trade,
                    [
                        .. trade.Roles
                            .Where(role => filter.Role is null || role.Role == filter.Role)
                            .Select(role => catalog.GetValueOrDefault(role.ModelId))
                            .OfType<CatalogModel>()
                            .DistinctBy(model => model.Id),
                    ]))
                .Where(counted => counted.Models.Count > 0),
        ];

    /// <summary>Whether a trade is of the market and the timeframe of a filter.</summary>
    public static bool Matches(ClosedTrade trade, TrackRecordFilter filter) =>
        (filter.Market is null || trade.Market == filter.Market)
        && (filter.Timeframe is null || trade.Timeframe == filter.Timeframe);

    /// <summary>
    /// The trades of each provider, in the order of the providers' codes. A trade counts once
    /// for a provider, with however many models of it.
    /// </summary>
    public static IEnumerable<ProviderTrades> ByProvider(this IEnumerable<CountedTrade> counted) =>
        counted
            .SelectMany(trade => trade.Models
                .DistinctBy(model => model.Provider)
                .Select(model => (model.Provider, model.ProviderName, trade.Trade)))
            .GroupBy(trade => (trade.Provider, trade.ProviderName), trade => trade.Trade)
            .Select(provider => new ProviderTrades(provider.Key.Provider, provider.Key.ProviderName, [.. provider]))
            .OrderBy(provider => provider.Provider.Value, StringComparer.Ordinal);

    /// <summary>The trades of each model, by provider and then by identifier.</summary>
    public static IEnumerable<ModelTrades> ByModel(this IEnumerable<CountedTrade> counted) =>
        counted
            .SelectMany(trade => trade.Models.Select(model => (Model: model, trade.Trade)))
            .GroupBy(trade => trade.Model.Id)
            .Select(model => new ModelTrades(model.First().Model, [.. model.Select(trade => trade.Trade)]))
            .OrderBy(model => model.Model.Provider.Value, StringComparer.Ordinal)
            .ThenBy(model => model.Model.Identifier, StringComparer.Ordinal);
}
