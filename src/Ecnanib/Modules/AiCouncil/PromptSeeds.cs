using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;

namespace Ecnanib.Modules.AiCouncil;

/// <summary>
/// The initial templates (FR-PRM-04), one per layer's own dimension: the system layer, an output contract and a role
/// template for each role, an overlay for each market and each timeframe. Every combination resolves through these
/// (FR-PRM-02). The text is in Templates/*.md, embedded in the assembly, so it reads as it will be sent.
/// </summary>
internal static class PromptSeeds
{
    private const string ResourcePrefix = "Ecnanib.Prompts.";

    private static readonly Lazy<IReadOnlyList<(PromptTemplateKey Key, string Content)>> Seeds = new(Load);

    public static IReadOnlyList<(PromptTemplateKey Key, string Content)> All => Seeds.Value;

    public static IEnumerable<(PromptTemplateKey Key, string File)> Files()
    {
        yield return (new PromptTemplateKey(PromptLayer.System), "System");

        foreach (var role in Enum.GetValues<AiRole>())
        {
            yield return (new PromptTemplateKey(PromptLayer.OutputContract, Role: role), $"OutputContract.{role}");
            yield return (new PromptTemplateKey(PromptLayer.Role, Role: role), $"Role.{role}");
        }

        foreach (var market in Enum.GetValues<Market>())
        {
            yield return (new PromptTemplateKey(PromptLayer.Market, Market: market), $"Market.{market}");
        }

        foreach (var timeframe in Enum.GetValues<Timeframe>())
        {
            yield return (new PromptTemplateKey(PromptLayer.Timeframe, Timeframe: timeframe), $"Timeframe.{timeframe}");
        }
    }

    /// <exception cref="InvalidOperationException">A template's file isn't embedded, which stops startup.</exception>
    private static IReadOnlyList<(PromptTemplateKey Key, string Content)> Load()
    {
        var assembly = typeof(PromptSeeds).Assembly;

        return Files()
            .Select(seed =>
            {
                var name = $"{ResourcePrefix}{seed.File}.md";
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"The prompt template {name} isn't embedded in the assembly.");
                using var reader = new StreamReader(stream);

                // A checkout on Windows may have CRLF line ends, which shouldn't reach a provider.
                return (seed.Key, reader.ReadToEnd().ReplaceLineEndings("\n").Trim());
            })
            .ToList();
    }
}
