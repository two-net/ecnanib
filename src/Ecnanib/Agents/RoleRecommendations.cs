using Ecnanib.Catalog;
using Ecnanib.Experience;
using Ecnanib.Shared;

namespace Ecnanib.Agents;

/// <summary>
/// A model with the strength score that it is gone by in one role, on one market and one
/// timeframe: the score of its Live samples where there are enough, and of its Demo samples
/// otherwise (FR-EXP-9).
/// </summary>
/// <param name="Slice">The slice whose score is gone by. It has a score: a model without one is not listed.</param>
/// <param name="Basis">The mode whose samples the slice is of, which is said wherever the score is (FR-EXP-9).</param>
/// <param name="Candidate">
/// Whether the role can be given the model now: its user's own key can use it, and for the
/// Sentiment Analyst it searches live. A model that is no candidate keeps its score, which the
/// model that a role has is compared by.
/// </param>
public sealed record ScoredModel(RoleStrength Slice, TradingMode Basis, bool Candidate)
{
    /// <summary>The id of the model in the catalog.</summary>
    public Guid ModelId => Slice.Model.Id;

    /// <summary>The strength score that the model is gone by.</summary>
    public decimal Score => Slice.Score ?? throw new InvalidOperationException("A model that is listed with its score has one.");
}

/// <summary>
/// The models that have a strength score in each role on one market and one timeframe, strongest
/// first, and which of them a role is recommended (FR-TEAM-4). It is a rule only: it reads
/// nothing. See "Role by strength" in docs/agents.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A model is gone by one mode, and never by both</b> (FR-EXP-9, FR-BIN-11):
/// <see cref="ExperienceBasis.Choose"/> says which, from the sample sizes of its slice in Live
/// and in Demo. A model whose Live slice has enough samples and no measure has no score, whatever
/// its Demo slice says: nothing is mixed.
/// </para>
/// <para>
/// <b>A slice without enough samples recommends nothing</b> (FR-EXP-4): it has no score, so its
/// model is not listed.
/// </para>
/// </remarks>
/// <param name="Market">The market that the scores are of.</param>
/// <param name="Timeframe">The timeframe that the scores are of.</param>
/// <param name="MinSamples">The sample size below which a slice has no score: <c>Experience:MinSamples</c>.</param>
/// <param name="Damping">The constant of the formula: <c>Experience:Damping</c>.</param>
/// <param name="Scored">
/// The models that have a score in each role: by score, the highest first, then by sample size,
/// the largest first, and then by the code of the provider and the identifier.
/// </param>
public sealed record RoleRecommendations(
    Market Market,
    Timeframe Timeframe,
    int MinSamples,
    int Damping,
    IReadOnlyDictionary<Role, IReadOnlyList<ScoredModel>> Scored)
{
    /// <summary>The scored models of each role on a market and a timeframe, from the slices of a user in each mode.</summary>
    /// <param name="market">The market.</param>
    /// <param name="timeframe">The timeframe.</param>
    /// <param name="live">The slices of the user in Live.</param>
    /// <param name="demo">The slices of the user in Demo.</param>
    /// <param name="selectable">The models that the user's own keys can use now.</param>
    public static RoleRecommendations Of(
        Market market, Timeframe timeframe, RoleStrengthView live, RoleStrengthView demo, IReadOnlyCollection<SelectableModel> selectable)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(selectable);

        if (live.Mode != TradingMode.Live || demo.Mode != TradingMode.Demo)
        {
            throw new ArgumentException("The slices of Live and the slices of Demo are handed over as that, each of its own mode.");
        }

        var canBeGiven = selectable.ToDictionary(model => model.Id);

        var inLive = SlicesOf(live);
        var inDemo = SlicesOf(demo);

        var scored = new Dictionary<Role, List<ScoredModel>>();

        foreach (var key in inLive.Keys.Union(inDemo.Keys))
        {
            var liveSlice = inLive.GetValueOrDefault(key);
            var demoSlice = inDemo.GetValueOrDefault(key);

            // Live where there is enough, otherwise Demo, by the sample sizes of the slice of
            // the model (FR-EXP-9). The slice of the mode that is chosen is the one gone by.
            var basis = ExperienceBasis.Choose(liveSlice?.Measure.Samples ?? 0, demoSlice?.Measure.Samples ?? 0, live.MinSamples);
            var slice = basis.Mode == TradingMode.Live ? liveSlice : demoSlice;

            if (slice?.Score is null)
            {
                // Not enough samples, or a measure that cannot be said: recommended from by nothing (FR-EXP-4).
                continue;
            }

            // A report of the Sentiment Analyst that rests on no live search is given no weight
            // by the Strategist (FR-CYC-3), so a model that cannot search is no candidate for it,
            // however its calls were scored.
            var candidate = canBeGiven.TryGetValue(key.ModelId, out var model)
                && (key.Role != Role.SentimentAnalyst || model.LiveSearch);

            if (!scored.TryGetValue(key.Role, out var ofRole))
            {
                scored[key.Role] = ofRole = [];
            }

            ofRole.Add(new ScoredModel(slice, basis.Mode, candidate));
        }

        return new RoleRecommendations(
            market,
            timeframe,
            live.MinSamples,
            live.Damping,
            scored.ToDictionary(
                ofRole => ofRole.Key,
                ofRole => (IReadOnlyList<ScoredModel>)
                [
                    .. ofRole.Value
                        .OrderByDescending(model => model.Score)
                        .ThenByDescending(model => model.Slice.Measure.Samples)
                        .ThenBy(model => model.Slice.Model.Provider.Value, StringComparer.Ordinal)
                        .ThenBy(model => model.Slice.Model.Identifier, StringComparer.Ordinal),
                ]));

        Dictionary<(Role Role, Guid ModelId), RoleStrength> SlicesOf(RoleStrengthView view) =>
            view.Slices
                .Where(slice => slice.Market == market && slice.Timeframe == timeframe)
                .ToDictionary(slice => (slice.Role, slice.Model.Id));
    }

    /// <summary>The strongest candidate of a role, or null when no model that the role can be given has a score.</summary>
    public ScoredModel? StrongestFor(Role role) => ScoredIn(role).FirstOrDefault(model => model.Candidate);

    /// <summary>A model with its score in a role, or null when it has none there, or when no model is asked about.</summary>
    public ScoredModel? Of(Role role, Guid? modelId) =>
        modelId is { } id ? ScoredIn(role).FirstOrDefault(model => model.ModelId == id) : null;

    /// <summary>
    /// The model that a role is recommended in place of the one it has: its strongest candidate,
    /// when that scores above the model that the role has. A model with no score counts as 0,
    /// and so does a role with no model. Null when the role is to stay as it is: it has its
    /// strongest candidate already, the candidate does not score above what it has, or there is
    /// no candidate.
    /// </summary>
    /// <param name="role">The role.</param>
    /// <param name="currentModelId">The model that the role has now, or null when it has none.</param>
    public ScoredModel? RecommendedFor(Role role, Guid? currentModelId)
    {
        if (StrongestFor(role) is not { } strongest || strongest.ModelId == currentModelId)
        {
            return null;
        }

        // What is not measured is neither strong nor weak: the formula gives no sample a score of 0.
        var current = Of(role, currentModelId)?.Score ?? 0m;

        return strongest.Score > current ? strongest : null;
    }

    private IReadOnlyList<ScoredModel> ScoredIn(Role role) => Scored.GetValueOrDefault(role, []);
}
