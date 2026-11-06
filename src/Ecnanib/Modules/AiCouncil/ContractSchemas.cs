using System.Collections.Frozen;
using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.AiCouncil;

/// <summary>
/// Appendix B's five contracts. Every name is the one the role's OutputContract template uses, which a test checks,
/// so the prose and the schema can't drift. The Strategist's actions are the market's, so a provider that enforces
/// the schema enforces FR-CYC-03 too. The Strategist's <c>close_fraction</c> isn't in Appendix B: it's the share
/// that FR-CYC-03's partial close needs.
/// </summary>
internal static class ContractSchemas
{
    private static readonly FrozenDictionary<(AiRole, Market), (SchemaNode Node, AiOutputSchema Schema)> All =
        (from role in Enum.GetValues<AiRole>()
         from market in Enum.GetValues<Market>()
         select (role, market))
        .ToFrozenDictionary(key => key, key => Build(key.role, key.market));

    public static SchemaNode NodeFor(AiRole role, Market market) => Find(role, market).Node;

    public static AiOutputSchema For(AiRole role, Market market) => Find(role, market).Schema;

    private static (SchemaNode Node, AiOutputSchema Schema) Find(AiRole role, Market market) =>
        All.TryGetValue((role, market), out var found)
            ? found
            : throw new ArgumentOutOfRangeException(nameof(role), $"There's no output contract for {role} on {market}.");

    private static (SchemaNode, AiOutputSchema) Build(AiRole role, Market market)
    {
        var (name, node) = role switch
        {
            AiRole.TechnicalAnalyst => ("technical_assessment", Technical()),
            AiRole.SentimentAnalyst => ("sentiment_assessment", Sentiment()),
            AiRole.Strategist => ("trade_proposal", Proposal(market)),
            AiRole.RiskCritic => ("risk_verdict", Verdict()),
            AiRole.Reviewer => ("reflection", Reflection()),
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Not a role."),
        };

        return (node, new AiOutputSchema(name, JsonSerializer.SerializeToElement(node.ToJson())));
    }

    private static SchemaNode Technical() => Schema.Object(
        ("trend", OneOf<Trend>()),
        ("htf_trend", OneOf<Trend>()),
        ("support_levels", Schema.Array(Schema.Number())),
        ("resistance_levels", Schema.Array(Schema.Number())),
        ("signals", Schema.Array(Schema.Object(
            ("name", Schema.String()),
            ("direction", OneOf<SignalDirection>()),
            ("strength", Schema.Number())))),
        ("volatility_regime", OneOf<VolatilityRegime>()),
        ("setup_present", Schema.Boolean()),
        ("confidence", Schema.Number()),
        ("summary", Schema.String()));

    private static SchemaNode Sentiment() => Schema.Object(
        ("score", Schema.Number()),
        ("drivers", Schema.Array(Schema.String())),
        ("sources", Schema.Array(Schema.Object(
            ("title", Schema.String()),
            ("url", Schema.String()),
            ("published_at", Schema.Nullable(Schema.Time()))))),
        ("data_freshness", Schema.Nullable(Schema.Time())),
        ("confidence", Schema.Number()),
        ("summary", Schema.String()));

    private static SchemaNode Proposal(Market market) => Schema.Object(
        ("action", Schema.Enum(TradeActions.For(market).Select(ContractNames.Action))),
        ("side", Schema.Nullable(OneOf<PositionSide>())),
        ("entry_type", Schema.Nullable(OneOf<EntryType>())),
        ("entry_price", Schema.Nullable(Schema.Number())),
        ("stop_price", Schema.Nullable(Schema.Number())),
        ("target_prices", Schema.Array(Schema.Number())),
        ("risk_percent", Schema.Nullable(Schema.Number())),
        ("limit_ttl_candles", Schema.Nullable(Schema.Integer())),
        ("close_fraction", Schema.Nullable(Schema.Number())),
        ("invalidation", Schema.Nullable(Schema.String())),
        ("rationale", Schema.String()),
        ("confidence", Schema.Number()));

    private static SchemaNode Verdict()
    {
        var item = Schema.Object(("result", OneOf<ChecklistResult>()), ("note", Schema.String()));

        return Schema.Object(
            ("verdict", OneOf<VerdictKind>()),
            ("modifications", Schema.Nullable(Schema.Object(
                ("stop_price", Schema.Nullable(Schema.Number())),
                ("target_prices", Schema.Array(Schema.Number())),
                ("risk_percent", Schema.Nullable(Schema.Number()))))),
            ("concerns", Schema.Array(Schema.String())),
            ("checklist", Schema.Object(
                ("trend_alignment", item),
                ("reward_to_risk", item),
                ("event_risk", item),
                ("funding", item),
                ("reflections", item))),
            ("rationale", Schema.String()));
    }

    private static SchemaNode Reflection() => Schema.Object(
        ("setup_summary", Schema.String()),
        ("outcome", Schema.Object(("r_multiple", Schema.Nullable(Schema.Number())), ("pnl", Schema.Number()))),
        ("what_worked", Schema.Array(Schema.String())),
        ("what_failed", Schema.Array(Schema.String())),
        ("lesson", Schema.String()),
        ("tags", Schema.Array(Schema.String())));

    private static SchemaNode OneOf<T>()
        where T : struct, Enum => Schema.Enum(Enum.GetValues<T>().Select(ContractNames.Of));
}
