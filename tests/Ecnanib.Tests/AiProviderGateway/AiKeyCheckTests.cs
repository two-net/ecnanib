using System.Net;
using Ecnanib.AiProviderGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.AiProviderGateway;

/// <summary>
/// A key of an AI provider is validated with a read-only call (FR-KEY-3): the gateway asks the
/// provider for the list of its models, with the key, and says whether the provider took it.
/// Only the provider's answer to a wrong key is a refusal. The key is in no log event
/// (FR-KEY-8).
/// </summary>
public class AiKeyCheckTests
{
    private const string Key = "a-users-key-0123456789-abcdefghij-Kx4P";

    private const string HttpArea = "Ecnanib.Platform.Adapters.Http.OutboundCallLogger";

    // What Gemini answers a wrong key with, and what it answers something else that is wrong with.
    private const string GeminiWrongKey =
        """
        { "error": { "code": 400, "message": "API key not valid. Please pass a valid API key.", "status": "INVALID_ARGUMENT",
            "details": [ { "@type": "type.googleapis.com/google.rpc.ErrorInfo", "reason": "API_KEY_INVALID", "domain": "googleapis.com" } ] } }
        """;

    private const string GeminiWrongRequest =
        """{ "error": { "code": 400, "message": "Invalid page size.", "status": "INVALID_ARGUMENT" } }""";

    [Fact]
    public async Task Each_provider_is_asked_for_its_models_at_its_address_with_the_key_in_its_own_header()
    {
        var provider = new StandInSystem(_ => StandInSystem.Json(HttpStatusCode.OK, """{ "data": [] }"""));
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var checks = application.Services.GetRequiredService<IAiKeyChecks>();

        foreach (var code in new[] { "claude", "gemini", "gpt", "grok" })
        {
            Assert.Equal(KeyVerdict.Accepted, await checks.CheckAsync(new AiProviderCode(code), Key, TestContext.Current.CancellationToken));
        }

        // The addresses are those of the configuration file. Every call reads, and sends nothing.
        Assert.Equal(
            [
                "GET https://claude.example/v1/models?limit=1",
                "GET https://gemini.example/v1beta/models?pageSize=1",
                "GET https://gpt.example/v1/models",
                "GET https://grok.example/v1/models",
            ],
            provider.Calls.Select(call => $"{call.Method} {call.Address}"));

        var (claude, gemini, gpt, grok) = (provider.Calls[0], provider.Calls[1], provider.Calls[2], provider.Calls[3]);
        Assert.Equal(Key, claude.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", claude.Headers["anthropic-version"]);
        Assert.Equal(Key, gemini.Headers["x-goog-api-key"]);
        Assert.Equal($"Bearer {Key}", gpt.Headers["Authorization"]);
        Assert.Equal($"Bearer {Key}", grok.Headers["Authorization"]);

        // The key is where the provider takes it, and nowhere else: not in an address.
        Assert.All(provider.Calls, call => Assert.DoesNotContain(Key, call.Address.ToString()));
    }

    [Theory]
    [InlineData("claude", HttpStatusCode.Unauthorized, """{ "type": "error", "error": { "type": "authentication_error" } }""")]
    [InlineData("gpt", HttpStatusCode.Unauthorized, """{ "error": { "code": "invalid_api_key" } }""")]
    [InlineData("grok", HttpStatusCode.BadRequest, """{ "code": "invalid-argument", "error": "Incorrect API key provided." }""")]
    [InlineData("grok", HttpStatusCode.Unauthorized, "{}")]
    [InlineData("gemini", HttpStatusCode.BadRequest, GeminiWrongKey)]
    [InlineData("gemini", HttpStatusCode.Unauthorized, "{}")]
    public async Task What_a_provider_answers_a_wrong_key_with_is_a_refusal(string code, HttpStatusCode status, string body)
    {
        var provider = new StandInSystem(_ => StandInSystem.Json(status, body));
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var checks = application.Services.GetRequiredService<IAiKeyChecks>();

        Assert.Equal(KeyVerdict.Refused, await checks.CheckAsync(new AiProviderCode(code), Key, TestContext.Current.CancellationToken));
    }

    [Theory]

    // A request that this code got wrong, which is not the user's key being wrong.
    [InlineData("claude", HttpStatusCode.BadRequest, "{}")]
    [InlineData("gpt", HttpStatusCode.BadRequest, "{}")]
    [InlineData("gemini", HttpStatusCode.BadRequest, GeminiWrongRequest)]
    [InlineData("gemini", HttpStatusCode.BadRequest, "not json")]

    // A block of the service's address or of its region.
    [InlineData("claude", HttpStatusCode.Forbidden, "{}")]
    [InlineData("gemini", HttpStatusCode.Forbidden, "{}")]
    [InlineData("gpt", HttpStatusCode.Forbidden, "{}")]
    [InlineData("grok", HttpStatusCode.Forbidden, "{}")]

    // A limit on requests, and a fault of the provider.
    [InlineData("claude", HttpStatusCode.TooManyRequests, "{}")]
    [InlineData("grok", HttpStatusCode.TooManyRequests, "{}")]
    [InlineData("gpt", HttpStatusCode.InternalServerError, "{}")]
    [InlineData("gemini", HttpStatusCode.ServiceUnavailable, "{}")]
    public async Task Any_other_answer_says_nothing_about_the_key(string code, HttpStatusCode status, string body)
    {
        var provider = new StandInSystem(_ => StandInSystem.Json(status, body));
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var checks = application.Services.GetRequiredService<IAiKeyChecks>();

        Assert.Equal(KeyVerdict.Unreachable, await checks.CheckAsync(new AiProviderCode(code), Key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_provider_that_does_not_answer_says_nothing_about_the_key_and_the_check_does_not_fail()
    {
        Exception failure = new HttpRequestException("The connection was refused.");
        var provider = new StandInSystem(_ => throw failure);
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var checks = application.Services.GetRequiredService<IAiKeyChecks>();

        Assert.Equal(KeyVerdict.Unreachable, await checks.CheckAsync(new AiProviderCode("claude"), Key, TestContext.Current.CancellationToken));

        // One that takes too long ends the same way: the call is given up, and nobody cancelled.
        failure = new TaskCanceledException("The provider took too long.");
        Assert.Equal(KeyVerdict.Unreachable, await checks.CheckAsync(new AiProviderCode("gpt"), Key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_check_that_its_caller_cancels_ends_as_cancelled()
    {
        using var cancelled = new CancellationTokenSource();
        var provider = new StandInSystem(_ =>
        {
            cancelled.Cancel();

            throw new OperationCanceledException(cancelled.Token);
        });
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var checks = application.Services.GetRequiredService<IAiKeyChecks>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => checks.CheckAsync(new AiProviderCode("claude"), Key, cancelled.Token));
    }

    [Fact]
    public async Task A_provider_without_a_check_is_refused_by_name()
    {
        var provider = new StandInSystem(_ => StandInSystem.Status(HttpStatusCode.OK));
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var checks = application.Services.GetRequiredService<IAiKeyChecks>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checks.CheckAsync(new AiProviderCode("a-fifth-provider"), Key, TestContext.Current.CancellationToken));

        Assert.Contains("a-fifth-provider", error.Message);
        Assert.DoesNotContain(Key, error.Message);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task The_call_is_logged_with_its_provider_and_the_key_is_in_no_log_event()
    {
        // With everything that can be logged: the debug level, at which bodies are written too.
        var provider = new StandInSystem(call => call.RequestUri!.Host == "claude.example"
            ? StandInSystem.Json(HttpStatusCode.OK, """{ "data": [ { "id": "claude-test-model" } ] }""")
            : StandInSystem.Json(HttpStatusCode.Unauthorized, """{ "error": { "code": "invalid_api_key" } }"""));
        using var application = provider.Behind(
            AiProviderGatewayModule.HttpClientName, LoggedApplication.Settings().With("Logging:MinimumLevel", "Debug"));
        var checks = application.Services.GetRequiredService<IAiKeyChecks>();

        await checks.CheckAsync(new AiProviderCode("claude"), Key, TestContext.Current.CancellationToken);
        await checks.CheckAsync(new AiProviderCode("gpt"), Key, TestContext.Current.CancellationToken);

        var calls = application.Events().Where(written => written.Area == HttpArea && !written.Has("BodyOf")).ToList();
        Assert.Equal(
            ["claude claude.example/v1/models Succeeded 200", "gpt gpt.example/v1/models Failed 401"],
            calls.Select(call =>
                $"{call.Text("Provider")} {call.Text("Host")}{call.Text("Path")} {call.Text("Outcome")} {call["StatusCode"].GetInt32()}"));
        Assert.All(calls, call => Assert.True(call.Has("DurationMs")));

        var log = application.LogText();
        Assert.DoesNotContain(Key, log);
        Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Key)), log);
    }
}
