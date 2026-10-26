using System.Net;
using Ecnanib.Accounts.Abstractions;
using Ecnanib.Components.Agents;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Configuration;
using Ecnanib.Tests.Database.Live;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Accounts.Live;

/// <summary>
/// FR-ACC-08 through the application's pipeline against a real database: a live agent's Start is refused until the risk
/// disclaimer is accepted; the acceptance is posted from the agent page and from the review step through the one form
/// <c>RiskDisclaimerNotice</c> carries, recorded with its timestamp, shown on the settings page, and the next Start moves
/// on to the key check.
/// </summary>
[Trait("Category", "Live")]
public class DisclaimerAcceptanceLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private const string Refusal = "Read and accept the risk disclaimer before starting a live agent. The disclaimer and the box to accept it are below.";
    private const string AcceptForm = "value=\"accept-disclaimer\"";
    private const string NoticeHeading = "id=\"risk-disclaimer-title\"";
    private readonly DatabaseLiveFixture _fixture;

    public DisclaimerAcceptanceLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AcceptingOnTheAgentPageRecordsTheTimestampAndLetsTheStartMoveOnToTheKeyCheck()
    {
        const string email = "disclaimer-agent@example.com";
        await StartAsync();
        await using var host = await PipelineTestHost.StartAsync(connectionString: _fixture.ConnectionString);
        var user = await PipelineSession.RegisterAsync(host, email);
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "Live BTC", AgentState.Draft, AgentMode.Live, MarketType.UsdFutures);
        var session = await PipelineSession.SignInAsync(host, email);
        var path = $"/agents/{agent.Id}";

        // The page carries the text with the box, and Start is refused pointing at it.
        var (html, antiforgery) = await PipelineSession.PageAsync(host, path, session);
        var token = PipelineTestHost.AntiforgeryToken(html);
        Assert.Contains(AcceptForm, html, StringComparison.Ordinal);
        Assert.Contains(RiskDisclaimer.AcceptancePrompt, WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        var refused = await PostAsync(host, path, [session, antiforgery], ("_handler", "start-agent"), ("__RequestVerificationToken", token));
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        var refusedHtml = WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync(Ct));
        Assert.Contains(Refusal, refusedHtml, StringComparison.Ordinal);
        Assert.Contains(AcceptForm, refusedHtml, StringComparison.Ordinal);

        // Unticked: the validation message, nothing recorded.
        var checkbox = PipelineTestHost.Inputs(html).Keys.Single(k => k.EndsWith(".Accepted", StringComparison.Ordinal));
        var unticked = await PostAsync(host, path, [session, antiforgery], ("_handler", "accept-disclaimer"), ("__RequestVerificationToken", token));
        Assert.Equal(HttpStatusCode.OK, unticked.StatusCode);
        Assert.Contains("Tick the box to accept the disclaimer.", await unticked.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        await using (var db = _fixture.CreateContext())
            Assert.Equal(0, await db.DisclaimerAcceptances.CountAsync(a => a.UserId == user.Id, Ct));

        // Ticked: one row for the current version with its timestamp, mirrored on the user; the page continues with the notice and without the box.
        var accepted = await PostAsync(host, path, [session, antiforgery], ("_handler", "accept-disclaimer"), ("__RequestVerificationToken", token), (checkbox, "true"));
        Assert.True(accepted.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther, accepted.StatusCode.ToString());
        Assert.Equal(path + "?status=disclaimer-accepted", new Uri(host.Client.BaseAddress!, accepted.Headers.Location!).PathAndQuery);
        await using (var db = _fixture.CreateContext())
        {
            var row = await db.DisclaimerAcceptances.SingleAsync(a => a.UserId == user.Id, Ct);
            Assert.Equal(RiskDisclaimer.Version, row.Version);
            Assert.Equal(row.AcceptedAt, await db.Users.Where(u => u.Id == user.Id).Select(u => u.DisclaimerAcceptedAt).SingleAsync(Ct));
        }

        var (continued, _) = await PipelineSession.PageAsync(host, path + "?status=disclaimer-accepted", session, antiforgery);
        Assert.Contains(RiskDisclaimer.AcceptedNotice, WebUtility.HtmlDecode(continued), StringComparison.Ordinal);
        Assert.DoesNotContain(AcceptForm, continued, StringComparison.Ordinal);
        Assert.Contains(NoticeHeading, continued, StringComparison.Ordinal);

        // Start moves on to the next check, the key.
        var next = await PostAsync(host, path, [session, antiforgery], ("_handler", "start-agent"), ("__RequestVerificationToken", token));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        var nextHtml = WebUtility.HtmlDecode(await next.Content.ReadAsStringAsync(Ct));
        Assert.Contains("The agent cannot start: Choose a Binance key for live mode.", nextHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(Refusal, nextHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(AcceptForm, nextHtml, StringComparison.Ordinal);

        // FR-UI-01: the settings page shows the status instead of the form.
        var (settings, _) = await PipelineSession.PageAsync(host, "/settings", session, antiforgery);
        Assert.Contains($"You accepted version {RiskDisclaimer.Version} on ", WebUtility.HtmlDecode(settings), StringComparison.Ordinal);
        Assert.DoesNotContain(AcceptForm, settings, StringComparison.Ordinal);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task AcceptingOnTheReviewStepContinuesThereWithoutTheBox()
    {
        const string email = "disclaimer-review@example.com";
        await StartAsync();
        await using var host = await PipelineTestHost.StartAsync(connectionString: _fixture.ConnectionString);
        var user = await PipelineSession.RegisterAsync(host, email);
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "Live ETH", AgentState.Draft, AgentMode.Live, MarketType.UsdFutures, symbol: "ETHUSDT");
        var session = await PipelineSession.SignInAsync(host, email);
        var review = WizardSteps.Href(agent.Id, WizardStep.Review);

        // The review step offers the box; the other steps show the text alone (FR-UI-05).
        var (html, antiforgery) = await PipelineSession.PageAsync(host, review, session);
        Assert.Contains(AcceptForm, html, StringComparison.Ordinal);
        Assert.Contains(RiskDisclaimer.AcceptancePrompt, WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        var (basics, _) = await PipelineSession.PageAsync(host, WizardSteps.Href(agent.Id, WizardStep.Basics), session, antiforgery);
        Assert.Contains(NoticeHeading, basics, StringComparison.Ordinal);
        Assert.DoesNotContain(AcceptForm, basics, StringComparison.Ordinal);

        var checkbox = PipelineTestHost.Inputs(html).Keys.Single(k => k.EndsWith(".Accepted", StringComparison.Ordinal));
        var accepted = await PostAsync(host, review, [session, antiforgery], ("_handler", "accept-disclaimer"), ("__RequestVerificationToken", PipelineTestHost.AntiforgeryToken(html)), (checkbox, "true"));
        Assert.True(accepted.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther, accepted.StatusCode.ToString());
        Assert.Equal(review + "?status=disclaimer-accepted", new Uri(host.Client.BaseAddress!, accepted.Headers.Location!).PathAndQuery);

        var (continued, _) = await PipelineSession.PageAsync(host, review + "?status=disclaimer-accepted", session, antiforgery);
        Assert.Contains(RiskDisclaimer.AcceptedNotice, WebUtility.HtmlDecode(continued), StringComparison.Ordinal);
        Assert.DoesNotContain(AcceptForm, continued, StringComparison.Ordinal);
        Assert.Contains(NoticeHeading, continued, StringComparison.Ordinal);
        Assert.Contains("Start the agent", continued, StringComparison.Ordinal);
        await using (var db = _fixture.CreateContext())
            Assert.Equal(1, await db.DisclaimerAcceptances.CountAsync(a => a.UserId == user.Id && a.Version == RiskDisclaimer.Version, Ct));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }

    private static Task<HttpResponseMessage> PostAsync(PipelineTestHost host, string path, IEnumerable<string> cookies, params (string Name, string Value)[] fields) =>
        host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Post, path, cookies: cookies, content: PipelineTestHost.Form(fields)), Ct);
}
