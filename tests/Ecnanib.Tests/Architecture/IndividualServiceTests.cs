using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Architecture;

/// <summary>
/// The only account type is the individual user: the model and the UI hold no tenant,
/// organization, workspace, team, or shared resource (FR-ACC-5). The service is free: there is no
/// plan, subscription, payment, invoice, trial, or paid tier (FR-ACC-6). These tests hold the
/// names of tables, columns, pages, and endpoints to <see cref="IndividualServiceRules"/>.
/// </summary>
public class IndividualServiceTests(EcnanibApplicationFactory application) : IClassFixture<EcnanibApplicationFactory>
{
    private const string Explanation =
        "The only account type is the individual user, and the service is free. If the name means something " +
        "else, accept it in IndividualServiceRules with what it means. See docs/ownership.md:";

    [Fact]
    public void No_table_or_column_is_about_a_tenant_a_team_a_shared_resource_or_money_for_the_service()
    {
        using var context = ModelOnly.Context();
        var names = IndividualServiceRules.NamesOf(context.Model);

        Assert.Contains("users.email", names);
        AssertNone(IndividualServiceRules.FindIn(names));
    }

    [Fact]
    public void No_page_or_endpoint_is_about_one()
    {
        var routes = Routes();

        // The pages, the API, and the endpoints of the Web UI are all among them.
        Assert.Contains("/account", routes);
        Assert.Contains("/api/health", routes);
        Assert.Contains("/sign-out", routes);

        AssertNone(IndividualServiceRules.FindIn(routes));
    }

    [Fact]
    public void Every_accepted_name_is_one_that_is_in_use()
    {
        using var context = ModelOnly.Context();
        var inUse = IndividualServiceRules.NamesOf(context.Model).Concat(Routes()).ToHashSet();

        var stale = IndividualServiceRules.Accepted.Keys.Where(name => !inUse.Contains(name)).ToList();

        Assert.True(
            stale.Count == 0,
            $"IndividualServiceRules accepts names that no table, column, page, or endpoint has: {string.Join(", ", stale)}");
    }

    [Fact]
    public void A_name_about_a_tenant_or_about_money_for_the_service_is_found()
    {
        string[] names =
        [
            "team_members",
            "agents.tenant_id",
            "users.organisation",
            "SubscriptionPlans",
            "users.is_paid",
            "users.trial_ends_at",
            "/billing/invoices/{id}",
            "/workspaces",
            "/agents/{id}/share",
            "PAYMENTS",
            "agents.tier",

            // Fine: none of the words, though some of their letters.
            "prompt_templates",
            "two_factor_recovery_codes.code_hash",
            "/account/two-factor",
            "explanations",
            "steam",
            "shares",
            "users.planned_at",
        ];

        var accepted = new Dictionary<string, string> { ["shares"] = "Accepted for this test." };

        Assert.Equal(
            [
                "/agents/{id}/share: names a shared resource",
                "/billing/invoices/{id}: names an invoice and billing",
                "/workspaces: names a workspace",
                "PAYMENTS: names a payment",
                "SubscriptionPlans: names a plan and a subscription",
                "agents.tenant_id: names a tenant",
                "agents.tier: names a paid tier",
                "team_members: names a team",
                "users.is_paid: names a paid tier",
                "users.organisation: names an organization",
                "users.trial_ends_at: names a trial",
            ],
            IndividualServiceRules.FindIn(names, accepted));
    }

    /// <summary>The route of every page and endpoint that the running application serves.</summary>
    private List<string> Routes() =>
    [
        .. application.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .OfType<string>()
            .Distinct(),
    ];

    private static void AssertNone(IReadOnlyList<string> found) =>
        Assert.True(
            found.Count == 0,
            Explanation + Environment.NewLine + string.Join(Environment.NewLine, found.Select(name => $"  {name}")));
}
