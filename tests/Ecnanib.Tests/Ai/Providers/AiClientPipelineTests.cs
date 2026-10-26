using System.Net;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Ai.Configuration;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Ai.Providers;

/// <summary>The shared send/translate/time/log/retry pipeline, exercised through one client.</summary>
public class AiClientPipelineTests
{
    private static readonly AiKeyHandle Handle = AiTestHost.Handle(AiProvider.Gpt);
    private static readonly AiRequest Request = new("model-one", "SYSTEM-PROMPT-TEXT", "USER-PROMPT-TEXT", Purpose: "risk-manager");

    private static AiOptions FastRetries(int maxAttempts = 3) =>
        new() { Retry = new AiRetryOptions { MaxAttempts = maxAttempts, BaseDelayMs = 1, MaxDelayMs = 1 } };

    [Fact]
    public async Task HttpClientTimeoutIsATransientTimeoutAndRetried()
    {
        var handler = new FakeHttpMessageHandler().EnqueueHang().EnqueueHang().EnqueueHang();
        var client = AiTestHost.Direct(AiProvider.Gpt, handler, FastRetries(), timeout: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => client.CompleteAsync(Handle, Request, AiTestHost.Ct));

        Assert.Equal(AiErrorKind.Timeout, ex.Kind);
        Assert.True(ex.IsTransient);
        Assert.Equal("model-one", ex.Model);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task TimeoutThenSuccessCompletes()
    {
        var handler = new FakeHttpMessageHandler().EnqueueHang().Enqueue(AiTestHost.Json("gpt/responses-completed.json"));
        var client = AiTestHost.Direct(AiProvider.Gpt, handler, FastRetries(), timeout: TimeSpan.FromMilliseconds(50));

        var completion = await client.CompleteAsync(Handle, Request, AiTestHost.Ct);

        Assert.Equal(AiFinishReason.Stop, completion.FinishReason);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CallerCancellationIsNotTranslated()
    {
        var handler = new FakeHttpMessageHandler().EnqueueHang();
        var client = AiTestHost.Direct(AiProvider.Gpt, handler, FastRetries());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

#pragma warning disable xUnit1051 // the test supplies its own cancelled token
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CompleteAsync(Handle, Request, cts.Token));
#pragma warning restore xUnit1051
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CancellationDuringTheRequestIsNotATimeout()
    {
        var handler = new FakeHttpMessageHandler().EnqueueHang();
        var client = AiTestHost.Direct(AiProvider.Gpt, handler, FastRetries(), timeout: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

#pragma warning disable xUnit1051 // the test supplies its own cancelled token
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CompleteAsync(Handle, Request, cts.Token));
#pragma warning restore xUnit1051
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RateLimitThenSuccessHonoursRetryAfter()
    {
        var time = new FakeTimeProvider();
        var handler = new FakeHttpMessageHandler()
            .Enqueue(AiTestHost.Json("gpt/error-429-rate-limit.json", HttpStatusCode.TooManyRequests, ("Retry-After", "5")))
            .Enqueue(AiTestHost.Json("gpt/responses-completed.json"));
        await using var services = AiTestHost.BuildServices(handler, time: time);

        var task = services.Client(AiProvider.Gpt).CompleteAsync(Handle, Request, AiTestHost.Ct);

        await AiTestHost.WaitForAsync(() => handler.Requests.Count == 1);
        time.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(20, AiTestHost.Ct);
        Assert.Single(handler.Requests);
        time.Advance(TimeSpan.FromSeconds(1));
        await AiTestHost.WaitForAsync(() => handler.Requests.Count == 2);
        var completion = await task.WaitAsync(TimeSpan.FromSeconds(5), AiTestHost.Ct);
        Assert.Equal(AiFinishReason.Stop, completion.FinishReason);
    }

    [Fact]
    public async Task OverloadThenSuccessHonoursRetryAfterHeader()
    {
        var time = new FakeTimeProvider();
        var handler = new FakeHttpMessageHandler()
            .Enqueue(AiTestHost.Json("gpt/error-500.json", HttpStatusCode.ServiceUnavailable, ("Retry-After", "5")))
            .Enqueue(AiTestHost.Json("gpt/responses-completed.json"));
        await using var services = AiTestHost.BuildServices(handler, time: time);

        var task = services.Client(AiProvider.Gpt).CompleteAsync(Handle, Request, AiTestHost.Ct);

        await AiTestHost.WaitForAsync(() => handler.Requests.Count == 1);
        time.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(20, AiTestHost.Ct);
        Assert.Single(handler.Requests);
        time.Advance(TimeSpan.FromSeconds(1));
        await AiTestHost.WaitForAsync(() => handler.Requests.Count == 2);
        Assert.Equal("resp_01", (await task.WaitAsync(TimeSpan.FromSeconds(5), AiTestHost.Ct)).ResponseId);
    }

    [Fact]
    public async Task NetworkFailureThenSuccessIsRetriedAndLogged()
    {
        var logs = new TestLoggerProvider();
        var handler = new FakeHttpMessageHandler()
            .Enqueue(new HttpRequestException("connection reset by peer"))
            .Enqueue(AiTestHost.Json("gpt/responses-completed.json"));
        var client = AiTestHost.Direct(AiProvider.Gpt, handler, FastRetries(), logs: logs);

        var completion = await client.CompleteAsync(Handle, Request, AiTestHost.Ct);

        Assert.Equal("resp_01", completion.ResponseId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("retrying"));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("failed with Network"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[1,2]")]
    public async Task UnreadableSuccessBodiesAreInvalidAndNotRetried(string body)
    {
        var handler = new FakeHttpMessageHandler().When(_ => true, _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, body));
        var client = AiTestHost.Direct(AiProvider.Gpt, handler, FastRetries());

        var ex = await Assert.ThrowsAsync<AiRequestException>(() => client.CompleteAsync(Handle, Request, AiTestHost.Ct));

        Assert.Equal(AiErrorKind.InvalidResponse, ex.Kind);
        Assert.False(ex.IsTransient);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GatewayHtmlIsATransientServerError()
    {
        var handler = new FakeHttpMessageHandler().When(_ => true, _ => FakeHttpMessageHandler.Text(HttpStatusCode.BadGateway, "<html>502</html>"));
        var client = AiTestHost.Direct(AiProvider.Gpt, handler, FastRetries(2));

        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => client.ListModelsAsync(Handle, AiTestHost.Ct));

        Assert.Equal(AiErrorKind.ServerError, ex.Kind);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SuccessLogLineCarriesIdentityAndTokensButNotThePrompt()
    {
        var logs = new TestLoggerProvider();
        var time = new FakeTimeProvider();
        var handler = new FakeHttpMessageHandler().When(_ => true, _ =>
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            return AiTestHost.Json("gpt/responses-completed.json");
        });
        await using var services = AiTestHost.BuildServices(handler, logs, time: time);

        var completion = await services.Client(AiProvider.Gpt).CompleteAsync(Handle, Request, AiTestHost.Ct);

        Assert.Equal(TimeSpan.FromMilliseconds(250), completion.Latency);
        var line = Assert.Single(logs.Entries, e => e.Level == LogLevel.Information && e.Category.Contains("GptClient"));
        foreach (var fragment in new[] { "Gpt", "complete", "risk-manager", "model-one", "Stop", "250 ms", "20/30", "key-1", AiTestHost.Masked })
            Assert.Contains(fragment, line.Message);
        Assert.DoesNotContain("USER-PROMPT-TEXT", logs.AllText);
        Assert.DoesNotContain("SYSTEM-PROMPT-TEXT", logs.AllText);
        Assert.DoesNotContain(AiTestHost.Key, logs.AllText);
    }

    [Fact]
    public async Task MissingUsageIsRecordedAsZeroWithAWarning()
    {
        var logs = new TestLoggerProvider();
        const string body = """{"id":"r","status":"completed","model":"m","output":[{"type":"message","content":[{"type":"output_text","text":"hi"}]}]}""";
        var handler = new FakeHttpMessageHandler().When(_ => true, _ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, body));
        var client = AiTestHost.Direct(AiProvider.Gpt, handler, logs: logs);

        var completion = await client.CompleteAsync(Handle, Request, AiTestHost.Ct);

        Assert.Equal(AiTokenUsage.None, completion.Usage);
        Assert.Equal("hi", completion.Text);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("no usage"));
    }

    /// <summary>NFR-COST-01: <c>Providers:MaxOutputTokens</c> bounds every provider's wire field; a request may ask for fewer, never more.</summary>
    [Theory]
    [InlineData(AiProvider.Gpt, null, 100)]
    [InlineData(AiProvider.Gpt, 500, 100)]
    [InlineData(AiProvider.Gpt, 50, 50)]
    [InlineData(AiProvider.Grok, null, 100)]
    [InlineData(AiProvider.Grok, 500, 100)]
    [InlineData(AiProvider.Grok, 50, 50)]
    [InlineData(AiProvider.Claude, null, 100)]
    [InlineData(AiProvider.Claude, 500, 100)]
    [InlineData(AiProvider.Claude, 50, 50)]
    [InlineData(AiProvider.Gemini, null, 100)]
    [InlineData(AiProvider.Gemini, 500, 100)]
    [InlineData(AiProvider.Gemini, 50, 50)]
    public async Task MaxOutputTokensIsCappedByConfigurationOnEveryProvider(AiProvider provider, int? requested, int expected)
    {
        var fixture = provider switch
        {
            AiProvider.Claude => "claude/messages-end-turn.json",
            AiProvider.Gemini => "gemini/generate-stop.json",
            AiProvider.Grok => "grok/responses-completed.json",
            _ => "gpt/responses-completed.json",
        };
        var handler = new FakeHttpMessageHandler().When(_ => true, _ => AiTestHost.Json(fixture));
        var client = AiTestHost.Direct(provider, handler, new AiOptions { MaxOutputTokens = 100 });

        await client.CompleteAsync(AiTestHost.Handle(provider), new AiRequest("model-one", "s", "u", MaxOutputTokens: requested), AiTestHost.Ct);

        var json = Assert.Single(handler.Requests).Json;
        var sent = provider switch
        {
            AiProvider.Claude => json.GetProperty("max_tokens").GetInt32(),
            AiProvider.Gemini => json.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32(),
            _ => json.GetProperty("max_output_tokens").GetInt32(),
        };
        Assert.Equal(expected, sent);
    }

    [Fact]
    public async Task ProviderMismatchIsRejectedBeforeAnyRequest()
    {
        var handler = new FakeHttpMessageHandler();
        var client = AiTestHost.Direct(AiProvider.Gpt, handler);
        var claudeKey = AiTestHost.Handle(AiProvider.Claude);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => client.CompleteAsync(claudeKey, Request, AiTestHost.Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListModelsAsync(claudeKey, AiTestHost.Ct));

        Assert.Contains("belongs to Claude", ex.Message);
        Assert.DoesNotContain(AiTestHost.Key, ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task BaseUrlComesFromConfigurationWithoutDoublingTheVersionPath()
    {
        var handler = new FakeHttpMessageHandler().When(_ => true, _ => AiTestHost.Json("gpt/models.json"));
        var client = AiTestHost.Direct(AiProvider.Gpt, handler, new AiOptions { Gpt = new AiEndpointOptions { BaseUrl = "https://eu.api.openai.com/gateway/v1/" } });

        await client.ListModelsAsync(Handle, AiTestHost.Ct);

        Assert.Equal("https://eu.api.openai.com/gateway/v1/models", Assert.Single(handler.Requests).Uri.ToString());
    }
}
