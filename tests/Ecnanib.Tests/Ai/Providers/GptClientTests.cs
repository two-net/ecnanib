using System.Net;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Tests.Support;

namespace Ecnanib.Tests.Ai.Providers;

public class GptClientTests
{
    private static readonly AiKeyHandle Handle = AiTestHost.Handle(AiProvider.Gpt);

    [Fact]
    public async Task CompleteSendsAResponsesRequestAndMapsUsage()
    {
        var handler = new FakeHttpMessageHandler().When(r => r.Uri.AbsolutePath.EndsWith("/responses"), _ => AiTestHost.Json("gpt/responses-completed.json"));
        await using var services = AiTestHost.BuildServices(handler);

        var completion = await services.Client(AiProvider.Gpt).CompleteAsync(Handle, new AiRequest("model-one", "Be terse.", "What is 2+2?", AiTestHost.Schema()), AiTestHost.Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.openai.com/v1/responses", request.Uri.ToString());
        Assert.Equal("Bearer " + AiTestHost.Key, request.Header("Authorization"));
        Assert.Null(request.Header("x-api-key"));
        var json = request.Json;
        Assert.Equal("model-one", json.GetProperty("model").GetString());
        Assert.Equal("Be terse.", json.GetProperty("instructions").GetString());
        Assert.Equal("What is 2+2?", json.GetProperty("input").GetString());
        Assert.Equal(8192, json.GetProperty("max_output_tokens").GetInt32());
        Assert.False(json.GetProperty("store").GetBoolean());
        var format = json.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("ecnanib_output", format.GetProperty("name").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.Equal("answer", format.GetProperty("schema").GetProperty("required")[0].GetString());
        Assert.False(json.TryGetProperty("temperature", out _));
        Assert.False(json.TryGetProperty("reasoning", out _));

        Assert.Equal("model-one-2026-01-01", completion.Model);
        Assert.Equal("{\"answer\":\"4\"}", completion.Text);
        Assert.True(completion.IsStructured);
        Assert.Equal(AiFinishReason.Stop, completion.FinishReason);
        Assert.Equal("completed", completion.RawFinishReason);
        Assert.Equal(new AiTokenUsage(20, 30, 8, 12), completion.Usage);
        Assert.Equal("resp_01", completion.ResponseId);
    }

    [Theory]
    [InlineData("gpt/responses-incomplete-max-output-tokens.json", AiFinishReason.MaxTokens, "incomplete:max_output_tokens", "{\"answer\":\"4", 16)]
    [InlineData("gpt/responses-incomplete-content-filter.json", AiFinishReason.ContentFiltered, "incomplete:content_filter", "", 2)]
    [InlineData("gpt/responses-refusal.json", AiFinishReason.Refusal, "refusal", "I can't help with that.", 9)]
    public async Task IncompleteAndRefusedResponsesAreResultsNotErrors(string fixture, AiFinishReason finish, string raw, string text, int outputTokens)
    {
        var handler = new FakeHttpMessageHandler().When(_ => true, _ => AiTestHost.Json(fixture));
        await using var services = AiTestHost.BuildServices(handler);

        var completion = await services.Client(AiProvider.Gpt).CompleteAsync(Handle, new AiRequest("model-one", "s", "u", AiTestHost.Schema()), AiTestHost.Ct);

        Assert.Equal(finish, completion.FinishReason);
        Assert.Equal(raw, completion.RawFinishReason);
        Assert.Equal(text, completion.Text);
        Assert.False(completion.IsStructured);
        Assert.Equal(outputTokens, completion.Usage.OutputTokens);
    }

    [Fact]
    public async Task FailedResponseIsATransientServerError()
    {
        var handler = new FakeHttpMessageHandler().When(_ => true, _ => AiTestHost.Json("gpt/responses-failed.json"));
        await using var services = AiTestHost.BuildServices(handler, config: new() { ["Providers:Retry:MaxAttempts"] = "1" });

        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => services.Client(AiProvider.Gpt).CompleteAsync(Handle, new AiRequest("model-one", "s", "u"), AiTestHost.Ct));

        Assert.Equal(AiErrorKind.ServerError, ex.Kind);
        Assert.Equal("server_error", ex.ProviderCode);
        Assert.Equal("model-one", ex.Model);
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task InProgressResponseIsInvalidAndNotRetried()
    {
        var handler = new FakeHttpMessageHandler().When(_ => true, _ => AiTestHost.Json("gpt/responses-in-progress.json"));
        await using var services = AiTestHost.BuildServices(handler);

        var ex = await Assert.ThrowsAsync<AiRequestException>(() => services.Client(AiProvider.Gpt).CompleteAsync(Handle, new AiRequest("model-one", "s", "u"), AiTestHost.Ct));

        Assert.Equal(AiErrorKind.InvalidResponse, ex.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListModelsMapsIdAndCreationTimeAndDedupes()
    {
        var handler = new FakeHttpMessageHandler().When(r => r.Uri.AbsolutePath.EndsWith("/models"), _ => AiTestHost.Json("gpt/models.json"));
        await using var services = AiTestHost.BuildServices(handler);

        var models = await services.Client(AiProvider.Gpt).ListModelsAsync(Handle, AiTestHost.Ct);

        Assert.Equal("https://api.openai.com/v1/models", Assert.Single(handler.Requests).Uri.ToString());
        Assert.Equal(["model-one", "model-two"], models.Select(m => m.Id));
        Assert.Equal("model-one", models[0].DisplayName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), models[0].CreatedAt);
        Assert.Null(models[0].SupportsTextGeneration);
        Assert.Null(models[0].InputTokenLimit);
        Assert.Equal("system", models[0].Capabilities.GetProperty("owned_by").GetString());
    }

    [Fact]
    public async Task QuotaExhaustionIsNotRetried()
    {
        var handler = new FakeHttpMessageHandler().When(_ => true, _ => AiTestHost.Json("gpt/error-429-insufficient-quota.json", HttpStatusCode.TooManyRequests));
        await using var services = AiTestHost.BuildServices(handler);

        var ex = await Assert.ThrowsAsync<AiRequestException>(() => services.Client(AiProvider.Gpt).ListModelsAsync(Handle, AiTestHost.Ct));

        Assert.Equal(AiErrorKind.QuotaExceeded, ex.Kind);
        Assert.Single(handler.Requests);
    }
}
