using System.Net;
using System.Security.Claims;
using Ecnanib.Platform.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging;

/// <summary>
/// Events written inside a request or a cycle carry the correlation properties that apply: request
/// id, user id, agent id, cycle id, role, provider, model, and exchange order id (NFR-LOG-3).
/// </summary>
public class CorrelationTests
{
    public static TheoryData<string, Correlation> EachProperty => new()
    {
        { "RequestId", new Correlation(RequestId: "the-value") },
        { "UserId", new Correlation(UserId: "the-value") },
        { "AgentId", new Correlation(AgentId: "the-value") },
        { "CycleId", new Correlation(CycleId: "the-value") },
        { "Role", new Correlation(Role: "the-value") },
        { "Provider", new Correlation(Provider: "the-value") },
        { "Model", new Correlation(Model: "the-value") },
        { "ExchangeOrderId", new Correlation(ExchangeOrderId: "the-value") },
    };

    [Theory]
    [MemberData(nameof(EachProperty))]
    public async Task A_property_is_on_every_event_inside_its_scope_and_on_none_after_it(string property, Correlation correlation)
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());
        var logger = application.Logger();

        using (logger.BeginCorrelation(correlation))
        {
            // Work continues on another thread, and another component logs.
            await Task.Yield();
            await Task.Run(
                () => application.Logger("Ecnanib.Tests.Deeper").LogInformation("Written {Where}", "inside"),
                TestContext.Current.CancellationToken);
        }

        logger.LogInformation("Written {Where}", "after");

        var inside = Assert.Single(application.Events(), written => written.Text("Where") == "inside");
        Assert.Equal("the-value", inside.Text(property));

        // Only the property that was set is added.
        Assert.Equal([property], EachProperty.Select(row => row.Data.Item1).Where(inside.Has));

        var after = Assert.Single(application.Events(), written => written.Text("Where") == "after");
        Assert.False(after.Has(property));
    }

    [Fact]
    public void An_inner_scope_adds_to_the_outer_one()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());
        var logger = application.Logger();

        using (logger.BeginCorrelation(new Correlation(AgentId: "agent-3", CycleId: "cycle-8")))
        {
            using (logger.BeginCorrelation(new Correlation(Role: "Strategist", Provider: "gemini", Model: "gemini-pro")))
            {
                logger.LogInformation("Written {Where}", "in the role call");
            }

            using (logger.BeginCorrelation(new Correlation(ExchangeOrderId: "9001")))
            {
                logger.LogInformation("Written {Where}", "with the order");
            }
        }

        var roleCall = Assert.Single(application.Events(), written => written.Text("Where") == "in the role call");
        Assert.Equal(
            ["agent-3", "cycle-8", "Strategist", "gemini", "gemini-pro", null],
            new[] { "AgentId", "CycleId", "Role", "Provider", "Model", "ExchangeOrderId" }.Select(roleCall.Text));

        var order = Assert.Single(application.Events(), written => written.Text("Where") == "with the order");
        Assert.Equal(
            ["agent-3", "cycle-8", null, "9001"],
            new[] { "AgentId", "CycleId", "Role", "ExchangeOrderId" }.Select(order.Text));
    }

    [Fact]
    public async Task An_event_written_while_a_request_is_handled_carries_the_request_id()
    {
        // With the host's own request logging off, so the id can only come from the application.
        using var application = new LoggedApplication(
            LoggedApplication.Settings().WithJson("Logging:Overrides", """{ "Microsoft.AspNetCore": "None" }"""),
            host => host.HandleAtTheEnd(LogSomething));
        using var client = application.CreateClient();

        using var first = await client.GetAsync("/logs-something", TestContext.Current.CancellationToken);
        using var second = await client.GetAsync("/logs-something", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        var written = application.Events().Where(e => e.Text("Where") == "in a request").ToList();
        Assert.Equal(2, written.Count);
        Assert.All(written, e => Assert.False(string.IsNullOrEmpty(e.Text("RequestId"))));
        Assert.NotEqual(written[0].Text("RequestId"), written[1].Text("RequestId"));

        // Nobody is signed in.
        Assert.All(written, e => Assert.False(e.Has("UserId")));
    }

    [Fact]
    public async Task An_event_written_for_a_signed_in_request_carries_the_user_id()
    {
        // Without signing in: the user is set the way the host's own middleware can set it,
        // before the application's pipeline runs. SessionTests covers a user who signed in.
        using var application = new LoggedApplication(
            LoggedApplication.Settings(),
            host => host
                .PrepareEachRequest(context => context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "user-7")], authenticationType: "Test")))
                .HandleAtTheEnd(LogSomething));
        using var client = application.CreateClient();

        using var response = await client.GetAsync("/logs-something", TestContext.Current.CancellationToken);

        var written = Assert.Single(application.Events(), e => e.Text("Where") == "in a request");
        Assert.Equal("user-7", written.Text("UserId"));
        Assert.False(string.IsNullOrEmpty(written.Text("RequestId")));
    }

    private static Task<bool> LogSomething(HttpContext context)
    {
        if (context.Request.Path != "/logs-something")
        {
            return Task.FromResult(false);
        }

        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Ecnanib.Tests.SomeComponent")
            .LogInformation("Written {Where}", "in a request");
        context.Response.StatusCode = StatusCodes.Status204NoContent;

        return Task.FromResult(true);
    }
}
