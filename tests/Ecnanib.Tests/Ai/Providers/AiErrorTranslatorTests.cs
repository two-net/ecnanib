using System.Net;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Ai.Providers;
using Ecnanib.Tests.Ai.Fixtures;

namespace Ecnanib.Tests.Ai.Providers;

public class AiErrorTranslatorTests
{
    private const string Model = "ecnanib-no-such-model";
    private static readonly AiCredentials Credentials = new(AiTestHost.Key);
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AiProvider.Claude, "claude/error-401-authentication.json", 401, null, typeof(AiAuthException), AiErrorKind.Unauthorized, false, "authentication_error")]
    [InlineData(AiProvider.Claude, "claude/error-404-model.json", 404, Model, typeof(AiRequestException), AiErrorKind.UnknownModel, false, "not_found_error")]
    [InlineData(AiProvider.Claude, "claude/error-429-rate-limit.json", 429, Model, typeof(AiRateLimitedException), AiErrorKind.RateLimited, true, "rate_limit_error")]
    [InlineData(AiProvider.Claude, "claude/error-529-overloaded.json", 529, Model, typeof(AiUnavailableException), AiErrorKind.Overloaded, true, "overloaded_error")]
    [InlineData(AiProvider.Claude, "claude/error-400-credit-balance.json", 400, Model, typeof(AiRequestException), AiErrorKind.QuotaExceeded, false, "invalid_request_error")]
    [InlineData(AiProvider.Claude, "claude/error-400-prompt-too-long.json", 400, Model, typeof(AiRequestException), AiErrorKind.ContextTooLong, false, "invalid_request_error")]
    [InlineData(AiProvider.Claude, "claude/error-500-api-error.json", 500, Model, typeof(AiUnavailableException), AiErrorKind.ServerError, true, "api_error")]
    [InlineData(AiProvider.Gpt, "gpt/error-401-invalid-api-key.json", 401, null, typeof(AiAuthException), AiErrorKind.Unauthorized, false, "invalid_api_key")]
    [InlineData(AiProvider.Gpt, "gpt/error-404-model-not-found.json", 404, Model, typeof(AiRequestException), AiErrorKind.UnknownModel, false, "model_not_found")]
    [InlineData(AiProvider.Gpt, "gpt/error-429-rate-limit.json", 429, Model, typeof(AiRateLimitedException), AiErrorKind.RateLimited, true, "rate_limit_exceeded")]
    [InlineData(AiProvider.Gpt, "gpt/error-429-insufficient-quota.json", 429, Model, typeof(AiRequestException), AiErrorKind.QuotaExceeded, false, "insufficient_quota")]
    [InlineData(AiProvider.Gpt, "gpt/error-400-context-length.json", 400, Model, typeof(AiRequestException), AiErrorKind.ContextTooLong, false, "context_length_exceeded")]
    [InlineData(AiProvider.Gpt, "gpt/error-500.json", 500, Model, typeof(AiUnavailableException), AiErrorKind.ServerError, true, "server_error")]
    [InlineData(AiProvider.Grok, "grok/error-401-flat.json", 401, null, typeof(AiAuthException), AiErrorKind.Unauthorized, false, "The request is not authenticated")]
    [InlineData(AiProvider.Grok, "grok/error-400-invalid-key-flat.json", 400, null, typeof(AiAuthException), AiErrorKind.Unauthorized, false, "Client specified an invalid argument")]
    [InlineData(AiProvider.Grok, "grok/error-404-model-flat.json", 404, Model, typeof(AiRequestException), AiErrorKind.UnknownModel, false, "Some requested entity was not found")]
    [InlineData(AiProvider.Grok, "grok/error-429-nested.json", 429, Model, typeof(AiRateLimitedException), AiErrorKind.RateLimited, true, "rate_limit_exceeded")]
    [InlineData(AiProvider.Grok, "grok/error-403.json", 403, null, typeof(AiAuthException), AiErrorKind.Unauthorized, false, "The caller does not have permission")]
    [InlineData(AiProvider.Gemini, "gemini/error-400-api-key-invalid.json", 400, null, typeof(AiAuthException), AiErrorKind.Unauthorized, false, "INVALID_ARGUMENT")]
    [InlineData(AiProvider.Gemini, "gemini/error-403-permission-denied.json", 403, null, typeof(AiAuthException), AiErrorKind.Unauthorized, false, "PERMISSION_DENIED")]
    [InlineData(AiProvider.Gemini, "gemini/error-404-model.json", 404, Model, typeof(AiRequestException), AiErrorKind.UnknownModel, false, "NOT_FOUND")]
    [InlineData(AiProvider.Gemini, "gemini/error-429-resource-exhausted.json", 429, Model, typeof(AiRateLimitedException), AiErrorKind.RateLimited, true, "RESOURCE_EXHAUSTED")]
    [InlineData(AiProvider.Gemini, "gemini/error-429-daily-quota.json", 429, Model, typeof(AiRequestException), AiErrorKind.QuotaExceeded, false, "RESOURCE_EXHAUSTED")]
    [InlineData(AiProvider.Gemini, "gemini/error-503-unavailable.json", 503, Model, typeof(AiUnavailableException), AiErrorKind.Overloaded, true, "UNAVAILABLE")]
    [InlineData(AiProvider.Gemini, "gemini/error-500-internal.json", 500, Model, typeof(AiUnavailableException), AiErrorKind.ServerError, true, "INTERNAL")]
    public void ProviderErrorsBecomeTypedExceptions(AiProvider provider, string fixture, int status, string? model, Type expectedType, AiErrorKind kind, bool transient, string providerCode)
    {
        var ex = Translate(provider, fixture, status, model);

        Assert.IsType(expectedType, ex);
        Assert.Equal(provider, ex.Provider);
        Assert.Equal(kind, ex.Kind);
        Assert.Equal(transient, ex.IsTransient);
        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(providerCode, ex.ProviderCode);
        Assert.Equal(model, ex.Model);
        Assert.StartsWith($"{provider} {(model is null ? "list models" : "complete")} failed: ", ex.Message);
        Assert.DoesNotContain(AiTestHost.Key, ex.Message);
        Assert.DoesNotContain(AiTestHost.ProviderMaskedKey, ex.Message);
        if (kind == AiErrorKind.UnknownModel)
            Assert.Contains(Model, ex.Message);
    }

    [Fact]
    public void RetryAfterComesFromHeaderMessageOrRetryInfo()
    {
        var fromHeader = Assert.IsType<AiRateLimitedException>(Translate(AiProvider.Claude, "claude/error-429-rate-limit.json", 429, Model, ("Retry-After", "7")));
        Assert.Equal(TimeSpan.FromSeconds(7), fromHeader.RetryAfter);
        Assert.Equal(Now.AddSeconds(7), fromHeader.RetryAt);

        var fromMessage = Assert.IsType<AiRateLimitedException>(Translate(AiProvider.Gpt, "gpt/error-429-rate-limit.json", 429, Model));
        Assert.Equal(TimeSpan.FromSeconds(20), fromMessage.RetryAfter);

        var fromRetryInfo = Assert.IsType<AiRateLimitedException>(Translate(AiProvider.Gemini, "gemini/error-429-resource-exhausted.json", 429, Model));
        Assert.Equal(TimeSpan.FromSeconds(33), fromRetryInfo.RetryAfter);

        var none = Assert.IsType<AiRateLimitedException>(Translate(AiProvider.Grok, "grok/error-429-nested.json", 429, Model));
        Assert.Null(none.RetryAfter);
        Assert.Null(none.RetryAt);
    }

    [Fact]
    public void NonJsonAndEmptyBodiesAreClassifiedByStatus()
    {
        var html = Translate(AiProvider.Gpt, "shared/gateway-502.html", 502, Model);
        Assert.IsType<AiUnavailableException>(html);
        Assert.Equal(AiErrorKind.ServerError, html.Kind);
        Assert.Equal("Gpt complete failed: HTTP 502 Bad Gateway (HTTP 502)", html.Message);
        Assert.Null(html.ProviderCode);

        var empty = AiErrorTranslator.Translate(AiProvider.Claude, ClaudeErrorParser.Instance, "list models", null, HttpStatusCode.Unauthorized, "Unauthorized", null, string.Empty, Now);
        Assert.IsType<AiAuthException>(empty);
        Assert.Equal("Claude list models failed: HTTP 401 Unauthorized (HTTP 401)", empty.Message);
    }

    [Fact]
    public void MessageNamesProviderOperationDetailAndCode()
    {
        var ex = Translate(AiProvider.Claude, "claude/error-404-model.json", 404, Model);

        Assert.Equal("Claude complete failed: model: ecnanib-no-such-model (not_found_error)", ex.Message);
    }

    [Fact]
    public void UnknownModelMessageNamesTheModelWhenTheProviderDoesNot()
    {
        var ex = AiErrorTranslator.Translate(
            AiProvider.Gpt, OpenAiErrorParser.Instance, "complete", "m-1", HttpStatusCode.NotFound, "Not Found", null,
            """{"error":{"message":"Not found","type":"invalid_request_error","param":null,"code":null}}""", Now);

        Assert.Equal(AiErrorKind.UnknownModel, ex.Kind);
        Assert.Equal("Gpt complete failed: model 'm-1' was rejected: Not found (invalid_request_error)", ex.Message);
    }

    [Fact]
    public void RequestIdIsReadFromHeaders()
    {
        Assert.Equal("req_1", Translate(AiProvider.Claude, "claude/error-500-api-error.json", 500, Model, ("request-id", "req_1")).RequestId);
        Assert.Equal("req_2", Translate(AiProvider.Gpt, "gpt/error-500.json", 500, Model, ("x-request-id", "req_2")).RequestId);
        Assert.Null(Translate(AiProvider.Gpt, "gpt/error-500.json", 500, Model).RequestId);
    }

    [Fact]
    public void LongDetailsAreTruncated()
    {
        var body = "{\"error\":{\"message\":\"" + new string('x', 2000) + "\",\"type\":\"server_error\"}}";
        var ex = AiErrorTranslator.Translate(AiProvider.Gpt, OpenAiErrorParser.Instance, "complete", "m", HttpStatusCode.InternalServerError, null, null, body, Now);

        Assert.True(ex.Message.Length < 600, ex.Message.Length.ToString());
        Assert.EndsWith("… (server_error)", ex.Message);
    }

    [Theory]
    [InlineData(408, null, null, null, AiErrorKind.Timeout)]
    [InlineData(402, null, null, null, AiErrorKind.QuotaExceeded)]
    [InlineData(418, null, null, null, AiErrorKind.InvalidRequest)]
    [InlineData(404, "The resource was not found", null, null, AiErrorKind.InvalidRequest)]
    [InlineData(404, "The resource was not found", null, "m", AiErrorKind.UnknownModel)]
    [InlineData(404, null, null, "m", AiErrorKind.InvalidRequest)]
    [InlineData(404, "Not found", "text.format", "m", AiErrorKind.InvalidRequest)]
    [InlineData(400, "The model foo does not exist", null, null, AiErrorKind.UnknownModel)]
    [InlineData(400, "Unsupported model: foo", null, "m", AiErrorKind.UnknownModel)]
    [InlineData(400, "model foo is not available in this region", null, "m", AiErrorKind.UnknownModel)]
    [InlineData(400, "Invalid parameter: 'text.format' of type 'json_schema' is not supported with this model.", "text.format", "m", AiErrorKind.InvalidRequest)]
    [InlineData(400, "Invalid parameter: 'text.format' of type 'json_schema' is not supported with this model.", null, "m", AiErrorKind.InvalidRequest)]
    [InlineData(400, "Invalid value for model_settings. See the model documentation.", null, "m", AiErrorKind.InvalidRequest)]
    [InlineData(400, "This model's maximum context length is 8192 tokens", null, "m", AiErrorKind.ContextTooLong)]
    [InlineData(400, "Invalid JSON", null, "m", AiErrorKind.InvalidRequest)]
    [InlineData(504, null, null, "m", AiErrorKind.ServerError)]
    [InlineData(200, null, null, "m", AiErrorKind.Unknown)]
    public void SharedClassificationFollowsTheStatus(int status, string? message, string? param, string? model, AiErrorKind expected)
    {
        var error = message is null ? null : new ProviderError(null, message, param);

        Assert.Equal(expected, AiErrorTranslator.ClassifyByStatus((HttpStatusCode)status, error, model));
    }

    [Theory]
    [InlineData("Incorrect API key provided: sk-abc. You can find your key at the console.", true)]
    [InlineData("Invalid API key", true)]
    [InlineData("API key not valid. Please pass a valid API key.", true)]
    [InlineData("The api_key is missing.", true)]
    [InlineData("Invalid parameter: 'text.format'", false)]
    [InlineData("You can find your API key at https://example.test/keys", false)]
    [InlineData(null, false)]
    public void InvalidKeyTextIsRecognised(string? message, bool expected)
    {
        Assert.Equal(expected, AiErrorTranslator.MentionsInvalidApiKey(message));
    }

    [Fact]
    public void RetryHintsAreKeptOnOverloadAndServerErrors()
    {
        var overloaded = Assert.IsType<AiUnavailableException>(Translate(AiProvider.Claude, "claude/error-529-overloaded.json", 529, Model, ("Retry-After", "20")));
        Assert.Equal(AiErrorKind.Overloaded, overloaded.Kind);
        Assert.Equal(TimeSpan.FromSeconds(20), overloaded.RetryAfter);
        Assert.Equal(Now.AddSeconds(20), overloaded.RetryAt);

        var unavailable = Assert.IsType<AiUnavailableException>(Translate(AiProvider.Gemini, "gemini/error-503-unavailable.json", 503, Model, ("Retry-After", "3")));
        Assert.Equal(TimeSpan.FromSeconds(3), unavailable.RetryAfter);

        var noHint = Assert.IsType<AiUnavailableException>(Translate(AiProvider.Gpt, "gpt/error-500.json", 500, Model));
        Assert.Null(noHint.RetryAfter);

        var inBand = Assert.IsType<AiRateLimitedException>(AiErrorTranslator.TranslateInBand(AiProvider.Gpt, OpenAiErrorParser.Instance, "complete", "m", new ProviderError("rate_limit_exceeded", "Please try again in 20s."), null, Now));
        Assert.Equal(TimeSpan.FromSeconds(20), inBand.RetryAfter);
    }

    [Fact]
    public void InBandFailuresUseTheProviderCode()
    {
        var server = AiErrorTranslator.TranslateInBand(AiProvider.Gpt, OpenAiErrorParser.Instance, "complete", "m", new ProviderError("server_error", "boom"), "req", Now);
        Assert.IsType<AiUnavailableException>(server);
        Assert.True(server.IsTransient);
        Assert.Equal("req", server.RequestId);
        Assert.Null(server.StatusCode);

        var unknown = AiErrorTranslator.TranslateInBand(AiProvider.Grok, OpenAiErrorParser.Instance, "complete", "m", new ProviderError("weird", null), null, Now);
        Assert.IsType<AiRequestException>(unknown);
        Assert.Equal(AiErrorKind.InvalidRequest, unknown.Kind);
        Assert.Equal("Grok complete failed: the provider reported a failure (weird)", unknown.Message);
    }

    private static AiException Translate(AiProvider provider, string fixture, int status, string? model, params (string Name, string Value)[] headers)
    {
        var body = Credentials.Scrub(Fixture.Read(fixture, AiTestHost.Key, AiTestHost.ProviderMaskedKey));
        using var response = new HttpResponseMessage((HttpStatusCode)status);
        foreach (var (name, value) in headers)
            response.Headers.TryAddWithoutValidation(name, value);
        var parser = provider switch
        {
            AiProvider.Claude => (IAiErrorBodyParser)ClaudeErrorParser.Instance,
            AiProvider.Gemini => GeminiErrorParser.Instance,
            _ => OpenAiErrorParser.Instance,
        };
        return AiErrorTranslator.Translate(provider, parser, model is null ? "list models" : "complete", model, (HttpStatusCode)status, response.ReasonPhrase, response.Headers, body, Now);
    }
}
