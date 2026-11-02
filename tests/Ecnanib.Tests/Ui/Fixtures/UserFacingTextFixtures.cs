// Fixtures for UserFacingTextGuardTests. None of it ever runs. Each class makes the builder calls Razor
// would generate, because this project doesn't compile Razor. The first namespace has to be found, and
// the second has to pass.

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;

#pragma warning disable ASP0006 // Razor's sequence numbers are constants, and these copy its output.

namespace Ecnanib.Tests.Ui.Fixtures.Found
{
    public sealed class TextInMarkup : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddMarkupContent(0, "<h1 class=\"title\">Hard-coded heading</h1>");
    }

    public sealed class TextAfterAnEntity : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddMarkupContent(0, "<p>Prices &amp; charts</p>");
    }

    public sealed class TextInContent : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddContent(0, "Hard-coded content");
    }

    public sealed class TextInChildContent : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddComponentParameter(0, "ChildContent", (RenderFragment)(child => child.AddContent(1, "Hard-coded title")));
    }

    public sealed class TextInBothBranches : ComponentBase
    {
        [Parameter]
        public bool Enabled { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddContent(0, Enabled ? "Yes" : "No");
    }

    public sealed class TextInInterpolation : ComponentBase
    {
        [Parameter]
        public string Name { get; set; } = string.Empty;

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddContent(0, $"Hello {Name}");
    }

    public sealed class LabelInMarkup : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddMarkupContent(0, "<button aria-label=\"Close menu\"></button>");
    }

    public sealed class LabelAttribute : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "aria-label", "Close menu");
            builder.CloseElement();
        }
    }

    public sealed class LabelInATernary : ComponentBase
    {
        [Parameter]
        public bool Open { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "aria-label", Open ? "Close menu" : "Open menu");
            builder.CloseElement();
        }
    }

    public sealed class LabelParameter : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<Router>(0);
            builder.AddComponentParameter(1, "title", "Close menu");
            builder.CloseComponent();
        }
    }
}

namespace Ecnanib.Tests.Ui.Fixtures.Passing
{
    internal static class Resource
    {
        public static string Text => string.Empty;
    }

    public sealed class MarkupWithoutText : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddMarkupContent(0, "<div class=\"components-rejoining-animation\" aria-hidden=\"true\"><div></div></div>");
    }

    public sealed class DoctypeAndEntities : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddMarkupContent(0, "<!DOCTYPE html><p>&nbsp;&middot;&nbsp;</p>");
    }

    public sealed class TextFromAResource : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddContent(0, Resource.Text);
    }

    public sealed class LabelFromAResource : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "aria-label", Resource.Text);
            builder.CloseElement();
        }
    }

    public sealed class OrdinaryAttribute : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "a");
            builder.AddAttribute(1, "href", "/agents");
            builder.AddAttribute(2, "class", "wordmark");
            builder.CloseElement();
        }
    }

    public sealed class OrdinaryParameter : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<Router>(0);
            builder.AddComponentParameter(1, "Selector", "h1");
            builder.CloseComponent();
        }
    }
}
