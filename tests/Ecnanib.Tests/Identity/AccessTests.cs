using System.Net;
using System.Reflection;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Mono.Cecil;

namespace Ecnanib.Tests.Identity;

/// <summary>
/// Pages are private by default, on HTTP and in a circuit. A circuit never reaches middleware, so these
/// also check what the router reads: see Access control in docs/architecture.md.
/// </summary>
public sealed class AccessTests(SharedApp app) : IClassFixture<SharedApp>
{
    /// <summary>Adding to this makes a page public, so it should be a decision.</summary>
    private static readonly string[] PublicPages =
    [
        "/",
        "/Error",
        "/forgot-password",
        "/legal/accept",
        "/legal/risk-disclosure",
        "/legal/terms",
        "/not-found",
        "/register",
        "/reset-password",
        "/sign-in",
    ];

    /// <summary>
    /// SignalR's own routes and the framework's, which have to work before anyone signs in, and the
    /// fallback that static assets map in Development.
    /// </summary>
    private static readonly string[] FrameworkRoutes = ["/_blazor", "/_framework", "{**path:file}"];

    private static IEnumerable<Type> Pages =>
        typeof(Program).Assembly.GetTypes().Where(type => type.GetCustomAttributes<RouteAttribute>().Any());

    [Fact]
    public void Every_page_is_private_unless_it_is_on_the_list()
    {
        var pages = Pages.ToList();

        Assert.NotEmpty(pages);
        Assert.All(pages, page => Assert.True(page.IsDefined(typeof(AuthorizeAttribute), inherit: true), $"{page} isn't private."));

        var open = pages
            .Where(page => page.IsDefined(typeof(AllowAnonymousAttribute), inherit: true))
            .SelectMany(page => page.GetCustomAttributes<RouteAttribute>())
            .Select(route => route.Template)
            .Order(StringComparer.Ordinal);

        Assert.Equal(PublicPages.Order(StringComparer.Ordinal), open);
    }

    /// <summary>Attributes don't reach endpoints mapped in code, so each has to say what it wants.</summary>
    [Fact]
    public void Every_endpoint_says_who_can_reach_it()
    {
        app.Start();

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        var undeclared =
            from endpoint in endpoints
            where endpoint.Metadata.GetMetadata<ComponentTypeMetadata>() is null
                && !FrameworkRoutes.Any(prefix => endpoint.RoutePattern.RawText!.StartsWith(prefix, StringComparison.Ordinal))
                && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null
                && endpoint.Metadata.GetMetadata<IAuthorizeData>() is null
            select endpoint.DisplayName;

        Assert.NotEmpty(endpoints);
        Assert.Empty(undeclared);
    }

    /// <summary>AllowAnonymous on the components builder reaches every page, and this is what would show it.</summary>
    [Fact]
    public void A_private_page_has_no_anonymous_metadata()
    {
        app.Start();

        var account = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == "/account");

        Assert.Null(account.Metadata.GetMetadata<IAllowAnonymous>());
        Assert.NotNull(account.Metadata.GetMetadata<IAuthorizeData>());
    }

    /// <summary>
    /// A page that takes a secret keeps it off the circuit by staying static, and it's the attribute that
    /// applies the stricter limit, so losing either would pass every test that only uses the form.
    /// </summary>
    [Theory]
    [InlineData("/sign-in")]
    [InlineData("/register")]
    [InlineData("/account/administrator")]
    [InlineData("/account/password")]
    public void A_page_that_takes_a_secret_stays_static_and_is_limited(string route)
    {
        app.Start();

        var page = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == route);

        Assert.NotNull(page.Metadata.GetMetadata<ExcludeFromInteractiveRoutingAttribute>());
        Assert.Equal("authentication", page.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
    }

    /// <summary>RouteView checks nothing, so a circuit would render a private page for anyone who navigated to it.</summary>
    [Fact]
    public void The_router_checks_access_inside_a_circuit()
    {
        using var module = ModuleDefinition.ReadModule(typeof(Program).Assembly.Location);

        var references =
            from type in module.GetTypes()
            where type.FullName.StartsWith("Ecnanib.Components.Routes", StringComparison.Ordinal)
            from method in type.Methods
            where method.HasBody
            from instruction in method.Body.Instructions
            where instruction.Operand is MemberReference
            select ((MemberReference)instruction.Operand).FullName;

        Assert.Contains(references, reference => reference.Contains(".Authorization.AuthorizeRouteView", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_visitor_to_a_private_page_goes_to_sign_in_and_can_come_back()
    {
        using var visitor = Visitor.To(app);

        using var response = await visitor.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/sign-in", response.Headers.Location?.AbsolutePath);
        Assert.Equal("?returnUrl=%2Faccount", response.Headers.Location?.Query);
    }

    /// <summary>The landing page is interactive, so visitors need the hub without being signed in.</summary>
    [Fact]
    public async Task A_visitor_can_start_a_circuit()
    {
        using var visitor = Visitor.To(app);

        using var response = await visitor.Client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_visitor_sees_ways_in_on_the_landing_page()
    {
        using var visitor = Visitor.To(app);

        var page = await visitor.PageAsync("/");

        Assert.Contains("href=\"/sign-in\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/register\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("action=\"/sign-out\"", page, StringComparison.Ordinal);
    }
}
