using Ecnanib.Catalog;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// What some calls of AI models add up to: how many there were, what their providers counted
/// for them, and what that is estimated to cost where the price is known (FR-RPT-3, FR-KEY-11).
/// It is the one place that says what is known of the usage and what is not. See "AI usage" in
/// docs/team.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is not known is null, and never zero.</b> A provider that did not count the tokens
/// of a call, because the call timed out or failed, may charge for it all the same: such a
/// call is among the calls, and among those that were not counted. The tokens are of the calls
/// that were counted, and are null when none was.
/// </para>
/// <para>
/// <b>The cost is an estimate of the tokens that were counted,</b> at the price that the
/// configuration file gives each model now (FR-CAT-11). It is null when no call was counted, and
/// when a model that was counted has no price: a sum that leaves a model out would read as the
/// whole, so there is none. It holds nothing for the calls that were not counted, and nothing
/// for the live searches, which a provider charges apart.
/// </para>
/// </remarks>
/// <param name="Calls">How many calls were made.</param>
/// <param name="NotCounted">How many of them their provider counted no tokens for.</param>
/// <param name="InputTokens">The tokens of the prompts of the calls that were counted, or null when none was.</param>
/// <param name="OutputTokens">The tokens of the answers of the calls that were counted, or null when none was.</param>
/// <param name="LiveSearches">How many live searches the providers ran for the calls (FR-CYC-3).</param>
/// <param name="EstimatedCost">What the counted tokens are estimated to cost, in US dollars, or null when it is not known.</param>
public sealed record UsageFigures(
    int Calls,
    int NotCounted,
    long? InputTokens,
    long? OutputTokens,
    long LiveSearches,
    decimal? EstimatedCost)
{
    /// <summary>What the calls of some models add up to.</summary>
    /// <param name="models">The calls of each model, with the price of the model, in any order.</param>
    public static UsageFigures Of(IEnumerable<ModelUsage> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        var calls = 0;
        var counted = 0;
        var input = 0L;
        var output = 0L;
        var searches = 0L;
        var cost = 0m;
        var priced = true;

        foreach (var model in models)
        {
            calls += model.Calls;
            counted += model.Counted;
            input += model.InputTokens;
            output += model.OutputTokens;
            searches += model.LiveSearches;

            // A model that was not counted adds no tokens, so its price is not asked for.
            if (model.Counted == 0)
            {
                continue;
            }

            if (model.Price is { } price)
            {
                cost += price.CostOf(model.InputTokens, model.OutputTokens);
            }
            else
            {
                priced = false;
            }
        }

        return new UsageFigures(
            calls,
            calls - counted,
            counted > 0 ? input : null,
            counted > 0 ? output : null,
            searches,
            counted > 0 && priced ? cost : null);
    }
}

/// <summary>The calls of one model among those that are added up, as the database counted them.</summary>
/// <param name="Calls">How many calls of the model were made.</param>
/// <param name="Counted">How many of them the provider counted tokens for.</param>
/// <param name="InputTokens">The tokens of their prompts, and 0 when none was counted.</param>
/// <param name="OutputTokens">The tokens of their answers, and 0 when none was counted.</param>
/// <param name="LiveSearches">How many live searches the provider ran for them.</param>
/// <param name="Price">The price of the model, or null when the configuration file gives it none.</param>
public sealed record ModelUsage(int Calls, int Counted, long InputTokens, long OutputTokens, long LiveSearches, ModelPrice? Price);
