using System.Net;
using System.Text;
using Ecnanib.AiProviderGateway;
using Ecnanib.Platform.Logging;
using Ecnanib.Shared;
using Ecnanib.Tests.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging;

/// <summary>
/// Every outbound call to an AI provider or to Binance is logged with its duration, its outcome,
/// and its usage or request weight. Request and response bodies are logged only at the debug
/// level, which is off unless the configuration file turns it on (NFR-LOG-5).
/// </summary>
public class OutboundCallTests
{
    private const string Signature = "SIGNATURE-MARKER-0a1b2c3d";
    private const string ApiKey = "APIKEY-MARKER-4e5f6a7b";

    private const string HttpArea = "Ecnanib.Platform.Adapters.Http.OutboundCallLogger";
    private const string RouterArea = "Ecnanib.AiProviderGateway.AiProviderRouter";

    [Fact]
    public async Task A_call_through_a_client_of_the_host_is_logged_with_duration_outcome_and_request_weight()
    {
        using var application = WithExternalSystem(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Headers =
            {
                { "X-MBX-USED-WEIGHT-1M", "12" },
                { "X-MBX-ORDER-COUNT-10S", "1" },
                { "Set-Cookie", "session=not-usage" },
            },
            Content = new StringContent("""{"serverTime":1}""", Encoding.UTF8, "application/json"),
        });
        using var client = ClientOf(application);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"https://fapi.exchange.example/fapi/v1/order?symbol=BTCUSDT&signature={Signature}")
        {
            Headers = { { "X-MBX-APIKEY", ApiKey } },
            Content = new StringContent("quantity=1&side=BUY"),
        };
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var call = Assert.Single(application.Events(), written => written.Area == HttpArea);
        Assert.Equal("Information", call.Level);
        Assert.Equal("POST", call.Text("Method"));
        Assert.Equal("fapi.exchange.example", call.Text("Host"));
        Assert.Equal("/fapi/v1/order", call.Text("Path"));
        Assert.Equal("Succeeded", call.Text("Outcome"));
        Assert.Equal(200, call["StatusCode"].GetInt32());
        Assert.InRange(call["DurationMs"].GetDouble(), 0, 60_000);

        // What the exchange reported it has counted, and no other header.
        Assert.Equal(
            [("X-MBX-ORDER-COUNT-10S", "1"), ("X-MBX-USED-WEIGHT-1M", "12")],
            call["Usage"].EnumerateObject().Select(header => (header.Name, header.Value.GetString()!)).Order());

        // Not the query, not a header, and at this level not a body.
        var log = application.LogText();
        Assert.DoesNotContain(Signature, log);
        Assert.DoesNotContain(ApiKey, log);
        Assert.DoesNotContain("serverTime", log);
        Assert.DoesNotContain("quantity=1", log);
    }

    [Fact]
    public async Task A_call_that_the_other_system_refuses_is_logged_as_failed()
    {
        using var application = WithExternalSystem(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { { "X-MBX-USED-WEIGHT-1M", "2400" } },
        });
        using var client = ClientOf(application);

        using var response = await client.GetAsync("https://api.exchange.example/api/v3/time", TestContext.Current.CancellationToken);

        var call = Assert.Single(application.Events(), written => written.Area == HttpArea);
        Assert.Equal("Warning", call.Level);
        Assert.Equal("Failed", call.Text("Outcome"));
        Assert.Equal(429, call["StatusCode"].GetInt32());
        Assert.Equal("2400", call["Usage"].GetProperty("X-MBX-USED-WEIGHT-1M").GetString());
        Assert.True(call.Has("DurationMs"));
    }

    [Fact]
    public async Task A_call_that_never_gets_an_answer_is_logged_as_failed_with_the_error()
    {
        using var application = WithExternalSystem(_ => throw new HttpRequestException("The connection was refused."));
        using var client = ClientOf(application);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("https://api.ai.example/v1/models", TestContext.Current.CancellationToken));

        var call = Assert.Single(application.Events(), written => written.Area == HttpArea);
        Assert.Equal("Warning", call.Level);
        Assert.Equal("Failed", call.Text("Outcome"));
        Assert.Equal("/v1/models", call.Text("Path"));
        Assert.True(call.Has("DurationMs"));
        Assert.Contains("The connection was refused.", call.Exception);
    }

    [Fact]
    public async Task Bodies_are_logged_only_when_the_file_turns_the_debug_level_on()
    {
        using var application = WithExternalSystem(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"answer":"buy","apiKey":"echoed-back"}""") },
            LoggedApplication.Settings().WithJson("Logging:Overrides", """{ "Ecnanib.Platform.Adapters.Http": "Debug" }"""));
        using var client = ClientOf(application);

        using var response = await client.PostAsync(
            "https://api.ai.example/v1/messages",
            new StringContent("""{"prompt":"What now?"}"""),
            TestContext.Current.CancellationToken);

        // The caller still reads the whole answer.
        Assert.Equal(
            """{"answer":"buy","apiKey":"echoed-back"}""",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var written = application.Events().Where(e => e.Area == HttpArea).ToList();
        Assert.Equal(["request", null, "response"], written.Select(e => e.Text("BodyOf")));
        Assert.Equal(["Debug", "Information", "Debug"], written.Select(e => e.Level));
        Assert.Equal("""{"prompt":"What now?"}""", written[0].Text("Body"));

        // A body passes the same masking as everything else.
        Assert.Equal("""{"answer":"buy","apiKey":"***"}""", written[2].Text("Body"));
    }

    [Fact]
    public async Task An_AI_call_is_logged_with_duration_outcome_token_usage_and_what_it_belongs_to()
    {
        var adapter = new FakeAdapter("claude");
        using var application = new LoggedApplication(
            LoggedApplication.Settings(),
            host => host.ConfigureServices(services => services.AddSingleton<IAiProviderAdapter>(adapter)));
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();
        adapter.Logger = application.Logger("Ecnanib.Tests.FakeAdapter");

        // The caller, a decision cycle in time, says which agent, cycle, and role the call is for.
        using (application.Logger().BeginCorrelation(new Correlation(AgentId: "agent-3", CycleId: "cycle-8", Role: "RiskManager")))
        {
            await gateway.CompleteAsync(
                new AiProviderCode("Claude"),
                new AiCallRequest("claude-test-model", "The prompt text", "{}"),
                ApiKey,
                TestContext.Current.CancellationToken);
        }

        var call = Assert.Single(application.Events(), written => written.Area == RouterArea);
        Assert.Equal("Information", call.Level);
        Assert.Equal("Succeeded", call.Text("Outcome"));
        Assert.InRange(call["DurationMs"].GetDouble(), 0, 60_000);
        Assert.Equal(11, call["InputTokens"].GetInt32());
        Assert.Equal(22, call["OutputTokens"].GetInt32());
        Assert.Equal(
            ["claude", "claude-test-model", "agent-3", "cycle-8", "RiskManager"],
            new[] { "Provider", "Model", "AgentId", "CycleId", "Role" }.Select(call.Text));

        // What the adapter logs during the call belongs to the same provider and model.
        var fromAdapter = Assert.Single(application.Events(), written => written.Area == "Ecnanib.Tests.FakeAdapter");
        Assert.Equal(["claude", "claude-test-model"], new[] { "Provider", "Model" }.Select(fromAdapter.Text));

        var log = application.LogText();
        Assert.DoesNotContain(ApiKey, log);
        Assert.DoesNotContain("The prompt text", log);
    }

    [Fact]
    public async Task An_AI_call_that_fails_is_logged_as_failed_and_the_error_still_reaches_the_caller()
    {
        var adapter = new FakeAdapter("grok") { Failure = new TimeoutException("The provider did not answer.") };
        using var application = new LoggedApplication(
            LoggedApplication.Settings(),
            host => host.ConfigureServices(services => services.AddSingleton<IAiProviderAdapter>(adapter)));
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var error = await Assert.ThrowsAsync<TimeoutException>(() => gateway.CompleteAsync(
            adapter.Provider, new AiCallRequest("grok-test-model", "prompt", "{}"), ApiKey, TestContext.Current.CancellationToken));

        Assert.Same(adapter.Failure, error);
        var call = Assert.Single(application.Events(), written => written.Area == RouterArea);
        Assert.Equal("Warning", call.Level);
        Assert.Equal("Failed", call.Text("Outcome"));
        Assert.True(call.Has("DurationMs"));
        Assert.Equal(["grok", "grok-test-model"], new[] { "Provider", "Model" }.Select(call.Text));
        Assert.Contains("The provider did not answer.", call.Exception);
        Assert.DoesNotContain(ApiKey, application.LogText());
    }

    /// <summary>
    /// The application with a named client whose calls end in <paramref name="answer"/> instead of
    /// the network. The client is created by the host's factory, as an adapter's client is.
    /// </summary>
    private static LoggedApplication WithExternalSystem(Func<HttpRequestMessage, HttpResponseMessage> answer, SettingsJson? settings = null) =>
        new(
            settings ?? LoggedApplication.Settings(),
            host => host.ConfigureServices(services => services
                .AddHttpClient("external-system")
                .ConfigurePrimaryHttpMessageHandler(() => new ExternalSystem(answer))));

    private static HttpClient ClientOf(LoggedApplication application) =>
        application.Services.GetRequiredService<IHttpClientFactory>().CreateClient("external-system");

    private sealed class ExternalSystem(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }

    private sealed class FakeAdapter(string provider) : IAiProviderAdapter
    {
        public AiProviderCode Provider { get; } = new(provider);

        public Exception? Failure { get; init; }

        /// <summary>Where the adapter logs what it does during a call, as a real one would.</summary>
        public ILogger? Logger { get; set; }

        public Task<AiCallResult> CompleteAsync(AiCallRequest request, string apiKey, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                return Task.FromException<AiCallResult>(Failure);
            }

            Logger?.LogInformation("Asked the provider");

            return Task.FromResult(new AiCallResult("{}", new AiTokenUsage(11, 22), TimeSpan.FromMilliseconds(3)));
        }
    }
}
