using Ecnanib.Kernel;

namespace Ecnanib.Modules.AiCouncil.Contracts;

/// <summary>
/// Composes a prompt from templates and a request (FR-PRM-01), reading nothing else, so the same arguments always
/// give the same prompt. <see cref="IPrompts"/> passes the stored templates, and a preview can pass drafts with
/// sample data (FR-PRM-03). See Prompts in docs/architecture.md.
/// </summary>
public static class PromptComposer
{
    /// <summary>The stored layers, in the order they're composed.</summary>
    public static IReadOnlyList<PromptLayer> Layers { get; } = Enum.GetValues<PromptLayer>();

    /// <exception cref="PromptTemplateMissingException">A layer has no template for the combination.</exception>
    /// <exception cref="ArgumentException">
    /// Two templates have the same key and version, or the context's snapshot is for another market or timeframe.
    /// </exception>
    public static ComposedPrompt Compose(IEnumerable<PromptTemplate> templates, PromptRequest request)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(request);

        var all = templates.ToList();
        var duplicate = all.GroupBy(template => (template.Key, template.Version)).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Two templates are version {duplicate.Key.Version} of {duplicate.Key.Key}.", nameof(templates));
        }

        if (request.Context.Snapshot is { } snapshot && (snapshot.Market != request.Market || snapshot.Timeframe != request.Timeframe))
        {
            throw new ArgumentException("The snapshot is for another market or timeframe than the prompt.", nameof(request));
        }

        var used = Layers.Select(layer => Resolve(all, layer, request.Role, request.Market, request.Timeframe)).ToList();

        return new ComposedPrompt(
            string.Join("\n\n", used.Select(template => template.Content.Trim())),
            ContextRenderer.Render(request),
            used.Select(template => template.ToVersion()).ToList());
    }

    /// <summary>
    /// The template a layer uses for a combination: of those that match it, the most specific key, at its highest
    /// version (FR-PRM-02).
    /// </summary>
    /// <exception cref="PromptTemplateMissingException">None matches.</exception>
    public static PromptTemplate Resolve(
        IEnumerable<PromptTemplate> templates,
        PromptLayer layer,
        AiRole role,
        Market market,
        Timeframe timeframe)
    {
        ArgumentNullException.ThrowIfNull(templates);

        return templates
            .Where(template => template.Key.Layer == layer && template.Key.Matches(role, market, timeframe))
            .OrderByDescending(template => template.Key.Specificity)
            .ThenByDescending(template => template.Version)
            .FirstOrDefault()
            ?? throw new PromptTemplateMissingException(layer, role, market, timeframe);
    }
}
