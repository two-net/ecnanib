using Ecnanib.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecnanib.Tests.Texts.Planted;

// What TextRuleTests plants to show that the rule finds what it looks for: components that hold
// a text of their own, each in another place, and one that holds none though it has everything
// that looks like one. They are written as the compiler writes a page. Nothing else uses them.

/// <summary>A text class as the rule asks for one, but for its last member.</summary>
internal static class PlantedPages
{
    /// <summary>A text: a property, which a page asks for when it is written.</summary>
    public static string Title => "A title";

    /// <summary>A name that code uses: a constant, which is handed over where no text is.</summary>
    public const string Form = "planted-form";

    /// <summary>The mistake: a text as a constant, which the compiler copies into the page.</summary>
    public const string Copied = "Copied into the page";
}

/// <summary>A part that others are put into, with a parameter that is text and one that is none.</summary>
public sealed class PlantedShell : ComponentBase
{
    [Parameter]
    public string? Label { get; set; }

    [Parameter]
    public string? Selector { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, ChildContent);
}

/// <summary>Found: words between the tags of markup that the page holds as it is.</summary>
public sealed class PlantedMarkup : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder) =>
        builder.AddMarkupContent(0, "<h1 class=\"wide\">Sign in</h1>\n    ");
}

/// <summary>Found: the value of an attribute in such markup, of a cell that stands without its table.</summary>
public sealed class PlantedCell : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder) =>
        builder.AddMarkupContent(0, "<td id=\"why\" aria-label=\"Why it failed\">7 %</td>");
}

/// <summary>Found: words on either side of a value, each written as content.</summary>
public sealed class PlantedContent : ComponentBase
{
    [Parameter]
    public int Minutes { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "p");
        builder.AddContent(1, "Too many attempts. Try again in up to ");
        builder.AddContent(2, Minutes);
        builder.AddContent(3, " minutes.");
        builder.CloseElement();
    }
}

/// <summary>Found: a word in the value of an attribute that a person is read, joined with a value.</summary>
public sealed class PlantedAttribute : ComponentBase
{
    [Parameter]
    public string Name { get; set; } = string.Empty;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "button");
        builder.AddAttribute(1, "class", "primary wide");
        builder.AddAttribute(2, "aria-label", "Copy " + Name);
        builder.CloseElement();
    }
}

/// <summary>Found: words in what a page puts into a part, which the compiler makes a method of its own.</summary>
public sealed class PlantedFragment : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<PlantedShell>(0);
        builder.AddComponentParameter(1, "ChildContent", (RenderFragment)(inner => inner.AddContent(2, "Inside a part")));
        builder.CloseComponent();
    }
}

/// <summary>Found: words that a part is handed as a parameter. Not found: what it is handed as a parameter that is listed.</summary>
public sealed class PlantedParameter : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<PlantedShell>(0);
        builder.AddComponentParameter(1, "Label", "Shown to a person");
        builder.AddComponentParameter(2, "Selector", "main h1");
        builder.CloseComponent();
    }
}

/// <summary>
/// Found: a member of an enum that is written as it is, which writes its name. Not found: the
/// same as the value of an attribute that is listed, and as a parameter of a part, which takes
/// the member and writes nothing.
/// </summary>
public sealed class PlantedEnum : ComponentBase
{
    [Parameter]
    public TradingMode Mode { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "p");
        builder.AddAttribute(1, "data-mode", Mode);
        builder.AddContent(2, Mode);
        builder.CloseElement();
        builder.OpenComponent<PlantedShell>(3);
        builder.AddComponentParameter(4, "Mode", Mode);
        builder.CloseComponent();
    }
}

/// <summary>
/// Found: words in the code of a page. A sentence in a method that waits, which the compiler
/// moves into a type of its own, and words where a field is given its value: a capitalised
/// word, and capitals with digits.
/// </summary>
public sealed class PlantedCode : ComponentBase
{
    private readonly string[] _columns = ["Open", "High", "SMA20"];

    private string? _said;

    public int Columns => _columns.Length + (_said?.Length ?? 0);

    public async Task SaveAsync()
    {
        await Task.Yield();

        _said = "Nothing was saved.";
    }
}

/// <summary>Found: a constant of a text class, which the compiler copied into the page.</summary>
public sealed class PlantedConstant : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, PlantedPages.Copied);
}

/// <summary>
/// Not found, though everything in it looks like a text to something: a property of a text
/// class, a constant that names a form, the names of classes, an id that is put together, signs
/// and digits between two texts, what an exception and a log are told, a record whose members
/// the compiler writes out by their names, and the names of the code.
/// </summary>
public sealed class PlantedClean : ComponentBase
{
    private readonly ILogger _logger = NullLogger.Instance;

    [Parameter]
    public int Row { get; set; }

    [Parameter]
    public string Field { get; set; } = "email";

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "form");
        builder.AddAttribute(1, "class", Row > 0 ? "rows wide" : "rows");
        builder.AddAttribute(2, "id", "row-" + Row);
        builder.AddNamedEvent("onsubmit", PlantedPages.Form);
        builder.AddContent(3, PlantedPages.Title);
        builder.AddContent(4, ", ");
        builder.AddContent(5, Row);
        builder.AddContent(6, " %.");
        builder.AddMarkupContent(7, "<span class=\"figure\" id=\"sum\">100 % &times; 2</span>\n");
        builder.CloseElement();
    }

    public string Check(int state)
    {
        if (state < 0)
        {
            throw new InvalidOperationException("A page is written for a request, and there is none.");
        }

        if (state > Row)
        {
            throw new ArgumentOutOfRangeException(nameof(state), $"No such state: {state} of {Field}.");
        }

        _logger.LogWarning("The count of {Count} was not read", state);

        return new Entry(Field == "sign-in" ? "email" : "new-password", state).ToString();
    }

    private sealed record Entry(string Field, int Count);
}
