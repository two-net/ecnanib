using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Ecnanib.Tests.Logging;

/// <summary>
/// An unhandled error is logged with full detail and correlation properties. The user sees a
/// plain message with a reference id, and that id is on the logged event (NFR-LOG-6).
/// </summary>
public class UnhandledErrorTests
{
    private const string Detail = "The order book was empty, which nothing expected";

    [Fact]
    public async Task A_failed_API_request_is_answered_with_a_plain_message_and_the_reference_of_the_logged_error()
    {
        using var application = FailingApplication();
        using var client = application.CreateClient();

        using var response = await client.GetAsync("/api/fails", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var problem = JsonDocument.Parse(body);
        var reference = problem.RootElement.GetProperty("referenceId").GetString()!;
        Assert.Equal("Something went wrong.", problem.RootElement.GetProperty("title").GetString());
        Assert.Contains(reference, problem.RootElement.GetProperty("detail").GetString());

        AssertPlain(body);
        AssertLoggedOnceAs(application, reference);
    }

    [Fact]
    public async Task A_failed_page_request_shows_a_plain_message_and_the_reference_of_the_logged_error()
    {
        using var application = FailingApplication();
        using var client = application.CreateClient();

        using var response = await client.GetAsync("/fails", TestContext.Current.CancellationToken);
        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<h1>Something went wrong.</h1>", page);

        var reference = Regex.Match(page, "<code>(?<reference>[A-Z0-9]+)</code>").Groups["reference"].Value;
        Assert.Equal(10, reference.Length);

        AssertPlain(page);
        AssertLoggedOnceAs(application, reference);
    }

    [Fact]
    public async Task Each_error_gets_a_reference_of_its_own()
    {
        using var application = FailingApplication();
        using var client = application.CreateClient();

        var references = new List<string>();
        for (var request = 0; request < 3; request++)
        {
            using var response = await client.GetAsync("/api/fails", TestContext.Current.CancellationToken);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            references.Add(problem.RootElement.GetProperty("referenceId").GetString()!);
        }

        Assert.Equal(3, references.Distinct().Count());
        Assert.Equal(
            references,
            application.Events().Where(written => written.Level == "Error").Select(written => written.Text("ReferenceId")));
    }

    [Fact]
    public async Task The_error_page_opened_on_its_own_shows_no_reference()
    {
        using var application = FailingApplication();
        using var client = application.CreateClient();

        using var response = await client.GetAsync("/Error", TestContext.Current.CancellationToken);
        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("<h1>Something went wrong.</h1>", page);
        Assert.DoesNotContain("<code>", page);
        Assert.DoesNotContain(application.Events(), written => written.Level == "Error");
    }

    /// <summary>
    /// The application as deployed, with two paths that fail: one under the API and one that is
    /// answered like a page. In the Development environment the host shows its developer page instead.
    /// </summary>
    private static LoggedApplication FailingApplication() =>
        new(
            LoggedApplication.Settings(),
            host => host.UseEnvironment("Production").HandleAtTheEnd(context =>
                context.Request.Path.Value is "/api/fails" or "/fails"
                    ? throw new InvalidOperationException(Detail)
                    : Task.FromResult(false)));

    /// <summary>The user is told that something went wrong, and nothing about what.</summary>
    private static void AssertPlain(string shown)
    {
        Assert.DoesNotContain(Detail, shown);
        Assert.DoesNotContain(nameof(InvalidOperationException), shown);
        Assert.DoesNotContain("   at ", shown);
    }

    private static void AssertLoggedOnceAs(LoggedApplication application, string reference)
    {
        // One event for the error, and not a second one from the host without the reference.
        var error = Assert.Single(application.Events(), written => written.Level is "Error" or "Fatal");
        Assert.Equal(reference, error.Text("ReferenceId"));
        Assert.Equal("Ecnanib.Platform.Logging.UnhandledErrors", error.Area);

        // Full detail: the type, the message, and where it was thrown.
        Assert.Contains($"System.InvalidOperationException: {Detail}", error.Exception);
        Assert.Contains("   at ", error.Exception);

        // And the request it happened in.
        Assert.False(string.IsNullOrEmpty(error.Text("RequestId")));
    }
}
