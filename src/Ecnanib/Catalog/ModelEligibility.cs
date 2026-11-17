using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.Catalog;

/// <summary>
/// Which models may be selected for an agent, and which of them can search live, as the
/// configuration file says for each provider (FR-CAT-6). The providers do not report what their
/// models can do in one way, so the file says it, with patterns. See docs/catalog.md.
/// </summary>
/// <remarks>
/// <para>
/// A model is eligible when a pattern of its provider's <c>Allow</c> list names it and no
/// pattern of the <c>Deny</c> list does. A provider with no <c>Allow</c> pattern, or with no
/// entry in the file, has no eligible model: a model is selected because someone allowed it, and
/// never because nobody thought of it.
/// </para>
/// <para>
/// A pattern is the identifier of a model, in which <c>*</c> stands for any run of characters,
/// an empty one included. Nothing else in it is special, and letter case is ignored.
/// </para>
/// <para>
/// Nothing of this is stored. The file is read once, as the application starts, so what it says
/// of a model is the same for as long as the application runs, and a pattern that was changed
/// holds for every model of the catalog from the next start, with no sync in between.
/// </para>
/// </remarks>
internal sealed class ModelEligibility(AiProvidersSettings providers) : IModelCapabilities
{
    /// <summary>Whether a model may be selected, as far as the file goes: its status is the catalog's to say.</summary>
    /// <param name="provider">The provider that offers the model.</param>
    /// <param name="identifier">The provider's identifier of the model.</param>
    public bool IsEligible(AiProviderCode provider, string identifier) =>
        providers.ByCode.TryGetValue(provider, out var entry)
        && Names(entry.Allow, identifier)
        && !Names(entry.Deny, identifier);

    /// <summary>Whether the file says of a model that it can search live (FR-CYC-3).</summary>
    /// <param name="provider">The provider that offers the model.</param>
    /// <param name="identifier">The provider's identifier of the model.</param>
    public bool SearchesLive(AiProviderCode provider, string identifier) =>
        providers.ByCode.TryGetValue(provider, out var entry) && Names(entry.LiveSearch, identifier);

    private static bool Names(IReadOnlyList<string> patterns, string identifier)
    {
        foreach (var pattern in patterns)
        {
            if (Matches(pattern, identifier))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a pattern names an identifier. It goes through both once, and goes back only to
    /// the last <c>*</c>, so an identifier that a provider wrote cannot make it take long.
    /// </summary>
    private static bool Matches(string pattern, string identifier)
    {
        var inPattern = 0;
        var inIdentifier = 0;

        // The last star that was passed, and how much of the identifier it stands for so far.
        var star = -1;
        var afterStar = 0;

        while (inIdentifier < identifier.Length)
        {
            if (inPattern < pattern.Length && pattern[inPattern] == '*')
            {
                star = inPattern++;
                afterStar = inIdentifier;
            }
            else if (inPattern < pattern.Length && Same(pattern[inPattern], identifier[inIdentifier]))
            {
                inPattern++;
                inIdentifier++;
            }
            else if (star >= 0)
            {
                // The star stands for one character more, and what follows it is tried again.
                inPattern = star + 1;
                inIdentifier = ++afterStar;
            }
            else
            {
                return false;
            }
        }

        // The identifier has ended. What is left of the pattern must stand for nothing.
        while (inPattern < pattern.Length && pattern[inPattern] == '*')
        {
            inPattern++;
        }

        return inPattern == pattern.Length;
    }

    private static bool Same(char one, char other) =>
        one == other || char.ToUpperInvariant(one) == char.ToUpperInvariant(other);
}
