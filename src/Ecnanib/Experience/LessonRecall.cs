namespace Ecnanib.Experience;

/// <summary>
/// Which lessons a prompt recalls (FR-EXP-7): those of the agent first, then those of the user's
/// other agents on the same symbol, market, and timeframe, up to a count, within a fixed size
/// budget. It is a rule only: it reads nothing. See "Recalling lessons" in docs/experience.md.
/// </summary>
public static class LessonRecall
{
    /// <summary>
    /// The size budget of the lessons of one prompt, in characters of the lessons themselves. It
    /// is fixed, and is no setting. A lesson is at most about half of it, so one always fits.
    /// </summary>
    public const int BudgetCharacters = 4000;

    /// <summary>The lessons that are recalled, in the order a prompt numbers them in.</summary>
    /// <param name="ofAgent">The lessons of the agent that the prompt is for, the most recent first.</param>
    /// <param name="ofOthers">Those of the user's other agents on the same symbol, market, and timeframe, the most recent first.</param>
    /// <param name="most">The most lessons of a prompt: <c>Experience:LessonsPerPrompt</c>.</param>
    public static IReadOnlyList<string> Of(IEnumerable<string> ofAgent, IEnumerable<string> ofOthers, int most)
    {
        ArgumentNullException.ThrowIfNull(ofAgent);
        ArgumentNullException.ThrowIfNull(ofOthers);

        var recalled = new List<string>();
        var left = BudgetCharacters;

        foreach (var lesson in ofAgent.Concat(ofOthers))
        {
            // A lesson is recalled whole or not at all: cut short, it would say something else.
            // The first that does not fit ends the recall, so an older one never stands in for
            // a more recent one.
            if (recalled.Count >= most || lesson.Length > left)
            {
                break;
            }

            recalled.Add(lesson);
            left -= lesson.Length;
        }

        return recalled;
    }
}
