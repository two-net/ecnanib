using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Ecnanib.Tests.Legal.Planted;

// What BrandRuleTests plants to show that the rule finds what it looks for: types that hold a
// text which names a company or has someone stand behind the service, each in another place,
// components that write a picture, and types that hold nothing of the kind though they hold
// what looks like it. The components are written as the compiler writes a page. Nothing else
// uses them.

/// <summary>Found: the exchange as one that stands behind the service, in a text of a text class.</summary>
internal static class PlantedPartner
{
    public static string Claim => "The official trading partner of Binance.";
}

/// <summary>Found: the companies behind two providers, in a sentence that is joined with a value.</summary>
internal static class PlantedCompanies
{
    public static string Of(string model) => $"{model} is a model of OpenAI, and Grok is made by xAI.";
}

/// <summary>Found: a provider as what the service runs on, in a text that a job writes while it waits.</summary>
internal sealed class PlantedNotification
{
    public string? Said { get; private set; }

    public async Task TellAsync()
    {
        await Task.Yield();

        Said = "Powered by Claude";
    }
}

/// <summary>Found: endorsement, in a constant, which stands in its type whether or not anything uses it.</summary>
internal static class PlantedConstant
{
    public const string Banner = "Endorsed by the exchange";
}

/// <summary>Found: a mark, in the name of a class of the stylesheet that a component writes, and a logo as its picture.</summary>
public sealed class PlantedLogo : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "img");
        builder.AddAttribute(1, "class", "provider-logo");
        builder.AddAttribute(2, "src", "/claude.svg");
        builder.CloseElement();
    }
}

/// <summary>Found: pictures in markup that a page holds as it is, of three kinds.</summary>
public sealed class PlantedMarkup : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder) =>
        builder.AddMarkupContent(
            0, "<p class=\"marks\"><svg class=\"mark\" viewBox=\"0 0 8 8\"></svg>\n<IMG src=\"/mark.png\" alt=\"\"><iframe src=\"/frame\"></iframe></p>");
}

/// <summary>Not found: a component that is listed to draw, and writes a drawing of its own. Found: the picture that it writes beside it.</summary>
public sealed class PlantedChart : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "svg");
        builder.AddAttribute(1, "class", "chart");
        builder.CloseElement();
        builder.OpenElement(2, "canvas");
        builder.CloseElement();
    }
}

/// <summary>Not found: a text that is listed, as a header that an API asks for is.</summary>
internal static class PlantedListed
{
    public static string Header => "anthropic-version";
}

/// <summary>
/// Not found, though everything in it looks like what the rule looks for to something: the
/// names of the providers and of the exchange where they say which one is meant, words that
/// only hold a word of the rule, names of the code, and elements that show no picture.
/// </summary>
public sealed class PlantedClean : ComponentBase
{
    public static string Team => "A team of four AI roles runs each agent, with models of Claude, Gemini, GPT, and Grok.";

    public static string Exchange => "Binance reports that this key can withdraw. Demo uses Binance's test environment.";

    public static string Words => "An unofficial figure. The certificate of the server. Untrusted content. The catalog of the models.";

    public static string Address => "https://api.x.ai";

    public static string Names => "LogOutputsSettings logout sign-out-form technologies";

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "figure");
        builder.AddAttribute(1, "class", "team-illustration");
        builder.AddMarkupContent(2, "<figcaption class=\"imgs\">A model of GPT</figcaption><span class=\"svg-like\"></span>\n");
        builder.CloseElement();
    }
}
