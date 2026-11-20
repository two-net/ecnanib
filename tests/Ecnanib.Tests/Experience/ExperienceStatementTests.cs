using System.Reflection;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.WebUi.Pages.Experience;
using Microsoft.AspNetCore.Components;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// Experience screens state that past results do not predict future results, that small samples
/// are unreliable, and that Demo results differ from Live (FR-EXP-11). See "What every screen
/// states" in docs/experience.md.
/// </summary>
public class ExperienceStatementTests
{
    private const string Ada = "ada@example.com";

    // Between two hours, so that the job that checks the stored keys is not due at the start.
    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 9, 20, 0, TimeSpan.Zero);

    [Fact]
    public async Task Every_experience_screen_states_what_its_figures_cannot_tell_whatever_it_shows()
    {
        // The file of the tests takes 15 trades as the minimum sample, and the statement says it.
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var model = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);

        // Every experience screen, by its route: a screen that is added later is among them, and
        // fails here until it makes the statements.
        var routes = typeof(ExperiencePages).Assembly.GetTypes()
            .Where(page => page.Namespace == typeof(ExperiencePages).Namespace)
            .SelectMany(page => page.GetCustomAttributes<RouteAttribute>())
            .Select(route => route.Template)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["/experience", "/experience/models/{ModelId:guid}"], routes);

        async Task EveryScreenStatesAsync(string what)
        {
            foreach (var route in routes)
            {
                var path = route.Replace("{ModelId:guid}", model.ToString(), StringComparison.Ordinal);

                Assert.False(
                    path.Contains('{', StringComparison.Ordinal),
                    $"The route {route} has a parameter that this test does not fill. Say here what a path of it is.");

                // Told no mode, and told each: a screen states the same in Demo and in Live.
                foreach (var query in new[] { string.Empty, "?mode=demo", "?mode=live", "?mode=live&market=futures&period=7" })
                {
                    var page = AgentBrowser.Read(await client.PageAsync(path + query));

                    foreach (var statement in new[]
                    {
                        "Past results do not predict future results.",
                        "Small samples are unreliable. A record of fewer than 15 closed trades is marked as a small sample",
                        "Demo results differ from Live results.",
                    })
                    {
                        Assert.True(
                            page.Contains($"<li>{statement}", StringComparison.Ordinal),
                            $"The page {path}{query} does not state \"{statement}\" {what}:{Environment.NewLine}{page}");
                    }
                }
            }
        }

        // With nothing to show: a screen that made its statements beside its figures alone would
        // make none here.
        await EveryScreenStatesAsync("when the user has no trade");

        // With trades in Demo alone, and then with trades in both.
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-3), resultInR: 1m);
        await EveryScreenStatesAsync("when the user has trades in Demo");

        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-2), resultInR: -1m, mode: TradingMode.Live);
        await EveryScreenStatesAsync("when the user has trades in Demo and in Live");
    }
}
