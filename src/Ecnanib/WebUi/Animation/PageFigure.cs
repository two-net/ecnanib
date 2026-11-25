namespace Ecnanib.WebUi.Animation;

/// <summary>
/// A text of a page in which one number is a figure that changes: a price, a profit or a loss,
/// an equity (FR-ANI-2). The page sets the number apart where it stands in the text, so the
/// script of the pages can count it from its old value to its new one, and the words around it
/// stay as they are written. See docs/motion.md.
/// </summary>
/// <remarks>
/// The value is worked out from the number as it is written, and from nothing else, so what
/// the script is told and what the page says cannot disagree.
/// </remarks>
public sealed record PageFigure
{
    private PageFigure(string before, string number, string value, string after)
    {
        Before = before;
        Number = number;
        Value = value;
        After = after;
    }

    /// <summary>What the text says before the number.</summary>
    public string Before { get; }

    /// <summary>The number as the page writes it: <c>1012.4</c>, <c>+12.4</c>, <c>-5.99</c>.</summary>
    public string Number { get; }

    /// <summary>
    /// The number as a script reads it: digits and a decimal point, with a minus for what is
    /// less than nothing and no plus. It is the value of the element that holds the number.
    /// </summary>
    public string Value { get; }

    /// <summary>What the text says after the number.</summary>
    public string After { get; }

    /// <summary>The text whole, as a page without the figure set apart would say it.</summary>
    public string Text => Before + Number + After;

    /// <summary>A text with the figure that it holds.</summary>
    /// <param name="number">The number as the page writes it, with its sign where the text writes one.</param>
    /// <param name="before">What the text says before it.</param>
    /// <param name="after">What the text says after it.</param>
    /// <param name="isLoss">
    /// True when the words around the number say that it is a loss, and the number itself is
    /// written without a sign: "would lose 5.99". Its value is then less than nothing.
    /// </param>
    public static PageFigure Of(string number, string before = "", string after = "", bool isLoss = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(number);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var unsigned = number.TrimStart('+');

        return new PageFigure(before, number, isLoss && !unsigned.StartsWith('-') ? $"-{unsigned}" : unsigned, after);
    }
}
