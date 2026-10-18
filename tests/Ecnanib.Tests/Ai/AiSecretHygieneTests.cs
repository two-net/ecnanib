using System.Net;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Ai.Configuration;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Ai;

/// <summary>
/// FR-KEY-04/05, FR-LOG-05: the key value appears in exactly one request header and nowhere else, even when the
/// provider echoes it back, and never in logs captured at Trace through the real container (which includes the
/// HttpClientFactory logging handlers).
/// </summary>
public class AiSecretHygieneTests
{
    private const string Key = AiTestHost.Key;

    [Theory]
    [InlineData(AiProvider.Claude, "claude/error-401-authentication.json", 401, "x-api-key", Key)]
    [InlineData(AiProvider.Gpt, "gpt/error-401-invalid-api-key.json", 401, "Authorization", "Bearer " + Key)]
    [InlineData(AiProvider.Grok, "grok/error-401-flat.json", 401, "Authorization", "Bearer " + Key)]
    [InlineData(AiProvider.Grok, "grok/error-400-invalid-key-flat.json", 400, "Authorization", "Bearer " + Key)]
    [InlineData(AiProvider.Gemini, "gemini/error-400-api-key-invalid.json", 400, "x-goog-api-key", Key)]
    public async Task KeyNeverLeaksIntoLogsExceptionsUrisOrBodies(AiProvider provider, string fixture, int status, string authHeader, string authValue)
    {
        var logs = new TestLoggerProvider();
        var handler = new FakeHttpMessageHandler().When(_ => true, _ => AiTestHost.Json(fixture, (HttpStatusCode)status));
        await using var services = AiTestHost.BuildServices(handler, logs);
        var client = services.Client(provider);
        var handle = AiTestHost.Handle(provider);

        var listEx = await Assert.ThrowsAsync<AiAuthException>(() => client.ListModelsAsync(handle, AiTestHost.Ct));
        var completeEx = await Assert.ThrowsAsync<AiAuthException>(() => client.CompleteAsync(handle, new AiRequest("model-1", "s", "u", AiTestHost.Schema()), AiTestHost.Ct));

        foreach (var text in new[] { listEx.ToString(), completeEx.ToString(), logs.AllText })
        {
            Assert.DoesNotContain(Key, text);
            Assert.DoesNotContain(AiTestHost.ProviderMaskedKey, text);
            Assert.Contains(AiTestHost.Masked, text);
        }

        Assert.Equal(2, handler.Requests.Count);
        foreach (var request in handler.Requests)
        {
            Assert.Equal(authValue, request.Header(authHeader));
            foreach (var header in request.Headers.Where(h => !string.Equals(h.Key, authHeader, StringComparison.OrdinalIgnoreCase)))
                Assert.DoesNotContain(Key, string.Join(",", header.Value));
            Assert.DoesNotContain(Key, request.Uri.ToString());
            Assert.DoesNotContain(Key, request.Body ?? string.Empty);
        }
    }

    [Fact]
    public async Task TransportAndGatewayFailuresLogWithoutTheKey()
    {
        var logs = new TestLoggerProvider();
        var handler = new FakeHttpMessageHandler()
            .Enqueue(new HttpRequestException("connection reset by peer"))
            .EnqueueHang()
            .Enqueue(FakeHttpMessageHandler.Text(HttpStatusCode.InternalServerError, "<html>oops</html>"));
        var client = AiTestHost.Direct(
            AiProvider.Claude, handler,
            new AiOptions { Retry = new AiRetryOptions { MaxAttempts = 3, BaseDelayMs = 1, MaxDelayMs = 1 } },
            logs: logs, timeout: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => client.CompleteAsync(AiTestHost.Handle(AiProvider.Claude), new AiRequest("model-1", "s", "u"), AiTestHost.Ct));

        Assert.Equal(AiErrorKind.ServerError, ex.Kind);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, logs.Entries.Count(e => e.Message.Contains("retrying")));
        Assert.DoesNotContain(Key, logs.AllText);
        Assert.DoesNotContain(Key, ex.ToString());
        Assert.Contains(AiTestHost.Masked, logs.AllText);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Timeout"));
    }
}
