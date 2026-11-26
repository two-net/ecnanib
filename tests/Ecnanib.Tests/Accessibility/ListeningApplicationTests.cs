using Ecnanib.Tests.Accounts;
using Ecnanib.WebUi;
using Ecnanib.WebUi.Layout;

namespace Ecnanib.Tests.Accessibility;

/// <summary>
/// The application as a browser reaches it in a test: listening on a port of this machine, over
/// TLS, in place of answering in memory. Only <c>AccessibilityBrowserTests</c> starts it so,
/// and only when a run is told to drive a browser. This test starts it so with every run, and
/// asks it as a client that takes its certificate: so that way of starting the application
/// works when a release needs it. See docs/accessibility.md.
/// </summary>
public class ListeningApplicationTests
{
    private const string Ada = "ada@example.com";

    [Fact]
    public async Task A_listening_application_answers_over_TLS_on_the_loopback()
    {
        using var application = new AccountsApplication(listens: true);
        await application.RegisterVerifiedAsync(Ada);

        // A port of its own on this machine, and TLS: nothing of it is reached from elsewhere.
        var address = application.ListeningAt;

        Assert.Equal(Uri.UriSchemeHttps, address.Scheme);
        Assert.Equal("127.0.0.1", address.Host);
        Assert.InRange(address.Port, 1024, 65535);

        // The client of such an application takes the certificate which it made for this run, and no other.
        using var client = application.CreateClient();

        Assert.Equal(address, client.BaseAddress);
        Assert.NotNull(application.Certificate);

        // It answers with its pages, and signs a user in: the cookie of a session is sent only
        // over TLS, and kept only from a host that was reached so.
        Assert.Contains("<h1>", await client.PageAsync("/"));

        await client.SignInAsync(Ada);

        Assert.Contains($"<form id=\"{Screens.SignOutFormId}\"", await client.PageAsync(WebUiModule.AccountPath));

        // A client that does not take the certificate reaches nothing.
        using var stranger = new HttpClient { BaseAddress = address };

        await Assert.ThrowsAsync<HttpRequestException>(() => stranger.GetAsync("/", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void An_application_that_answers_in_memory_listens_nowhere()
    {
        using var application = new AccountsApplication();

        // As every other test starts it: asking where it listens says what to do.
        var refused = Assert.Throws<InvalidOperationException>(() => application.ListeningAt);

        Assert.Contains("listens: true", refused.Message);
        Assert.Null(application.Certificate);
    }
}
