using Ecnanib.Tests.Themes;
using Ecnanib.WebUi.Layout;

namespace Ecnanib.Tests.Legal;

/// <summary>
/// Holds the application to <see cref="BrandRules"/>: the names of the AI providers and of the
/// exchange are used only to identify them, with no suggestion of endorsement, and an official
/// logo is used only where the terms of its brand allow it, which is nowhere (NFR-LEG-4). See
/// docs/legal.md.
/// </summary>
/// <remarks>
/// What was compiled of the application is read, and no page is asked for: a word is found
/// whether it is of a page, of a notification, of an email, or of a prompt, and whether or not
/// a test reaches what shows it. That the rule finds what it looks for is shown with types that
/// hold a text or write a picture each, and that it finds nothing else, with types that hold
/// none though they hold what looks like one.
/// </remarks>
public class BrandRuleTests
{
    private const string Planted = "Ecnanib.Tests.Legal.Planted";

    [Fact]
    public void No_text_names_a_company_behind_a_provider_or_has_anyone_stand_behind_the_service()
    {
        var found = BrandRules.Application().Words;

        Assert.True(
            found.Count == 0,
            "These texts name a company behind a provider, speak of a logo, or say what would have the exchange or a provider " +
            $"stand behind the service: {Environment.NewLine}{string.Join(Environment.NewLine, found)}{Environment.NewLine}" +
            "A provider and the exchange are named only to say which one is meant: \"a model of GPT\", \"your key at Binance\". " +
            $"Say it without the word. Where the text names nobody and is shown to nobody, list it in {nameof(BrandRules)}." +
            $"{nameof(BrandRules.TextsThatAreListed)}, with why. See docs/legal.md.");
    }

    [Fact]
    public void No_page_shows_a_picture_but_the_drawings_of_the_application_itself()
    {
        var found = BrandRules.Application().Pictures;

        Assert.True(
            found.Count == 0,
            "These write an element that shows a picture. A logo is a picture, and an official logo is used only where the terms " +
            $"of its brand allow it: {Environment.NewLine}{string.Join(Environment.NewLine, found)}{Environment.NewLine}" +
            "Say it in words. Where it is a drawing of the application's own, made of what the application computes, list its " +
            $"component in {nameof(BrandRules)}.{nameof(BrandRules.ComponentsThatDraw)}, with what it draws. Before a logo is " +
            "used, the terms of its brand are read and written down: see docs/legal.md.");
    }

    [Fact]
    public async Task The_application_serves_no_picture_and_no_font_and_no_stylesheet_loads_one()
    {
        var addresses = BrandRules.ServedAddresses();

        // The stylesheet of the pages and the scripts of the application are among them, so the
        // list is the one of what is served.
        Assert.Contains("app.css", addresses);
        Assert.Contains("motion.js", addresses);

        var others = BrandRules.OfNoListedKind(addresses);

        Assert.True(
            others.Count == 0,
            $"The application serves these, which are neither a stylesheet nor a script: {string.Join(", ", others)}. A picture " +
            "is where a logo would be, and a font is a work of someone with terms of its own. See docs/legal.md.");

        foreach (var (name, styles) in await Stylesheets.AllAsync())
        {
            Assert.False(BrandRules.Loads(styles), $"The stylesheet {name} loads something from an address, which is where a picture would come from.");
        }
    }

    [Fact]
    public void What_is_listed_is_there()
    {
        var read = BrandRules.Application();

        // A text or a component that is listed and is not there is a hole that something else
        // can fall into later.
        var texts = BrandRules.TextsThatAreListed.Keys.Except(read.Listed).ToList();

        Assert.True(
            texts.Count == 0,
            $"These are listed in {nameof(BrandRules)}.{nameof(BrandRules.TextsThatAreListed)}, and the application holds no such text: " +
            string.Join(" | ", texts));

        var drawing = BrandRules.ComponentsThatDraw.Keys.Except(read.Drawing).ToList();

        Assert.True(
            drawing.Count == 0,
            $"These are listed in {nameof(BrandRules)}.{nameof(BrandRules.ComponentsThatDraw)}, and draw nothing: {string.Join(", ", drawing)}");

        Assert.All(BrandRules.TextsThatAreListed, listed =>
            Assert.False(string.IsNullOrWhiteSpace(listed.Value), $"\"{listed.Key}\" does not say why it is listed."));
        Assert.All(BrandRules.ComponentsThatDraw, listed =>
            Assert.False(string.IsNullOrWhiteSpace(listed.Value), $"{listed.Key} does not say what it draws."));
        Assert.All(BrandRules.ServedFiles, listed =>
            Assert.False(string.IsNullOrWhiteSpace(listed.Value), $"{listed.Key} does not say what it is."));

        // The sentence at the foot of every page is the one text of a page that is listed.
        Assert.Contains(Screens.Independence, BrandRules.TextsThatAreListed.Keys);

        // A picture of any other kind is listed for nobody, and neither is a file that holds one.
        Assert.DoesNotContain(".svg", BrandRules.ServedFiles.Keys);
        Assert.DoesNotContain(".png", BrandRules.ServedFiles.Keys);
        Assert.DoesNotContain(".ico", BrandRules.ServedFiles.Keys);
    }

    [Fact]
    public void A_text_that_the_rule_looks_for_is_found_wherever_it_is()
    {
        var read = ReadPlanted();

        // Each where it is: in a text class, in a sentence that is joined with a value, in a
        // method that waits, in a constant, and in the name of a class of the stylesheet.
        Assert.Equal(
            [
                "PlantedCompanies: OpenAI",
                "PlantedConstant: Endorsed by the exchange",
                "PlantedLogo: provider-logo",
                "PlantedNotification: Powered by Claude",
                "PlantedPartner: The official trading partner of Binance.",
            ],
            read.Words.Select(found => $"{Short(found.Type)}: {(found.Literal.Contains("OpenAI", StringComparison.Ordinal) ? found.Word : found.Literal)}")
                .Order(StringComparer.Ordinal));

        // The word that is found is the first that the text says.
        Assert.Equal("official", read.Words.Single(found => Short(found.Type) == "PlantedPartner").Word);
        Assert.Equal("Powered by", read.Words.Single(found => Short(found.Type) == "PlantedNotification").Word);
        Assert.Equal("logo", read.Words.Single(found => Short(found.Type) == "PlantedLogo").Word);
    }

    [Fact]
    public void A_picture_that_a_page_writes_is_found_wherever_it_is()
    {
        var read = ReadPlanted();

        // An element that is opened, markup that the page holds as it is, in any letter case,
        // and what a component that is listed to draw writes beside its drawing.
        Assert.Equal(
            [
                "PlantedChart: canvas",
                "PlantedLogo: img",
                "PlantedMarkup: iframe",
                "PlantedMarkup: img",
                "PlantedMarkup: svg",
            ],
            read.Pictures.Select(found => $"{Short(found.Type)}: {found.Element}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void What_names_nobody_and_shows_no_picture_is_not_found()
    {
        var read = ReadPlanted();

        // The names of the providers and of the exchange where they say which one is meant,
        // words that only hold a word of the rule, names of the code, and elements that show
        // no picture.
        Assert.DoesNotContain(read.Words, found => Short(found.Type) is "PlantedClean" or "PlantedListed");
        Assert.DoesNotContain(read.Pictures, found => Short(found.Type) == "PlantedClean");

        // What is listed is not found, and is known to be there.
        Assert.Equal(["anthropic-version"], read.Listed);
        Assert.Equal([$"{Planted}.PlantedChart"], read.Drawing);
    }

    [Theory]
    [InlineData("A model of OpenAI", "OpenAI")]
    [InlineData("made by xAI.", "xAI")]
    [InlineData("anthropic-version", "anthropic")]
    [InlineData("googleSearch", "google")]
    [InlineData("The Google of trading", "Google")]
    [InlineData("our partners", "partner")]
    [InlineData("In partnership with Binance", "partner")]
    [InlineData("Endorsed by Binance", "Endors")]
    [InlineData("an endorsement", "endors")]
    [InlineData("Sponsored by the exchange", "Sponsor")]
    [InlineData("an affiliate of Binance", "affiliat")]
    [InlineData("the official app", "official")]
    [InlineData("Officially supported", "Official")]
    [InlineData("a certified integration", "certified")]
    [InlineData("POWERED BY GPT", "POWERED BY")]
    [InlineData("approved by Binance", "approved by")]
    [InlineData("trusted by traders", "trusted by")]
    [InlineData("in association with Binance", "in association with")]
    [InlineData("the logo of the provider", "logo")]
    [InlineData("provider-logo", "logo")]
    [InlineData("logos.svg", "logos")]
    public void A_text_says_what_the_rule_looks_for(string text, string word) =>
        Assert.Equal(word, BrandRules.Says().Match(text).Value);

    [Theory]
    [InlineData("A model of GPT")]
    [InlineData("Claude, Gemini, GPT, and Grok")]
    [InlineData("your real Binance account")]
    [InlineData("an unofficial figure")]
    [InlineData("the certificate of the server")]
    [InlineData("untrusted content")]
    [InlineData("the model catalog")]
    [InlineData("LogOutputsSettings")]
    [InlineData("logout")]
    [InlineData("https://api.x.ai")]
    [InlineData("a maximal drawdown")]
    [InlineData("approved")]
    [InlineData("trusted")]
    [InlineData("")]
    public void A_text_that_names_nobody_says_nothing_of_the_kind(string text) =>
        Assert.DoesNotMatch(BrandRules.Says(), text);

    [Fact]
    public void Every_word_of_the_lists_is_one_that_the_rule_looks_for()
    {
        // The lists say what the rule looks for, and the rule is what finds it: neither has a
        // word that the other lacks.
        Assert.All(
            BrandRules.Companies.Concat(BrandRules.Marks).Concat(BrandRules.StandingBehind),
            word => Assert.Equal(word, BrandRules.Says().Match($"A text of {word}.").Value));
    }

    [Fact]
    public void A_file_that_is_neither_a_stylesheet_nor_a_script_is_found()
    {
        Assert.Equal(
            ["favicon.ico", "fonts/inter.woff2", "img/claude.svg", "img/mark.png.gz"],
            BrandRules.OfNoListedKind(
            [
                "app.css", "app.css.gz", "app.fdtujw287n.css", "motion.js", "_framework/blazor.web.js.gz",
                "img/claude.svg", "img/mark.png.gz", "favicon.ico", "fonts/inter.woff2", "img/claude.svg",
            ]));

        Assert.Empty(BrandRules.OfNoListedKind(["app.CSS", "WebUi/Layout/ReconnectModal.razor.js"]));
    }

    [Theory]
    [InlineData(".mark { background-image: url(/claude.svg); }", true)]
    [InlineData(".mark { background: URL( 'data:image/png;base64,AAAA' ) no-repeat; }", true)]
    [InlineData("@import \"fonts.css\";", true)]
    [InlineData("@font-face { font-family: Mark; src: url(mark.woff2); }", true)]
    [InlineData("/* no logo: nothing is loaded with url( here */ .mark { color: red; }", false)]
    [InlineData(".curl { color: CanvasText; }", false)]
    [InlineData("", false)]
    public void A_stylesheet_that_loads_something_is_found(string styles, bool loads) =>
        Assert.Equal(loads, BrandRules.Loads(styles));

    private static ReadBrands ReadPlanted() => BrandRules.Read(
        typeof(BrandRuleTests).Assembly,
        Planted,
        new HashSet<string> { "anthropic-version", "listed-and-not-there" },
        new HashSet<string> { $"{Planted}.PlantedChart", $"{Planted}.PlantedClean" });

    private static string Short(string type) => type[(Planted.Length + 1)..];
}
