namespace Ecnanib.Catalog;

/// <summary>
/// A pattern of the configuration file that names models of a provider: in the lists that say
/// which models can be selected and which search live (FR-CAT-6), and in the list that says
/// what their tokens cost (FR-CAT-11). See docs/catalog.md.
/// </summary>
/// <remarks>
/// A pattern is the identifier of a model, in which <c>*</c> stands for any run of characters,
/// an empty one included. Nothing else in it is special, and letter case is ignored.
/// </remarks>
internal static class ModelPattern
{
    /// <summary>
    /// Whether a pattern names an identifier. It goes through both once, and goes back only to
    /// the last <c>*</c>, so an identifier that a provider wrote cannot make it take long.
    /// </summary>
    /// <param name="pattern">The pattern, as the file writes it.</param>
    /// <param name="identifier">The provider's identifier of a model.</param>
    public static bool Names(string pattern, string identifier)
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
