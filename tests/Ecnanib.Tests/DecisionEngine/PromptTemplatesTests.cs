using Ecnanib.DecisionEngine;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// The templates that an agent uses follow from its market and its timeframe, with the four
/// roles and the one profile, each in the version that is in use (FR-PRM-7). They are delivered
/// as seed data: what is read here is what the migrations stored, and nothing else (NFR-DAT-10).
/// </summary>
public class PromptTemplatesTests
{
    [Fact]
    public async Task The_templates_of_an_agent_are_those_of_its_market_its_timeframe_the_four_roles_and_the_profile()
    {
        using var application = new AccountsApplication();
        var templates = application.PromptTemplates;
        var asked = new List<TemplatesInUse>();

        // Every market with every timeframe: the eight combinations of the specification.
        foreach (var market in Enum.GetValues<Market>())
        {
            foreach (var timeframe in Timeframes.All)
            {
                var inUse = await templates.InUseAsync(market, timeframe, TestContext.Current.CancellationToken);
                asked.Add(inUse);

                Assert.Equal((PromptTemplateKind.Market, market.ToString()), (inUse.Market.Kind, inUse.Market.Subject));
                Assert.Equal((PromptTemplateKind.Timeframe, timeframe.Code()), (inUse.Timeframe.Kind, inUse.Timeframe.Subject));
                Assert.Equal((PromptTemplateKind.Profile, "Aggressive"), (inUse.Profile.Kind, inUse.Profile.Subject));

                // Each of the four roles, under its own name.
                Assert.Equal(Enum.GetValues<Role>(), inUse.Roles.Keys.Order());
                Assert.All(inUse.Roles, role =>
                    Assert.Equal((PromptTemplateKind.Role, role.Key.ToString()), (role.Value.Kind, role.Value.Subject)));

                // Seven templates, each in use, and each stored with a text.
                var all = inUse.Roles.Values.Concat([inUse.Market, inUse.Timeframe, inUse.Profile]).ToList();
                Assert.Equal(7, all.Select(template => template.Id).Distinct().Count());
                Assert.All(all, template =>
                {
                    Assert.True(template.IsActive);
                    Assert.False(string.IsNullOrWhiteSpace(template.Text));
                });
            }
        }

        // No two combinations have the same two modules, and all of them have the same roles
        // and the same profile.
        Assert.Equal(8, asked.Select(inUse => (inUse.Market.Id, inUse.Timeframe.Id)).Distinct().Count());
        Assert.Single(asked.Select(inUse => inUse.Profile.Id).Distinct());
        Assert.Single(asked.Select(inUse => string.Join(",", inUse.Roles.OrderBy(role => role.Key).Select(role => role.Value.Id))).Distinct());
    }

    [Fact]
    public async Task A_call_of_a_role_uses_its_instructions_and_the_three_modules_of_the_agent()
    {
        using var application = new AccountsApplication();

        var inUse = await application.PromptTemplates.InUseAsync(
            Market.Futures, Timeframe.FourHours, TestContext.Current.CancellationToken);

        // What the output of a role records as the template versions it used (FR-PRM-6): the
        // four that its prompt is composed from, in that order.
        Assert.All(Enum.GetValues<Role>(), role =>
            Assert.Equal(
                [
                    (PromptTemplateKind.Role, role.ToString()),
                    (PromptTemplateKind.Market, "Futures"),
                    (PromptTemplateKind.Timeframe, "4H"),
                    (PromptTemplateKind.Profile, "Aggressive"),
                ],
                inUse.For(role).Select(template => (template.Kind, template.Subject))));

        // Each says which version it is, by its id and its number.
        Assert.All(inUse.For(Role.Strategist), template =>
        {
            Assert.True(template.Id > 0);
            Assert.True(template.Version >= 1);
        });
    }

    [Fact]
    public async Task The_latest_version_of_a_template_is_the_one_that_is_read()
    {
        using var application = new AccountsApplication();
        var templates = application.PromptTemplates;

        var before = await templates.InUseAsync(Market.Spot, Timeframe.OneHour, TestContext.Current.CancellationToken);
        var otherBefore = await templates.InUseAsync(Market.Spot, Timeframe.FourHours, TestContext.Current.CancellationToken);

        // The template of the timeframe is edited, which is a new version.
        var next = StoredTemplate.GiveNextVersion(application.Database, PromptTemplateKind.Timeframe, "1H", "Style: Short swing, said anew.");

        var after = await templates.InUseAsync(Market.Spot, Timeframe.OneHour, TestContext.Current.CancellationToken);

        Assert.NotEqual(before.Timeframe.Id, after.Timeframe.Id);
        Assert.Equal(
            (next.Id, before.Timeframe.Version + 1, "Style: Short swing, said anew."),
            (after.Timeframe.Id, after.Timeframe.Version, after.Timeframe.Text));

        // Nothing else of what the agent uses changed, and an agent on another timeframe reads
        // what it read before.
        Assert.Equal(before.Market.Id, after.Market.Id);
        Assert.Equal(before.Profile.Id, after.Profile.Id);
        Assert.Equal(before.Roles.Values.Select(template => template.Id).Order(), after.Roles.Values.Select(template => template.Id).Order());

        var other = await templates.InUseAsync(Market.Spot, Timeframe.FourHours, TestContext.Current.CancellationToken);
        Assert.Equal(otherBefore.Timeframe.Id, other.Timeframe.Id);
    }
}
