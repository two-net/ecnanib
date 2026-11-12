using System.Net;
using Ecnanib.Accounts;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Settings;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Security;

/// <summary>
/// The application defends against request forgery (NFR-SEC-4): a request that changes something
/// is taken only with the token of a page that the application served to the same browser. The
/// forms of signed-in users have such tests beside what they do. These are the forms a visitor
/// can post, and the rule that holds every endpoint to <see cref="ForgeryRules"/>.
/// </summary>
public class RequestForgeryTests(EcnanibApplicationFactory running) : IClassFixture<EcnanibApplicationFactory>
{
    private const string Ada = "ada@example.com";

    [Theory]
    [InlineData("sign-in", "not-the-token")]
    [InlineData("sign-in", null)]
    [InlineData("register", "not-the-token")]
    [InlineData("register", null)]
    [InlineData("forgot-password", "not-the-token")]
    [InlineData("forgot-password", null)]
    [InlineData("reset-password", "not-the-token")]
    [InlineData("reset-password", null)]
    [InlineData("verify-email", "not-the-token")]
    [InlineData("verify-email", null)]
    public async Task A_form_that_a_visitor_posts_without_the_token_of_its_page_is_refused_and_does_nothing(string form, string? token)
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        // An account whose address waits to be verified, and that asked for a reset link: each
        // form has something it could do.
        await application.RegisterAsync(Ada);
        var verification = AccountsApplication.TokenIn(application.Email.Sent[^1]);
        await application.RequestResetAsync(Ada);
        var reset = AccountsApplication.TokenIn(application.Email.Sent[^1]);

        var (path, fields) = form switch
        {
            "sign-in" => (SessionAuthentication.SignInPath, Fields(("Email", Ada), ("Password", AccountsApplication.Password))),
            "register" => ("/register", Fields(
            [
                ("Email", "grace@example.com"),
                ("Password", AccountsApplication.Password),
                .. AccountsApplication.EveryDocument.Select(accepted => (accepted.Document.ToString(), accepted.Version)),
            ])),
            "forgot-password" => ("/forgot-password", Fields(("Email", Ada))),
            "reset-password" => ($"{PasswordResetLink.Path}?token={reset}", Fields(("Password", "a new password of someone else"))),
            _ => ($"{EmailVerification.Path}?token={verification}", Fields()),
        };

        var stored = application.EverythingStored();
        var sent = application.Email.Sent.Count;

        // As a page of another site would post it: to the right address, with the right
        // fields, and with the cookies of the browser, but without what only a page of the
        // application carries.
        await client.PageAsync(path);
        using var content = new FormUrlEncodedContent(
        [
            new("_handler", form),
            .. token is null ? [] : new KeyValuePair<string, string>[] { new("__RequestVerificationToken", token) },
            .. fields,
        ]);
        using var answer = await client.PostAsync(path, content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal(stored, application.EverythingStored());
        Assert.Equal(sent, application.Email.Sent.Count);
        Assert.Empty(Browser.CookiesSetBy(answer));
    }

    [Fact]
    public async Task The_token_of_another_browser_is_not_taken()
    {
        using var application = new AccountsApplication();
        using var one = application.CreateClient();
        using var other = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        // What someone can get from the application themselves: a page, and the token on it.
        // It belongs with the cookie of their own browser, and is of no use in another.
        var token = Browser.AntiforgeryTokenIn(await one.PageAsync(SessionAuthentication.SignInPath));
        await other.PageAsync(SessionAuthentication.SignInPath);

        using var answer = await other.SendFormAsync(
            SessionAuthentication.SignInPath, "sign-in", token, ("Email", Ada), ("Password", AccountsApplication.Password));

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Empty(StoredSession.All(application.Database));

        // In the browser it was made for, it is taken.
        var (path, _) = await one.PostAsync(
            SessionAuthentication.SignInPath, "sign-in", token, ("Email", Ada), ("Password", AccountsApplication.Password));

        Assert.Equal("/", path);
        Assert.Single(StoredSession.All(application.Database));
    }

    [Fact]
    public void Every_endpoint_that_takes_more_than_a_read_checks_the_token_or_is_listed_with_why_it_need_not()
    {
        var withoutTheCheck = Endpoints()
            .Where(endpoint => TakesMoreThanARead(endpoint) && !ChecksTheToken(endpoint))
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .Distinct()
            .ToList();

        var unlisted = withoutTheCheck.Except(ForgeryRules.WithoutTheCheck.Keys).ToList();
        Assert.True(
            unlisted.Count == 0,
            "These endpoints take a request that can change something, without the check of the token against request " +
            $"forgery. Require it on the endpoint, or list the route in {nameof(ForgeryRules)} with why it is safe " +
            $"without: {string.Join(", ", unlisted)}");

        // Nothing is listed that is not there, or that has the check by now.
        var stale = ForgeryRules.WithoutTheCheck.Keys.Except(withoutTheCheck).ToList();
        Assert.True(
            stale.Count == 0,
            $"{nameof(ForgeryRules)} lists routes that the application does not serve without the check: {string.Join(", ", stale)}");

        Assert.All(ForgeryRules.WithoutTheCheck.Values, why => Assert.False(string.IsNullOrWhiteSpace(why)));
    }

    [Fact]
    public void Every_page_has_the_check()
    {
        var pages = Endpoints()
            .Where(endpoint => endpoint.Metadata.GetMetadata<ComponentTypeMetadata>() is not null)
            .ToList();

        // The pages of the application are among the endpoints, and none has turned the check off.
        Assert.Contains(pages, page => page.RoutePattern.RawText == SessionAuthentication.SignInPath);
        Assert.Contains(pages, page => page.RoutePattern.RawText == "/register");
        Assert.All(pages, page => Assert.True(
            ChecksTheToken(page),
            $"The page {page.RoutePattern.RawText} takes a form without the token against request forgery."));
    }

    private static KeyValuePair<string, string>[] Fields(params (string Name, string Value)[] fields) =>
        [.. fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value))];

    /// <summary>The page and endpoint routes that the running application serves.</summary>
    private IEnumerable<RouteEndpoint> Endpoints() =>
        running.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

    /// <summary>Whether an endpoint takes a method that can change something. One that names no method takes every method.</summary>
    private static bool TakesMoreThanARead(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>() is not { } methods
        || methods.HttpMethods.Any(method => !ForgeryRules.ReadingMethods.Contains(method));

    /// <summary>Whether the framework checks the token of a request to an endpoint before the endpoint runs.</summary>
    private static bool ChecksTheToken(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>() is { RequiresValidation: true };
}
