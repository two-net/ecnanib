using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Themes;
using Ecnanib.WebUi;
using Ecnanib.WebUi.Pages;
using Ecnanib.WebUi.Pages.Agents;

namespace Ecnanib.Tests.Animation;

/// <summary>
/// The public landing page presents the four-provider team with an animated illustration
/// (FR-ANI-7): the start page shows the team of a new agent as the screen of an agent draws a
/// team, each role with the provider that the configuration file gives it (FR-TEAM-2,
/// FR-TEAM-3), by the name that the catalog has for the provider. A provider is named as text,
/// only to say which it is, and the page carries no logo (NFR-LEG-4). See docs/motion.md.
/// </summary>
/// <remarks>
/// None of these tests drives a browser. What is held here is what a browser is sent: the picture as it
/// stands still, complete, which is what a device that asks for reduced motion and an account
/// that reduced it are shown (FR-ANI-4). That it is played once, in steps, for any other, and
/// stands still while nobody sees it, is the stylesheet's: see <see cref="MotionStyleTests"/>.
/// </remarks>
public partial class TeamIllustrationPageTests
{
    private const string Ada = "ada@example.com";

    // What the illustration says under it. It says the order that the roles work in, in words.
    private const string Caption =
        "The team of a new agent. The Market Analyst and the Sentiment Analyst work at the same time, " +
        "then the Strategist, then the Risk Manager. You choose the model of each role, and can give a role to another provider.";

    [Fact]
    public async Task The_start_page_presents_the_team_of_a_new_agent_as_an_illustration()
    {
        using var application = new AccountsApplication();
        using var visitor = application.CreateClient();

        var page = Read(await visitor.PageAsync("/"));
        var figure = Assert.Single(IllustrationsOn(page));

        // The four roles in the order that they work in, in three stages: the two analysts in
        // one, then the Strategist, then the Risk Manager. Each says its name, the provider
        // that the configuration file of the tests gives it, by the name that the catalog has
        // for it, and its job as the form of an agent says it. Every role is done, and the work
        // was handed to each stage: it is the picture of a cycle that has gone through.
        Assert.Equal(Illustration("Claude", "Grok", "GPT", "Gemini"), figure);
        Assert.Equal(HomePages.TeamCaption, Caption);

        // It is under what the page says of the team, and above what is at stake.
        Assert.True(page.IndexOf(HomePages.Who, StringComparison.Ordinal) < page.IndexOf(figure[..40], StringComparison.Ordinal));
        Assert.True(page.IndexOf(figure[..40], StringComparison.Ordinal) < page.IndexOf(HomePages.Risk, StringComparison.Ordinal));

        // And it is of the start page alone.
        Assert.Empty(IllustrationsOn(await visitor.PageAsync(SessionAuthentication.SignInPath)));
        Assert.Empty(IllustrationsOn(await visitor.PageAsync(WebUiModule.RegisterPath)));
    }

    [Fact]
    public async Task The_illustration_follows_the_configuration_file_and_names_a_provider_as_the_catalog_does()
    {
        // A file that gives two roles to one provider, which nothing keeps it from.
        using var application = new AccountsApplication(settings: file => file
            .With("Trading:DefaultRoles:MarketAnalyst", "Gemini")
            .With("Trading:DefaultRoles:Strategist", "Claude"));
        using var visitor = application.CreateClient();

        Assert.Equal(
            Illustration("Gemini", "Grok", "Claude", "Gemini"),
            Assert.Single(IllustrationsOn(Read(await visitor.PageAsync("/")))));

        // The name is the one of the row of the provider, and no text of the page's own.
        application.Database.Execute("UPDATE ai_providers SET display_name = 'Gemini of the test' WHERE code = 'gemini'");

        Assert.Equal(
            Illustration("Gemini of the test", "Grok", "Claude", "Gemini of the test"),
            Assert.Single(IllustrationsOn(Read(await visitor.PageAsync("/")))));
    }

    [Fact]
    public async Task A_provider_is_named_as_text_to_say_which_it_is_and_the_page_carries_no_logo()
    {
        using var application = new AccountsApplication();
        using var visitor = application.CreateClient();

        var page = Read(await visitor.PageAsync("/"));
        var figure = Assert.Single(IllustrationsOn(page));

        // No picture is on the page, and no stylesheet loads one: an official logo is used
        // only where the terms of its brand allow it, and none is used at all (NFR-LEG-4).
        Assert.All(
            new[] { "<img", "<svg", "<picture", "<object", "<embed", "<canvas", "<video", "<iframe" },
            element => Assert.DoesNotContain(element, page, StringComparison.OrdinalIgnoreCase));

        foreach (var (name, styles) in await Stylesheets.AllAsync())
        {
            Assert.False(Stylesheets.WithoutComments(styles).Contains("url(", StringComparison.OrdinalIgnoreCase), name);
        }

        // In the illustration a provider is in one sentence, which says whose model fills a
        // role, and each of the four is there once: nothing else is said of one.
        Assert.Equal(
            ["A model of Claude", "A model of Grok", "A model of GPT", "A model of Gemini"],
            Said().Matches(figure).Select(said => said.Groups[1].Value));
        Assert.All(
            new[] { "Claude", "Gemini", "GPT", "Grok" },
            provider => Assert.Single(Regex.Matches(figure, $@"\b{provider}\b")));

        // The page names the providers and no company behind one, and says nothing that would
        // have a provider or the exchange stand behind the service.
        Assert.All(
            new[] { "Anthropic", "Google", "OpenAI", "xAI", "logo", "partner", "endorse", "official", "sponsor", "powered by" },
            word => Assert.DoesNotMatch($@"(?i)\b{Regex.Escape(word)}", ContentOf(page)));
    }

    [Fact]
    public async Task The_illustration_says_that_it_moves_and_carries_no_style_no_script_and_nothing_to_click()
    {
        using var application = new AccountsApplication();
        using var visitor = application.CreateClient();

        var figure = Assert.Single(IllustrationsOn(Read(await visitor.PageAsync("/"))));

        // The figure says that something of it moves, which is what has the script of the pages
        // say when it is off the screen. What moves, and that it stands still then, is in the
        // stylesheet: the page carries no style and no script for it.
        Assert.StartsWith("<figure class=\"team-illustration\" data-moves>", figure);
        Assert.DoesNotContain("style=", figure, StringComparison.Ordinal);
        Assert.DoesNotContain("<style", figure, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", figure, StringComparison.Ordinal);

        // What is drawn for the eye alone is hidden from a screen reader: the marker of each
        // role, and the second outline of each card. Everything else is text.
        Assert.Equal(8, Regex.Matches(figure, "aria-hidden=\"true\"").Count);
        Assert.Equal(8, Regex.Matches(figure, "<span class=\"role-(?:marker|turn)\" aria-hidden=\"true\"></span>").Count);

        // Nothing of it takes a click or a key, so nothing that moves holds one up (FR-ANI-5).
        Assert.All(
            new[] { "<a ", "<button", "<input", "<details", "tabindex", "<form" },
            element => Assert.DoesNotContain(element, figure, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_user_is_shown_it_too_and_one_who_reduced_what_moves_is_sent_the_same_picture()
    {
        using var application = new AccountsApplication();
        using var visitor = application.CreateClient();
        using var user = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await user.SignInAsync(Ada);

        var asVisitor = Assert.Single(IllustrationsOn(Read(await visitor.PageAsync("/"))));

        // The start page of a user has it as the one of a visitor does.
        var page = Read(await user.PageAsync("/"));

        Assert.Equal("<html lang=\"en\">", Root().Match(page).Value);
        Assert.Equal(asVisitor, Assert.Single(IllustrationsOn(page)));

        // A user who reduced what moves is sent the same picture, whole, on a page that says
        // "reduced": the stylesheet gives such a page nothing that moves, so the picture stands
        // still as it is written (FR-ANI-4).
        await user.SubmitAsync(WebUiModule.AppearancePath, "motion", ("Motion", "reduced"));
        page = Read(await user.PageAsync("/"));

        Assert.Equal("<html lang=\"en\" data-motion=\"reduced\">", Root().Match(page).Value);
        Assert.Equal(asVisitor, Assert.Single(IllustrationsOn(page)));
    }

    /// <summary>
    /// The illustration as a page writes it, with nothing between one element and the next: the
    /// roles with the names of their providers, in the order of the roles.
    /// </summary>
    private static string Illustration(string marketAnalyst, string sentimentAnalyst, string strategist, string riskManager) =>
        "<figure class=\"team-illustration\" data-moves><ol class=\"team-pipeline\">" +
        "<li class=\"pipeline-stage\"><ul class=\"pipeline-parallel\">" +
        $"<li>{Card(Role.MarketAnalyst, marketAnalyst)}</li><li>{Card(Role.SentimentAnalyst, sentimentAnalyst)}</li></ul></li>" +
        $"<li class=\"pipeline-stage stage-handed\">{Card(Role.Strategist, strategist)}</li>" +
        $"<li class=\"pipeline-stage stage-handed\">{Card(Role.RiskManager, riskManager)}</li></ol>" +
        $"<figcaption>{Caption}</figcaption></figure>";

    /// <summary>
    /// The card of a role: the element of its second outline, its name, whose model fills it
    /// beside its marker, and its job. The name and the job are the ones of the form of an agent.
    /// </summary>
    private static string Card(Role role, string provider) =>
        "<div class=\"pipeline-role role-done\"><span class=\"role-turn\" aria-hidden=\"true\"></span>" +
        $"<span class=\"role-name\">{AgentPages.Describe(role).Name}</span>" +
        $"<span class=\"role-state\"><span class=\"role-marker\" aria-hidden=\"true\"></span>A model of {provider}</span>" +
        $"<span class=\"role-job\">{AgentPages.Describe(role).Job}</span></div>";

    /// <summary>A page as a person reads it: what was written for markup is text again.</summary>
    private static string Read(string page) => WebUtility.HtmlDecode(page);

    /// <summary>The illustrations of a page, each as it is written, with nothing between one element and the next.</summary>
    private static IReadOnlyList<string> IllustrationsOn(string page) =>
        [.. Figure().Matches(page).Select(figure => Between().Replace(figure.Value, "><"))];

    /// <summary>The content of a page: what is between the bar and the foot.</summary>
    private static string ContentOf(string page) => Content().Match(page).Value;

    [GeneratedRegex("<figure class=\"team-illustration\".*?</figure>", RegexOptions.Singleline)]
    private static partial Regex Figure();

    [GeneratedRegex("<main.*?</main>", RegexOptions.Singleline)]
    private static partial Regex Content();

    // What a card says beside its marker.
    [GeneratedRegex("<span class=\"role-marker\" aria-hidden=\"true\"></span>(.*?)</span>")]
    private static partial Regex Said();

    [GeneratedRegex("<html[^>]*>")]
    private static partial Regex Root();

    [GeneratedRegex(@">\s+<")]
    private static partial Regex Between();
}
