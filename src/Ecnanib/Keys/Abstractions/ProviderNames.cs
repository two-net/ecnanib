using Ecnanib.Ai.Abstractions;

namespace Ecnanib.Keys.Abstractions;

/// <summary>Display names of the providers as the SRS writes them (C-04); enum names elsewhere.</summary>
public static class ProviderNames
{
    public static string Display(AiProvider provider) => provider switch
    {
        AiProvider.Gpt => "GPT",
        _ => provider.ToString(),
    };

    /// <summary>Parses a route value such as <c>claude</c> or <c>GPT</c>; null for anything that is not a provider.</summary>
    public static AiProvider? Parse(string? value) =>
        Enum.TryParse<AiProvider>(value, ignoreCase: true, out var provider) && Enum.IsDefined(provider) ? provider : null;

    /// <summary>The lower-case route segment for a provider.</summary>
    public static string Route(AiProvider provider) => provider.ToString().ToLowerInvariant();
}
