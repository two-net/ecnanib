namespace Ecnanib.Tests.Texts;

/// <summary>
/// Holds the components of the Web UI to <see cref="TextRules"/>: text is kept separate from
/// code, so that other languages can be added (NFR-USA-4). A page and a part of one hold no
/// words of their own, and ask a text class for each. See docs/texts.md.
/// </summary>
/// <remarks>
/// What was compiled of the components is read, and no page is asked for: a word that a
/// component holds is found whether or not a test reaches the state that shows it. That the
/// rule finds what it looks for is shown with components that hold a text each, in another
/// place each, and that it finds nothing else, with one that holds none.
/// </remarks>
public class TextRuleTests
{
    private const string Planted = "Ecnanib.Tests.Texts.Planted";

    [Fact]
    public void No_component_holds_a_text_of_its_own()
    {
        var found = TextRules.Application().Texts;

        Assert.True(
            found.Count == 0,
            "These components hold words of their own, which no other language can take the place of: " +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, found)}{Environment.NewLine}" +
            "Move each to the text class of its screen, as a property or a method, and have the component ask for it there. " +
            "A constant of a text class is found as well: the compiler copies it into the page. Where the value is no text " +
            $"but a name that code uses, list its attribute in {nameof(TextRules)}.{nameof(TextRules.AttributesThatAreNoText)}, " +
            $"or its parameter in {nameof(TextRules.ParametersThatAreNoText)}. See docs/texts.md.");
    }

    [Fact]
    public void What_is_listed_as_no_text_is_there()
    {
        var read = TextRules.Application();

        // An attribute or a parameter that is listed and that no component gives a value is a
        // hole that something else can fall into later.
        var attributes = TextRules.AttributesThatAreNoText.Except(read.Attributes).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            attributes.Count == 0,
            $"These are listed in {nameof(TextRules)}.{nameof(TextRules.AttributesThatAreNoText)}, and no component writes them: " +
            $"{string.Join(", ", attributes)}");

        var parameters = TextRules.ParametersThatAreNoText.Keys.Except(read.Parameters).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            parameters.Count == 0,
            $"These are listed in {nameof(TextRules)}.{nameof(TextRules.ParametersThatAreNoText)}, and no component hands them over: " +
            $"{string.Join(", ", parameters)}");

        Assert.All(TextRules.ParametersThatAreNoText, listed =>
            Assert.False(string.IsNullOrWhiteSpace(listed.Value), $"{listed.Key} does not say what it holds in place of a text."));

        // A person is read what these say, so none of them is ever listed.
        Assert.DoesNotContain("aria-label", TextRules.AttributesThatAreNoText);
        Assert.DoesNotContain("alt", TextRules.AttributesThatAreNoText);
        Assert.DoesNotContain("placeholder", TextRules.AttributesThatAreNoText);
        Assert.DoesNotContain("lang", TextRules.AttributesThatAreNoText);
    }

    [Fact]
    public void A_text_that_a_component_holds_is_found_wherever_it_is()
    {
        var read = ReadPlanted();

        // Each where it is: between the tags of markup, in an attribute of markup, as content
        // on either side of a value, in an attribute that is joined with a value, in what is
        // put into a part, in a parameter of a part, as a member of an enum that is written by
        // its name, in the code of a page, and as a constant of a text class.
        Assert.Equal(
            [
                "PlantedAttribute: Copy",
                "PlantedCell: Why it failed",
                "PlantedCode: High",
                "PlantedCode: Nothing was saved.",
                "PlantedCode: Open",
                "PlantedCode: SMA20",
                "PlantedConstant: Copied into the page",
                "PlantedContent: Too many attempts. Try again in up to",
                "PlantedContent: minutes.",
                "PlantedEnum: a member of TradingMode, by its name",
                "PlantedFragment: Inside a part",
                "PlantedMarkup: Sign in",
                "PlantedParameter: Shown to a person",
            ],
            read.Texts.Select(text => $"{text.Component}: {text.Text}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void What_is_no_text_is_not_found()
    {
        var read = ReadPlanted();

        // A property of a text class, a constant that names a form, the names of classes, an id
        // that is put together, signs and digits, what an exception and a log are told, a
        // record that the compiler writes out, and the names of the code.
        Assert.DoesNotContain(read.Texts, text => text.Component is "PlantedClean" or "PlantedShell");

        // What is listed is not found, and is known to be there.
        Assert.Contains("data-mode", read.Attributes);
        Assert.Contains("PlantedShell.Selector", read.Parameters);
        Assert.DoesNotContain(read.Texts, text => text.Text == "main h1");
    }

    [Theory]
    [InlineData("Nothing was saved.", true)]
    [InlineData("Open", true)]
    [InlineData("SMA20", true)]
    [InlineData("in less than a minute", true)]
    [InlineData("sign-in", false)]
    [InlineData("email", false)]
    [InlineData("agent-risk-per-trade", false)]
    [InlineData("ReassignsRoles", false)]
    [InlineData("D", false)]
    [InlineData(", ", false)]
    [InlineData("100 %", false)]
    [InlineData("", false)]
    public void A_literal_in_code_is_text_when_it_reads_as_words(string literal, bool words) =>
        Assert.Equal(words, TextRules.ReadsAsWords(literal));

    [Theory]
    [InlineData("Sign in", true)]
    [InlineData("a", true)]
    [InlineData("é", true)]
    [InlineData(", ", false)]
    [InlineData("100 % × 2", false)]
    [InlineData("\n    ", false)]
    public void What_is_handed_over_as_text_is_one_when_it_has_a_letter(string said, bool text) =>
        Assert.Equal(text, TextRules.HasLetter(said));

    private static ReadComponents ReadPlanted() => TextRules.Read(
        typeof(TextRuleTests).Assembly,
        Planted,
        new HashSet<string> { "class", "id", "data-mode" },
        new HashSet<string> { "PlantedShell.Selector" });
}
