using System.Net;
using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Ai;

/// <summary>EXT-AI-04: every provider's failures come out the same, for metrics and fallback.</summary>
public sealed class AiFailureTests(PostgresFixture postgres)
{
    private const string Complete = "/v1/complete";

    /// <summary>One application for every status, with a key per status to count its attempts. MaxRetries is 2.</summary>
    [Fact]
    public async Task Each_status_is_normalized_and_retried_only_when_another_attempt_could_help()
    {
        await using var ai = new AiHarness(postgres);
        ai.Provider.On(HttpMethod.Post, Complete, request => FakeAiProvider.Json("{}", (HttpStatusCode)int.Parse(request.Key!["status-".Length..])));
        (int Status, AiFailure Failure, int Attempts)[] cases =
        [
            (400, AiFailure.InvalidRequest, 1),
            (401, AiFailure.Authentication, 1),
            (403, AiFailure.Authentication, 1),
            (404, AiFailure.InvalidRequest, 1),
            (408, AiFailure.Timeout, 3),
            (409, AiFailure.Other, 1),
            (413, AiFailure.InvalidRequest, 1),
            (422, AiFailure.InvalidRequest, 1),
            (429, AiFailure.RateLimit, 3),
            (500, AiFailure.Other, 3),
            (502, AiFailure.Other, 3),
            (503, AiFailure.Overloaded, 3),
            (504, AiFailure.Timeout, 3),
            (529, AiFailure.Overloaded, 3),

            // The fake stands in for the handler, so this is a redirect that was never followed.
            (302, AiFailure.Other, 1),
        ];

        foreach (var (status, failure, attempts) in cases)
        {
            var key = $"status-{status}";

            var exception = await Assert.ThrowsAsync<AiException>(() => ai.CompleteAsync(AiHarness.Key(apiKey: key)));

            Assert.Equal(((int?)status, failure, attempts, attempts), (exception.StatusCode, exception.Failure, exception.Attempts, ai.Provider.WithKey(key).Count));
            Assert.Equal("Test", exception.Provider);
            Assert.Contains($"HTTP {status}", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_adapter_can_say_what_a_status_alone_would_get_wrong()
    {
        await using var ai = new AiHarness(postgres);
        ai.Provider.On(HttpMethod.Post, Complete, _ => FakeAiProvider.Error(HttpStatusCode.BadRequest, "busy", "Try again soon."));

        var exception = await Assert.ThrowsAsync<AiException>(() => ai.CompleteAsync());

        Assert.Equal((AiFailure.Overloaded, (string?)"busy", 3), (exception.Failure, exception.ErrorCode, exception.Attempts));
    }

    /// <summary>A provider's message can quote the prompt, and whoever catches the exception may log it.</summary>
    [Fact]
    public async Task The_providers_message_is_kept_for_the_trace_but_out_of_the_exceptions_message()
    {
        var quoted = $"Your prompt Z{Guid.NewGuid():N} is too long.";
        await using var ai = new AiHarness(postgres);
        ai.Provider.On(HttpMethod.Post, Complete, _ => FakeAiProvider.Error(HttpStatusCode.BadRequest, "invalid_request_error", quoted));

        var exception = await Assert.ThrowsAsync<AiException>(() => ai.CompleteAsync());

        Assert.Equal((AiFailure.InvalidRequest, (string?)"invalid_request_error", (string?)quoted), (exception.Failure, exception.ErrorCode, exception.ProviderMessage));
        Assert.DoesNotContain(quoted, exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("(invalid_request_error)", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_error_code_that_isnt_a_plain_token_is_dropped()
    {
        await using var ai = new AiHarness(postgres);
        ai.Provider.On(HttpMethod.Post, Complete, _ => FakeAiProvider.Error(HttpStatusCode.Unauthorized, "bad key Zsk123", "No."));

        var exception = await Assert.ThrowsAsync<AiException>(() => ai.CompleteAsync());

        Assert.Equal((AiFailure.Authentication, (string?)null), (exception.Failure, exception.ErrorCode));
        Assert.DoesNotContain("Zsk123", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_is_retried_and_its_tokens_still_count()
    {
        var price = new AiModelPrice(3m, 15m);
        await using var ai = new AiHarness(postgres);
        ai.Provider.On(HttpMethod.Post, Complete, _ => FakeAiProvider.Completion("I can't help with that.", 10, 2, stop: "refusal"));

        var exception = await Assert.ThrowsAsync<AiException>(() => ai.CompleteAsync(request: AiHarness.Request(price: price)));

        Assert.Equal(
            (AiFailure.Refusal, 3, new AiUsage(30, 6), (decimal?)price.CostOf(new AiUsage(30, 6))),
            (exception.Failure, exception.Attempts, exception.Usage, exception.Cost));
    }

    /// <summary>A format this version can't read won't change on a retry, and the body is the model's answer, which stays out of the message.</summary>
    [Fact]
    public async Task An_answer_that_cant_be_read_is_other_and_not_retried()
    {
        var answer = $"Z{Guid.NewGuid():N}";
        await using var ai = new AiHarness(postgres);
        ai.Provider.On(HttpMethod.Post, Complete, _ => FakeAiProvider.Json($$"""{"text":"{{answer}}"}"""));

        var exception = await Assert.ThrowsAsync<AiException>(() => ai.CompleteAsync());

        Assert.Equal((AiFailure.Other, 1, (int?)200), (exception.Failure, exception.Attempts, exception.StatusCode));
        Assert.Contains("can't read", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(answer, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_answer_within_the_timeout_is_a_timeout()
    {
        await using var ai = new AiHarness(postgres, new Dictionary<string, string?>
        {
            ["AiProviders:Test:TimeoutSeconds"] = "1",
            ["AiProviders:Test:MaxRetries"] = "1",
        });
        ai.Provider.On(HttpMethod.Post, Complete, async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return FakeAiProvider.Completion();
        });

        var exception = await Assert.ThrowsAsync<AiException>(() => ai.CompleteAsync());

        Assert.Equal((AiFailure.Timeout, 2, (int?)null), (exception.Failure, exception.Attempts, exception.StatusCode));
    }

    [Fact]
    public async Task A_broken_connection_is_other_and_retried()
    {
        await using var ai = new AiHarness(postgres);
        ai.Provider.On(HttpMethod.Post, Complete, (Func<RecordedAiRequest, HttpResponseMessage>)(_ => throw new HttpRequestException("Connection reset (test.ai.test:443)")));

        var exception = await Assert.ThrowsAsync<AiException>(() => ai.CompleteAsync());

        Assert.Equal((AiFailure.Other, 3, (int?)null), (exception.Failure, exception.Attempts, exception.StatusCode));
        Assert.IsType<HttpRequestException>(exception.InnerException);
    }

    [Fact]
    public async Task An_unknown_provider_is_a_mistake_in_the_caller()
    {
        await using var ai = new AiHarness(postgres);

        await Assert.ThrowsAsync<ArgumentException>(() => ai.CompleteAsync(AiHarness.Key("Nobody")));
        await Assert.ThrowsAsync<ArgumentException>(() => ai.CompleteAsync(AiHarness.Key("test")));
        Assert.Empty(ai.Provider.Requests);
    }
}
