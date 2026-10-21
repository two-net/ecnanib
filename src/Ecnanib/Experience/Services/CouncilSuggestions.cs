using Ecnanib.Agents.Abstractions;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Experience.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecnanib.Experience.Services;

/// <summary>
/// FR-EXP-08: per role, the provider and model with the best expectancy in the user's own history at the timeframe
/// and market, over at least <see cref="ExperienceOptions.MinimumTradesForSuggestion"/> closed trades, never a model
/// the catalog has deprecated (FR-SYNC-03). Only the user's own rows are read (FR-EXP-05, NFR-SEC-03).
/// </summary>
internal sealed class CouncilSuggestions : ICouncilSuggestions
{
    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IOptions<ExperienceOptions> _options;

    public CouncilSuggestions(IDbContextFactory<EcnanibDbContext> factory, IOptions<ExperienceOptions> options)
    {
        _factory = factory;
        _options = options;
    }

    public async Task<IReadOnlyList<CouncilSuggestion>> SuggestAsync(Guid userId, CandleInterval timeframe, MarketType marketType, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ExperienceStats.AsNoTracking()
            .Where(s => s.Scope == StatScope.User && s.UserId == userId && s.Timeframe == timeframe && s.MarketType == marketType)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
            return [];
        var deprecated = await db.ProviderModels.AsNoTracking()
            .Where(m => m.Status == ModelStatus.Deprecated)
            .Select(m => new { m.Provider, m.ModelId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var excluded = deprecated.Select(m => (m.Provider, m.ModelId)).ToHashSet();
        return Choose(rows, excluded, _options.Value.MinimumTradesForSuggestion);
    }

    /// <summary>The best row per role: highest expectancy, then the larger sample; nothing for a role without a qualifying row.</summary>
    internal static IReadOnlyList<CouncilSuggestion> Choose(IEnumerable<ExperienceStat> rows, IReadOnlySet<(AiProvider, string)> deprecated, int minimumTrades)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(deprecated);
        return rows
            .Where(r => r.Metrics.Expectancy is not null && r.Metrics.Trades >= minimumTrades && !deprecated.Contains((r.Provider, r.Model)))
            .GroupBy(r => r.Role)
            .Select(g => g.OrderByDescending(r => r.Metrics.Expectancy).ThenByDescending(r => r.Metrics.Trades).ThenBy(r => r.Provider).ThenBy(r => r.Model, StringComparer.Ordinal).First())
            .OrderBy(r => CouncilRoles.Sequence(r.Role))
            .Select(r => new CouncilSuggestion(r.Role, r.Provider, r.Model, r.Metrics.Expectancy!.Value, r.Metrics.Trades))
            .ToList();
    }
}
