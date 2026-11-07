using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Ecnanib.Components.Agents;

/// <summary>
/// An SVG <c>&lt;text&gt;</c> element. Razor takes <c>&lt;text&gt;</c> in a code block for its own tag, which can't
/// have attributes, so the chart writes its labels through this. Its parent's isolated CSS reaches it with ::deep.
/// </summary>
public sealed class SvgText : ComponentBase
{
    [Parameter, EditorRequired]
    public string X { get; set; } = "";

    [Parameter, EditorRequired]
    public string Y { get; set; } = "";

    /// <summary>A shift from <see cref="Y"/>, such as -0.4em to sit above a line.</summary>
    [Parameter]
    public string? Dy { get; set; }

    /// <summary>start, middle or end.</summary>
    [Parameter]
    public string? Anchor { get; set; }

    [Parameter, EditorRequired]
    public string Text { get; set; } = "";

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "text");
        builder.AddAttribute(1, "x", X);
        builder.AddAttribute(2, "y", Y);
        builder.AddAttribute(3, "dy", Dy);
        builder.AddAttribute(4, "text-anchor", Anchor);
        builder.AddContent(5, Text);
        builder.CloseElement();
    }
}
