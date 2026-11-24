using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Settings;
using Ecnanib.WebUi;
using Microsoft.AspNetCore.Authorization;

namespace Ecnanib.Tests.Layout;

/// <summary>
/// There is a route and navigation for each screen: landing, registration and sign-in, dashboard,
/// agent list, agent editor, agent detail, experience, model catalog, settings (profile,
/// security, API keys, appearance, notifications), and operator status (FR-UI-2). These tests
/// hold the application to <see cref="ScreenRules"/>. See docs/layout.md.
/// </summary>
public class ScreenRuleTests(EcnanibApplicationFactory application) : IClassFixture<EcnanibApplicationFactory>
{
    private const string Ada = "ada@example.com";
    private const string Olive = "olive@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_screens_are_the_ones_the_requirement_names()
    {
        // Pinned, as the requirement lists them, so that a screen that is added or dropped shows
        // in a review as what it is.
        Assert.Equal(
            [
                "landing",
                "registration and sign-in",
                "dashboard",
                "agent list",
                "agent editor",
                "agent detail",
                "experience",
                "model catalog",
                "settings: profile",
                "settings: security",
                "settings: API keys",
                "settings: appearance",
                "settings: notifications",
                "operator status",
            ],
            ScreenRules.Specified);

        // Each of them is on the map, and the map holds nothing else.
        Assert.Equal(
            ScreenRules.Specified.Order(StringComparer.Ordinal),
            ScreenRules.Screens.Select(screen => screen.Screen).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_screen_has_a_page_at_its_route_which_says_whom_it_opens_for()
    {
        var endpoints = AuditRules.Endpoints(application.Services).ToList();

        Assert.All(ScreenRules.Screens, screen =>
        {
            var page = Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText == screen.Route);
            var required = page.Metadata.GetOrderedMetadata<IAuthorizeData>();

            switch (screen.For)
            {
                case ScreenFor.Visitor:
                    Assert.True(required.Count == 0, $"{screen.Route} is for visitors, and asks who is signed in.");
                    break;

                case ScreenFor.User:
                    Assert.True(required.Count > 0, $"{screen.Route} is for users, and opens for a visitor.");
                    Assert.All(required, data => Assert.Null(data.Policy));
                    break;

                default:
                    Assert.Contains(required, data => data.Policy == OperatorAccess.Policy);
                    break;
            }
        });
    }

    [Fact]
    public async Task Every_screen_is_led_to_from_where_the_map_says_and_opens_there()
    {
        using var hosted = Application();
        using var visitor = hosted.CreateClient();
        using var user = hosted.CreateClient();
        using var anOperator = hosted.CreateClient();

        var ada = await hosted.RegisterVerifiedAsync(Ada);
        await hosted.RegisterVerifiedAsync(Olive);
        await user.SignInAsync(Ada);
        await anOperator.SignInAsync(Olive);

        var agent = await hosted.GiveAgentAsync(ada.Id, Morning, name: ScreenRules.AgentName);

        foreach (var screen in ScreenRules.Screens)
        {
            var client = screen.For switch
            {
                ScreenFor.Visitor => visitor,
                ScreenFor.User => user,
                _ => anOperator,
            };

            var path = screen.Route.Replace(ScreenRules.AgentInRoute, agent.ToString());

            // A link on the page that the map names leads to it.
            Assert.True(
                Regex.IsMatch(await client.PageAsync(screen.From), LinkTo(path, screen.Link)),
                $"The page {screen.From} has no link \"{screen.Link}\" to {path}, which is how the {screen.Screen} screen is reached.");

            // And the screen is there, at that address, with a heading.
            using var answer = await client.GetAsync(path, TestContext.Current.CancellationToken);
            var page = await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.True(answer.IsSuccessStatusCode, $"GET {path} answered {(int)answer.StatusCode}.");
            Assert.Equal(path, answer.RequestMessage!.RequestUri!.PathAndQuery);
            Assert.Contains("<h1", page);
        }
    }

    [Fact]
    public async Task Nobody_is_led_to_a_screen_that_does_not_open_for_them()
    {
        using var hosted = Application();
        using var visitor = hosted.CreateClient();
        using var user = hosted.CreateClient();

        await hosted.RegisterVerifiedAsync(Ada);
        await user.SignInAsync(Ada);

        var asVisitor = await visitor.PageAsync(WebUiModule.HomePath);
        var asUser = await user.PageAsync(WebUiModule.HomePath);

        // What the bar leads a user to, it does not show a visitor, and what it leads an operator
        // to, it shows neither.
        foreach (var screen in ScreenRules.Screens.Where(screen => screen.From == WebUiModule.HomePath))
        {
            var link = LinkTo(screen.Route, screen.Link);

            if (screen.For != ScreenFor.Visitor)
            {
                Assert.DoesNotMatch(link, asVisitor);
            }

            if (screen.For == ScreenFor.Operator)
            {
                Assert.DoesNotMatch(link, asUser);
            }
        }

        // The list is not empty of either kind, so the loop looked at something.
        Assert.Contains(ScreenRules.Screens, screen => screen.From == WebUiModule.HomePath && screen.For == ScreenFor.User);
        Assert.Contains(ScreenRules.Screens, screen => screen.From == WebUiModule.HomePath && screen.For == ScreenFor.Operator);
    }

    /// <summary>
    /// The application with one operator in its configuration file, and with a stand-in for the
    /// market data of the exchange, which the editor of an agent asks for the symbols of a market.
    /// </summary>
    private static AccountsApplication Application() =>
        AgentsApplication.With(
            new StandInMarketData(),
            new TestClock(Morning),
            settings: file => file.WithJson("Application:Operators", $"[ \"{Olive}\" ]"));

    /// <summary>
    /// A link to a path that says a text. The layout marks its elements for its own styles, and
    /// a link says whether its page is open, so a link carries more than its address.
    /// </summary>
    private static string LinkTo(string path, string text) =>
        $"<a [^>]*href=\"{Regex.Escape(path)}\"[^>]*>{Regex.Escape(WebUtility.HtmlEncode(text))}</a>";
}
