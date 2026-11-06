using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;

namespace Ecnanib.Tests.Council;

/// <summary>An answer that conforms to each role's contract on Futures, and ways to break one.</summary>
internal static class ContractSamples
{
    public const string Technical = """
        {"trend":"up","htf_trend":"up","support_levels":[64000.5,63500],"resistance_levels":[66000.25],
         "signals":[{"name":"ema20 above ema50","direction":"bullish","strength":0.7}],
         "volatility_regime":"normal","setup_present":true,"confidence":0.65,"summary":"Uptrend holding above the 20 EMA."}
        """;

    public const string Sentiment = """
        {"score":0.35,"drivers":["ETF inflows"],
         "sources":[{"title":"ETF inflows rise","url":"https://news.example/etf","published_at":"2026-11-05T14:00:00Z"}],
         "data_freshness":"2026-11-05T14:00:00+01:00","confidence":0.5,"summary":"Mildly bullish."}
        """;

    /// <summary>A limit long on Futures.</summary>
    public const string Proposal = """
        {"action":"OPEN_LONG","side":"long","entry_type":"limit","entry_price":64250.5,"stop_price":63500,
         "target_prices":[66000,67500.75],"risk_percent":0.8,"limit_ttl_candles":4,"close_fraction":null,
         "invalidation":"A close below 63500.","rationale":"Pullback to support in an uptrend.","confidence":0.6}
        """;

    /// <summary>A modification of <see cref="Proposal"/> that reduces its risk.</summary>
    public const string Verdict = """
        {"verdict":"modify","modifications":{"stop_price":63800,"target_prices":[],"risk_percent":0.5},
         "concerns":["Funding is elevated."],
         "checklist":{"trend_alignment":{"result":"pass","note":"Aligned."},"reward_to_risk":{"result":"pass","note":"2.3R."},
          "event_risk":{"result":"pass","note":"None."},"funding":{"result":"fail","note":"High."},
          "reflections":{"result":"not_applicable","note":"None yet."}},
         "rationale":"A good trade with less risk."}
        """;

    public const string Reflection = """
        {"setup_summary":"A pullback long at support.","outcome":{"r_multiple":1.8,"pnl":42.5},
         "what_worked":["Entry at support"],"what_failed":[],"lesson":"Wait for the retest.","tags":["pullback"]}
        """;

    /// <summary>The checks a role's sample meets: the Strategist's needs a maximum risk, and the Risk Critic's a proposal.</summary>
    public static ContractContext ContextFor(AiRole role, Market market = Market.Futures) => role switch
    {
        AiRole.Strategist => new ContractContext(market) { MaxRiskPercent = 1m },
        AiRole.RiskCritic => new ContractContext(market) { Proposal = ProposalOutput() },
        _ => new ContractContext(market),
    };

    public static string For(AiRole role) => role switch
    {
        AiRole.TechnicalAnalyst => Technical,
        AiRole.SentimentAnalyst => Sentiment,
        AiRole.Strategist => Proposal,
        AiRole.RiskCritic => Verdict,
        _ => Reflection,
    };

    /// <summary><see cref="Proposal"/>, read.</summary>
    public static TradeProposal ProposalOutput() =>
        (TradeProposal)OutputContracts.Check(AiRole.Strategist, Proposal, new ContractContext(Market.Futures) { MaxRiskPercent = 1m }).Output!;

    /// <summary>
    /// <paramref name="json"/> with the value at <paramref name="path"/>, such as <c>signals[0].strength</c>, set to
    /// <paramref name="value"/>, which is JSON, or removed when it's null.
    /// </summary>
    public static string With(string json, string path, string? value)
    {
        var root = JsonNode.Parse(json)!;
        var parts = path.Split('.');
        var parent = parts[..^1].Aggregate(root, Step);
        var last = parts[^1];

        if (last.EndsWith(']'))
        {
            var (name, index) = Indexed(last);
            parent[name]!.AsArray()[index] = value is null ? null : JsonNode.Parse(value);
        }
        else if (value is null)
        {
            parent.AsObject().Remove(last);
        }
        else
        {
            parent[last] = JsonNode.Parse(value);
        }

        return root.ToJsonString();
    }

    /// <summary><paramref name="text"/> as a JSON string.</summary>
    public static string Quoted(string text) => JsonSerializer.Serialize(text);

    private static JsonNode Step(JsonNode node, string part)
    {
        if (!part.EndsWith(']'))
        {
            return node[part]!;
        }

        var (name, index) = Indexed(part);
        return node[name]![index]!;
    }

    private static (string Name, int Index) Indexed(string part)
    {
        var open = part.IndexOf('[', StringComparison.Ordinal);

        return (part[..open], int.Parse(part[(open + 1)..^1], CultureInfo.InvariantCulture));
    }
}
