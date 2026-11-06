using System.Globalization;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.AiCouncil;

/// <summary>
/// What a contract asks that the schema subset can't say: ranges, lengths, and fields that one action needs and
/// another doesn't. An answer that's out of range, ambiguous or unusable is a violation. A field the action doesn't
/// use is cleared rather than refused, because a retry costs the user money and the field would be ignored anyway.
/// Blank items in a list of text are dropped for the same reason. Whether prices make sense against the market is
/// the Risk Engine's to decide, not this. See Output contracts in docs/architecture.md.
/// </summary>
internal static class ContractRules
{
    public static RoleOutput Check(RoleOutput output, ContractContext context, Violations violations) => output switch
    {
        TechnicalAssessment technical => Technical(technical, violations),
        SentimentAssessment sentiment => Sentiment(sentiment, violations),
        TradeProposal proposal => Proposal(proposal, context, violations),
        CriticVerdict verdict => Verdict(verdict, context, violations),
        TradeReflection reflection => Reflection(reflection, violations),
        _ => throw new ArgumentException($"{output.GetType().Name} isn't a role's output.", nameof(output)),
    };

    private static TechnicalAssessment Technical(TechnicalAssessment technical, Violations violations)
    {
        Positive(technical.SupportLevels, "$.support_levels", violations);
        Positive(technical.ResistanceLevels, "$.resistance_levels", violations);

        for (var index = 0; index < technical.Signals.Count; index++)
        {
            var path = SchemaNode.Item("$.signals", index);
            NotBlank(technical.Signals[index].Name, SchemaNode.Child(path, "name"), violations);
            Unit(technical.Signals[index].Strength, SchemaNode.Child(path, "strength"), violations);
        }

        Unit(technical.Confidence, "$.confidence", violations);
        Summary(technical.Summary, violations);

        return technical;
    }

    private static SentimentAssessment Sentiment(SentimentAssessment sentiment, Violations violations)
    {
        if (sentiment.Score is < -1m or > 1m)
        {
            violations.Add("$.score", "must be from -1 to 1");
        }

        for (var index = 0; index < sentiment.Sources.Count; index++)
        {
            var path = SchemaNode.Item("$.sources", index);
            NotBlank(sentiment.Sources[index].Title, SchemaNode.Child(path, "title"), violations);

            if (!Uri.TryCreate(sentiment.Sources[index].Url, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            {
                violations.Add(SchemaNode.Child(path, "url"), "must be an absolute http or https URL");
            }
        }

        Unit(sentiment.Confidence, "$.confidence", violations);
        Summary(sentiment.Summary, violations);

        return sentiment with { Drivers = Texts(sentiment.Drivers) };
    }

    private static TradeProposal Proposal(TradeProposal proposal, ContractContext context, Violations violations)
    {
        var action = $"`{ContractNames.Action(proposal.Action)}`";

        NotBlank(proposal.Rationale, "$.rationale", violations);
        Unit(proposal.Confidence, "$.confidence", violations);

        if (TradeActions.IsEntry(proposal.Action))
        {
            var side = proposal.Action == TradeAction.OpenShort ? PositionSide.Short : PositionSide.Long;

            if (proposal.Side is null)
            {
                violations.Add("$.side", $"is required for {action}");
            }
            else if (proposal.Side != side)
            {
                violations.Add("$.side", $"must be \"{ContractNames.Of(side)}\" for {action}");
            }

            Required(proposal.EntryType, "$.entry_type", action, violations);
            Required(proposal.StopPrice, "$.stop_price", action, violations);
            Positive(proposal.StopPrice, "$.stop_price", violations);
            Positive(proposal.TargetPrices, "$.target_prices", violations);

            if (proposal.EntryType == EntryType.Limit)
            {
                Required(proposal.EntryPrice, "$.entry_price", "a limit entry", violations);
                Positive(proposal.EntryPrice, "$.entry_price", violations);
                Required(proposal.LimitTtlCandles, "$.limit_ttl_candles", "a limit entry", violations);

                if (proposal.LimitTtlCandles < 1)
                {
                    violations.Add("$.limit_ttl_candles", "must be at least 1");
                }
            }

            Required(proposal.RiskPercent, "$.risk_percent", action, violations);
            Positive(proposal.RiskPercent, "$.risk_percent", violations);

            if (proposal.RiskPercent > context.MaxRiskPercent)
            {
                violations.Add("$.risk_percent", $"must be at most {context.MaxRiskPercent.Value.ToString(CultureInfo.InvariantCulture)}, the risk settings' maximum risk per trade");
            }

            if (string.IsNullOrWhiteSpace(proposal.Invalidation))
            {
                violations.Add("$.invalidation", $"is required for {action}");
            }

            return proposal.EntryType == EntryType.Limit
                ? proposal with { CloseFraction = null }
                : proposal with { EntryPrice = null, LimitTtlCandles = null, CloseFraction = null };
        }

        var cleared = proposal with
        {
            Side = null,
            EntryType = null,
            EntryPrice = null,
            StopPrice = null,
            TargetPrices = [],
            RiskPercent = null,
            LimitTtlCandles = null,
            CloseFraction = null,
        };

        switch (proposal.Action)
        {
            case TradeAction.Sell or TradeAction.Close:
                if (proposal.CloseFraction is <= 0m or > 1m)
                {
                    violations.Add("$.close_fraction", "must be above 0 and at most 1, or null to close all of it");
                }

                return cleared with { CloseFraction = proposal.CloseFraction };

            case TradeAction.AdjustStop:
                Required(proposal.StopPrice, "$.stop_price", action, violations);
                Positive(proposal.StopPrice, "$.stop_price", violations);
                return cleared with { StopPrice = proposal.StopPrice };

            case TradeAction.AdjustTarget:
                Positive(proposal.TargetPrices, "$.target_prices", violations);
                return cleared with { TargetPrices = proposal.TargetPrices };

            default:
                return cleared;
        }
    }

    /// <remarks>
    /// Against <see cref="ContractContext.Proposal"/>, each change must reduce risk (Appendix B). The side comes from
    /// the proposal for an entry, and from the position otherwise. Without either, the stop and targets can only be
    /// checked as prices, and the Risk Engine still refuses a stop that widens.
    /// </remarks>
    private static CriticVerdict Verdict(CriticVerdict verdict, ContractContext context, Violations violations)
    {
        NotBlank(verdict.Rationale, "$.rationale", violations);

        var cleaned = verdict with { Concerns = Texts(verdict.Concerns) };

        if (verdict.Verdict != VerdictKind.Modify)
        {
            if (verdict.Modifications is not null)
            {
                violations.Add("$.modifications", $"must be null when the verdict is \"{ContractNames.Of(verdict.Verdict)}\"");
            }

            return cleaned;
        }

        if (verdict.Modifications is not { } changes)
        {
            violations.Add("$.modifications", "is required when the verdict is \"modify\"");
            return cleaned;
        }

        if (changes is { StopPrice: null, TargetPrices.Count: 0, RiskPercent: null })
        {
            violations.Add("$.modifications", "must change at least one of stop_price, target_prices and risk_percent");
            return cleaned;
        }

        Positive(changes.StopPrice, "$.modifications.stop_price", violations);
        Positive(changes.TargetPrices, "$.modifications.target_prices", violations);
        Positive(changes.RiskPercent, "$.modifications.risk_percent", violations);

        if (context.Proposal is not { } proposal)
        {
            return cleaned;
        }

        var side = proposal.Side ?? context.PositionSide;

        if (changes.StopPrice is { } stop)
        {
            if (proposal.StopPrice is not { } proposed)
            {
                violations.Add("$.modifications.stop_price", "must be null, because the proposal has no stop to tighten");
            }
            else if ((side == PositionSide.Long && stop <= proposed) || (side == PositionSide.Short && stop >= proposed))
            {
                violations.Add("$.modifications.stop_price", $"must be tighter than the proposal's stop: {(side == PositionSide.Long ? "higher, for a long" : "lower, for a short")}");
            }
        }

        if (changes.RiskPercent is { } risk)
        {
            if (proposal.RiskPercent is not { } proposed)
            {
                violations.Add("$.modifications.risk_percent", "must be null, because the proposal asks for no risk percent");
            }
            else if (risk >= proposed)
            {
                violations.Add("$.modifications.risk_percent", "must be lower than the proposal's risk percent");
            }
        }

        if (proposal.TargetPrices.Count > 0 && side is { } targetSide)
        {
            var farthest = targetSide == PositionSide.Long ? proposal.TargetPrices.Max() : proposal.TargetPrices.Min();

            for (var index = 0; index < changes.TargetPrices.Count; index++)
            {
                var target = changes.TargetPrices[index];
                if ((targetSide == PositionSide.Long && target > farthest) || (targetSide == PositionSide.Short && target < farthest))
                {
                    violations.Add(SchemaNode.Item("$.modifications.target_prices", index), "must be no farther than the proposal's farthest target");
                }
            }
        }

        return cleaned;
    }

    private static TradeReflection Reflection(TradeReflection reflection, Violations violations)
    {
        NotBlank(reflection.SetupSummary, "$.setup_summary", violations);
        NotBlank(reflection.Lesson, "$.lesson", violations);

        return reflection with
        {
            WhatWorked = Texts(reflection.WhatWorked),
            WhatFailed = Texts(reflection.WhatFailed),
            Tags = Texts(reflection.Tags),
        };
    }

    private static void Unit(decimal value, string path, Violations violations)
    {
        if (value is < 0m or > 1m)
        {
            violations.Add(path, "must be from 0 to 1");
        }
    }

    private static void Positive(decimal? value, string path, Violations violations)
    {
        if (value <= 0m)
        {
            violations.Add(path, "must be above 0");
        }
    }

    private static void Positive(IReadOnlyList<decimal> values, string path, Violations violations)
    {
        for (var index = 0; index < values.Count; index++)
        {
            Positive(values[index], SchemaNode.Item(path, index), violations);
        }
    }

    private static void Required<T>(T? value, string path, string what, Violations violations)
        where T : struct
    {
        if (value is null)
        {
            violations.Add(path, $"is required for {what}");
        }
    }

    private static void NotBlank(string value, string path, Violations violations)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            violations.Add(path, "can't be blank");
        }
    }

    private static void Summary(string summary, Violations violations)
    {
        NotBlank(summary, "$.summary", violations);

        if (summary.Length > OutputContracts.SummaryMaxLength)
        {
            violations.Add("$.summary", $"must be at most {OutputContracts.SummaryMaxLength} characters");
        }
    }

    private static IReadOnlyList<string> Texts(IReadOnlyList<string> texts) => [.. texts.Where(text => !string.IsNullOrWhiteSpace(text))];
}
