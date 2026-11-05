using System.Globalization;
using Ecnanib.Kernel;

namespace Ecnanib.Modules.AiCouncil.Contracts;

/// <summary>
/// Composes prompts from the templates in use (FR-PRM-01, FR-PRM-02). See Prompts in docs/architecture.md.
/// </summary>
public interface IPrompts
{
    /// <summary>The prompt for one role's call, from the highest version of each template.</summary>
    /// <exception cref="PromptTemplateMissingException">A layer has no template for the combination.</exception>
    /// <exception cref="ArgumentException">The context's snapshot is for another market or timeframe.</exception>
    Task<ComposedPrompt> ComposeAsync(PromptRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A prompt ready for <c>AiRequest</c>. Its ToString shows no text (FR-LOG-07).
/// </summary>
/// <param name="Instructions">
/// Layers 1 to 4: system, output contract, role, market and timeframe. Administrators write them, so they go in
/// <c>AiRequest.Instructions</c>, which providers keep apart from the conversation.
/// </param>
/// <param name="Message">
/// Layers 5 and 6: the custom instructions, delimited, then the runtime context. This is the first user message, so
/// nothing a user or a website wrote reaches the instructions (FR-PRM-05, FR-PRM-06).
/// </param>
/// <param name="Templates">The template versions used, in layer order, which the AI call records (FR-PRM-03).</param>
public sealed record ComposedPrompt(string Instructions, string Message, IReadOnlyList<PromptTemplateVersion> Templates)
{
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{nameof(ComposedPrompt)}, {Instructions.Length} + {Message.Length} characters, {Templates.Count} templates");
}

/// <summary>
/// How many closed candles of the agent's timeframe, and of its higher timeframe, go into each role's prompt. Every
/// role that gets a snapshot gets its indicators and the latest prices, and raw candles cost tokens, which the user
/// pays for, so the Technical Analyst gets the most. The Sentiment Analyst reads the news, and the Reviewer gets the
/// trade's own history (#45).
/// </summary>
public static class PromptWindows
{
    public static (int Candles, int HigherTimeframe) For(AiRole role) => role switch
    {
        AiRole.TechnicalAnalyst => (120, 60),
        AiRole.Strategist or AiRole.RiskCritic => (24, 12),
        _ => (0, 0),
    };
}
