using Ecnanib.Catalog;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The AI usage of a user: the calls that the roles of the user's agents made, what the
/// providers counted for them, and what that is estimated to cost where the price is known. It
/// answers by agent, provider, and day (FR-RPT-3), and for each of the user's keys (FR-KEY-11).
/// See "AI usage" in docs/team.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing of it is stored.</b> It is worked out from the recorded calls of the roles each
/// time it is asked for (FR-TEAM-8), so it says what those rows say. The calls of an agent go
/// with the agent when it is deleted, and its usage with them.
/// </para>
/// <para>
/// <b>Every call that was made counts,</b> whatever came of it, in a cycle that a close started
/// and in a test cycle alike, and in Demo as in Live: a provider charges a key the same for
/// each. A row that says that no call was made, because the user had no key to make it with,
/// is no call. The calls that write lessons are recorded nowhere, and are not among them: see
/// "Not built yet" in docs/experience.md.
/// </para>
/// <para>
/// <b>A day is a UTC day.</b> A call is of the day that it was made in, by the clock of the
/// service.
/// </para>
/// <para>
/// Everything is asked for by owner. It may not use Accounts, so it is handed the user whose
/// usage is asked for, which is the user who is signed in.
/// </para>
/// </remarks>
public interface IAiUsage
{
    /// <summary>
    /// The usage of a user over a period: for each provider, and for each day, agent, and
    /// provider that a call was made in.
    /// </summary>
    /// <param name="userId">The user whose agents made the calls.</param>
    /// <param name="filter">Which period, and which agent when it is one alone.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<AiUsageView> OfUserAsync(Guid userId, AiUsageFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// The usage of each key of a user: the calls to the provider of the key in the last
    /// <see cref="KeyUsage.Days"/> days, or since the key that is stored now was stored when
    /// that is later. A user has one key for each provider (FR-KEY-2), so the calls to a
    /// provider since its key was stored are the calls that were made with that key.
    /// </summary>
    /// <param name="userId">The user whose keys they are.</param>
    /// <param name="keys">The keys that are stored, each with its provider and when it was stored.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    /// <returns>The usage of each key, in the order the keys were given in.</returns>
    Task<IReadOnlyList<KeyUsage>> OfKeysAsync(Guid userId, IReadOnlyList<KeyInUse> keys, CancellationToken cancellationToken);
}

/// <summary>How far back usage is shown. A day is a UTC day, and today is one of them.</summary>
public enum UsagePeriod
{
    /// <summary>Today and the 6 days before.</summary>
    Last7Days,

    /// <summary>Today and the 29 days before.</summary>
    Last30Days,

    /// <summary>Today and the 89 days before.</summary>
    Last90Days,
}

/// <summary>What usage is asked for.</summary>
/// <param name="AgentId">The agent whose calls are counted, or null for those of every agent of the user.</param>
/// <param name="Period">How far back.</param>
public sealed record AiUsageFilter(Guid? AgentId, UsagePeriod Period)
{
    /// <summary>The usage of every agent, over the period that is shown first.</summary>
    public static readonly AiUsageFilter None = new(AgentId: null, UsagePeriod.Last30Days);

    /// <summary>How many days the period is: today, and the days before.</summary>
    public int Days => Period switch
    {
        UsagePeriod.Last7Days => 7,
        UsagePeriod.Last30Days => 30,
        UsagePeriod.Last90Days => 90,
        _ => throw new InvalidOperationException($"No such period: {Period}."),
    };
}

/// <summary>The usage of a user over a period.</summary>
/// <param name="From">The start of the first day of the period: midnight, UTC.</param>
/// <param name="Providers">What the calls to each provider add up to over the period, by the provider's code. A provider that no call was made to is not among them.</param>
/// <param name="Days">
/// What the calls of one agent to one provider add up to in one day, the latest day first. A
/// day, an agent, or a provider with no call is not among them.
/// </param>
public sealed record AiUsageView(DateTimeOffset From, IReadOnlyList<ProviderUsage> Providers, IReadOnlyList<DayUsage> Days);

/// <summary>What the calls to one provider add up to.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="ProviderName">Its name as users read it.</param>
/// <param name="Figures">What the calls add up to.</param>
public sealed record ProviderUsage(AiProviderCode Provider, string ProviderName, UsageFigures Figures);

/// <summary>What the calls of one agent to one provider add up to in one day.</summary>
/// <param name="Day">The start of the day: midnight, UTC.</param>
/// <param name="AgentId">The agent whose roles made the calls.</param>
/// <param name="Provider">The provider that was called.</param>
/// <param name="ProviderName">Its name as users read it.</param>
/// <param name="Figures">What the calls add up to.</param>
public sealed record DayUsage(DateTimeOffset Day, Guid AgentId, AiProviderCode Provider, string ProviderName, UsageFigures Figures);

/// <summary>A key that is stored for a user, as far as its usage goes.</summary>
/// <param name="Provider">The provider that the key is for.</param>
/// <param name="SavedAt">When the key that is stored now was stored.</param>
public sealed record KeyInUse(AiProviderCode Provider, DateTimeOffset SavedAt);

/// <summary>The usage of one key of a user.</summary>
/// <param name="Provider">The provider that the key is for.</param>
/// <param name="From">The moment from which the calls are counted.</param>
/// <param name="SinceSaved">
/// Whether that is the moment the key was stored, which is later than the start of the period.
/// The figures are then of the whole time that the key has been in use.
/// </param>
/// <param name="Figures">What the calls add up to.</param>
public sealed record KeyUsage(AiProviderCode Provider, DateTimeOffset From, bool SinceSaved, UsageFigures Figures)
{
    /// <summary>How many days back the usage of a key is counted: today, and the days before. A day is a UTC day.</summary>
    public const int Days = 30;
}

/// <summary>Works out the usage of a user from the recorded calls of the roles.</summary>
internal sealed class AiUsage(
    IDbContextFactory<EcnanibDbContext> contexts,
    ICatalogModels models,
    IModelPrices prices,
    TimeProvider clock) : IAiUsage
{
    public async Task<AiUsageView> OfUserAsync(Guid userId, AiUsageFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var start = StartOf(filter.Days);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Both are asked for by owner: a call of another user, and a cycle of another user's
        // agent, are not found. A call names its cycle, and the cycle its agent.
        var calls =
            from output in context.Set<RoleOutput>().AsNoTracking().OwnedBy(userId)
            join cycle in context.Set<DecisionCycle>().AsNoTracking().OwnedBy(userId) on output.CycleId equals cycle.Id
            where output.CalledAt >= start && output.Outcome != RoleCallOutcome.NoKey
            select new
            {
                cycle.AgentId,
                output.ModelId,

                // The UTC day of the call, whatever time zone the database goes by.
                Day = output.CalledAt.DateTime.Date,
                output.InputTokens,
                output.OutputTokens,
                output.LiveSearches,
            };

        if (filter.AgentId is { } agentId)
        {
            calls = calls.Where(call => call.AgentId == agentId);
        }

        // An agent of a short timeframe makes hundreds of calls a day, so the calls are counted
        // by the database, and not handed over one by one.
        var counted = await calls
            .GroupBy(call => new { call.AgentId, call.ModelId, call.Day })
            .Select(slice => new
            {
                slice.Key.AgentId,
                slice.Key.ModelId,
                slice.Key.Day,
                Calls = slice.Count(),
                Counted = slice.Count(call => call.InputTokens != null && call.OutputTokens != null),
                InputTokens = slice.Sum(call => (long?)call.InputTokens) ?? 0L,
                OutputTokens = slice.Sum(call => (long?)call.OutputTokens) ?? 0L,
                LiveSearches = slice.Sum(call => (long?)call.LiveSearches) ?? 0L,
            })
            .ToListAsync(cancellationToken);

        var called = await CalledAsync(counted.Select(slice => slice.ModelId), cancellationToken);

        var slices = counted
            .Select(slice =>
            {
                var model = called[slice.ModelId];

                return new
                {
                    // The database hands the day over as a time of no zone, which is UTC here.
                    Day = new DateTimeOffset(DateTime.SpecifyKind(slice.Day, DateTimeKind.Utc)),
                    slice.AgentId,
                    model.Provider,
                    model.ProviderName,
                    Usage = new ModelUsage(
                        slice.Calls, slice.Counted, slice.InputTokens, slice.OutputTokens, slice.LiveSearches, PriceOf(model)),
                };
            })
            .ToList();

        return new AiUsageView(
            start,
            [
                .. slices
                    .GroupBy(slice => (slice.Provider, slice.ProviderName), slice => slice.Usage)
                    .OrderBy(provider => provider.Key.Provider.Value, StringComparer.Ordinal)
                    .Select(provider => new ProviderUsage(provider.Key.Provider, provider.Key.ProviderName, UsageFigures.Of(provider))),
            ],
            [
                .. slices
                    .GroupBy(slice => (slice.Day, slice.AgentId, slice.Provider, slice.ProviderName), slice => slice.Usage)
                    .OrderByDescending(day => day.Key.Day)
                    .ThenBy(day => day.Key.AgentId)
                    .ThenBy(day => day.Key.Provider.Value, StringComparer.Ordinal)
                    .Select(day => new DayUsage(day.Key.Day, day.Key.AgentId, day.Key.Provider, day.Key.ProviderName, UsageFigures.Of(day))),
            ]);
    }

    public async Task<IReadOnlyList<KeyUsage>> OfKeysAsync(Guid userId, IReadOnlyList<KeyInUse> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count == 0)
        {
            return [];
        }

        var period = StartOf(KeyUsage.Days);
        var usage = new List<KeyUsage>(keys.Count);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The keys of a user are asked about one after the other, and agree with each other:
        // a call that is recorded meanwhile is in all of the answer, or in none of it.
        await using var snapshot = await context.BeginSnapshotAsync(cancellationToken);

        foreach (var key in keys)
        {
            // The database takes a moment in UTC alone.
            var saved = key.SavedAt.ToUniversalTime();
            var sinceSaved = saved > period;
            var since = sinceSaved ? saved : period;
            var provider = key.Provider;

            // Asked for by owner. A call names its model, and the model its provider: the
            // catalog is the same for everyone, and a model of it is never deleted.
            var counted = await (
                from output in context.Set<RoleOutput>().AsNoTracking().OwnedBy(userId)
                join model in context.Set<AiModel>().AsNoTracking() on output.ModelId equals model.Id
                where model.Provider == provider && output.CalledAt >= since && output.Outcome != RoleCallOutcome.NoKey
                group output by output.ModelId into slice
                select new
                {
                    ModelId = slice.Key,
                    Calls = slice.Count(),
                    Counted = slice.Count(call => call.InputTokens != null && call.OutputTokens != null),
                    InputTokens = slice.Sum(call => (long?)call.InputTokens) ?? 0L,
                    OutputTokens = slice.Sum(call => (long?)call.OutputTokens) ?? 0L,
                    LiveSearches = slice.Sum(call => (long?)call.LiveSearches) ?? 0L,
                }).ToListAsync(cancellationToken);

            var called = await CalledAsync(counted.Select(slice => slice.ModelId), cancellationToken);

            usage.Add(new KeyUsage(
                provider,
                since,
                sinceSaved,
                UsageFigures.Of(counted.Select(slice => new ModelUsage(
                    slice.Calls, slice.Counted, slice.InputTokens, slice.OutputTokens, slice.LiveSearches, PriceOf(called[slice.ModelId]))))));
        }

        return usage;
    }

    /// <summary>The start of a period of so many UTC days that ends with today.</summary>
    private DateTimeOffset StartOf(int days) => EquityMarks.DayOf(clock.GetUtcNow()).AddDays(-(days - 1));

    /// <summary>What the configuration file says that the tokens of a model cost, or null when it says nothing (FR-CAT-11).</summary>
    private ModelPrice? PriceOf(CatalogModel model) => prices.Of(model.Provider, model.Identifier);

    /// <summary>The models that were called, by their ids, as the catalog names them.</summary>
    private async Task<Dictionary<Guid, CatalogModel>> CalledAsync(IEnumerable<Guid> modelIds, CancellationToken cancellationToken)
    {
        var called = new Dictionary<Guid, CatalogModel>();

        foreach (var modelId in modelIds.Distinct())
        {
            // A call points at its model with a key that keeps the model from being deleted
            // (FR-CAT-5), so the catalog holds every model that was called.
            called[modelId] = await models.FindAsync(modelId, cancellationToken)
                ?? throw new InvalidOperationException($"The catalog holds no model {modelId}, which a recorded call names.");
        }

        return called;
    }
}
