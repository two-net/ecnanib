using System.Net;
using System.Text.Json;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Tests.Support;

namespace Ecnanib.Tests.Ai.Providers;

public class ClaudeClientTests
{
    private static readonly AiKeyHandle Handle = AiTestHost.Handle(AiProvider.Claude);

    [Fact]
    public async Task CompleteSendsOnlyTheDocumentedFieldsAndMapsTheResponse()
    {
        var handler = new FakeHttpMessageHandler().When(r => r.Uri.AbsolutePath.EndsWith("/messages"), _ => AiTestHost.Json("claude/messages-end-turn.json", headers: ("request-id", "req_9")));
        await using var services = AiTestHost.BuildServices(handler, config: new() { ["Providers:MaxOutputTokens"] = "100" });
        var client = services.Client(AiProvider.Claude);

        var completion = await client.CompleteAsync(Handle, new AiRequest("model-alpha", "Be terse.", "What is 2+2?", AiTestHost.Schema(), MaxOutputTokens: 500, Purpose: "risk-manager"), AiTestHost.Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.anthropic.com/v1/messages", request.Uri.ToString());
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(AiTestHost.Key, request.Header("x-api-key"));
        Assert.Equal("2023-06-01", request.Header("anthropic-version"));
        Assert.Null(request.Header("Authorization"));
        var json = request.Json;
        Assert.Equal("model-alpha", json.GetProperty("model").GetString());
        Assert.Equal(100, json.GetProperty("max_tokens").GetInt32());
        Assert.Equal("Be terse.", json.GetProperty("system").GetString());
        var message = Assert.Single(json.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("What is 2+2?", message.GetProperty("content").GetString());
        var format = json.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("answer", format.GetProperty("schema").GetProperty("required")[0].GetString());
        Assert.Equal(["max_tokens", "messages", "model", "output_config", "system"], json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(AiTestHost.Key, request.Body);

        Assert.Equal(AiProvider.Claude, completion.Provider);
        Assert.Equal("model-alpha-20260201", completion.Model);
        Assert.Equal("{\"answer\":\"4\"}", completion.Text);
        Assert.True(completion.IsStructured);
        Assert.Equal(AiFinishReason.Stop, completion.FinishReason);
        Assert.Equal("end_turn", completion.RawFinishReason);
        Assert.Equal(new AiTokenUsage(20, 5, 6, null), completion.Usage);
        Assert.Equal("msg_01", completion.ResponseId);
    }

    [Fact]
    public async Task SystemPromptAndSchemaAreOmittedWhenAbsent()
    {
        var handler = new FakeHttpMessageHandler().When(_ => true, _ => AiTestHost.Json("claude/messages-end-turn.json"));
        await using var services = AiTestHost.BuildServices(handler);

        var completion = await services.Client(AiProvider.Claude).CompleteAsync(Handle, new AiRequest("model-alpha", "", "Hi"), AiTestHost.Ct);

        var json = Assert.Single(handler.Requests).Json;
        Assert.False(json.TryGetProperty("system", out _));
        Assert.False(json.TryGetProperty("output_config", out _));
        Assert.Equal(8192, json.GetProperty("max_tokens").GetInt32());
        Assert.False(completion.IsStructured);
    }

    [Theory]
    [InlineData("claude/messages-max-tokens.json", AiFinishReason.MaxTokens, "max_tokens", "{\"answer\":\"4")]
    [InlineData("claude/messages-refusal.json", AiFinishReason.Refusal, "refusal", "")]
    public async Task TruncationAndRefusalAreResultsNotErrors(string fixture, AiFinishReason finish, string raw, string text)
    {
        var handler = new FakeHttpMessageHandler().When(_ => true, _ => AiTestHost.Json(fixture));
        await using var services = AiTestHost.BuildServices(handler);

        var completion = await services.Client(AiProvider.Claude).CompleteAsync(Handle, new AiRequest("model-alpha", "s", "u", AiTestHost.Schema()), AiTestHost.Ct);

        Assert.Equal(finish, completion.FinishReason);
        Assert.Equal(raw, completion.RawFinishReason);
        Assert.Equal(text, completion.Text);
        Assert.False(completion.IsStructured);
        Assert.Equal(10, completion.Usage.InputTokens);
    }

    [Fact]
    public async Task ListModelsFollowsAfterIdAndDedupes()
    {
        var handler = new FakeHttpMessageHandler()
            .When(r => r.Uri.Query.Contains("after_id=model-beta"), _ => AiTestHost.Json("claude/models-page2.json"))
            .When(r => r.Uri.AbsolutePath.EndsWith("/models"), _ => AiTestHost.Json("claude/models-page1.json"));
        await using var services = AiTestHost.BuildServices(handler);

        var models = await services.Client(AiProvider.Claude).ListModelsAsync(Handle, AiTestHost.Ct);

        Assert.Equal(["model-alpha", "model-beta", "model-gamma"], models.Select(m => m.Id));
        var alpha = models[0];
        Assert.Equal("Model Alpha", alpha.DisplayName);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), alpha.CreatedAt);
        Assert.Equal(200000, alpha.InputTokenLimit);
        Assert.Equal(32000, alpha.OutputTokenLimit);
        Assert.True(alpha.SupportsTextGeneration);
        Assert.True(alpha.Capabilities.GetProperty("capabilities").GetProperty("structured_outputs").GetProperty("supported").GetBoolean());
        Assert.Null(models[2].InputTokenLimit);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("?limit=1000", handler.Requests[0].Uri.Query);
        Assert.Equal("?limit=1000&after_id=model-beta", handler.Requests[1].Uri.Query);
        Assert.All(handler.Requests, r => Assert.Equal(AiTestHost.Key, r.Header("x-api-key")));
    }

    [Fact]
    public async Task PaginationLoopIsAnInvalidResponse()
    {
        var handler = new FakeHttpMessageHandler().When(_ => true, _ => AiTestHost.Json("claude/models-page1.json"));
        await using var services = AiTestHost.BuildServices(handler);

        var ex = await Assert.ThrowsAsync<AiRequestException>(() => services.Client(AiProvider.Claude).ListModelsAsync(Handle, AiTestHost.Ct));

        Assert.Equal(AiErrorKind.InvalidResponse, ex.Kind);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task UnknownModelIsRejectedByName()
    {
        var handler = new FakeHttpMessageHandler().When(_ => true, _ => AiTestHost.Json("claude/error-404-model.json", HttpStatusCode.NotFound));
        await using var services = AiTestHost.BuildServices(handler);

        var ex = await Assert.ThrowsAsync<AiRequestException>(() => services.Client(AiProvider.Claude).CompleteAsync(Handle, new AiRequest("ecnanib-no-such-model", "s", "u"), AiTestHost.Ct));

        Assert.Equal(AiErrorKind.UnknownModel, ex.Kind);
        Assert.Equal("ecnanib-no-such-model", ex.Model);
        Assert.Contains("ecnanib-no-such-model", ex.Message);
        Assert.Single(handler.Requests);
    }
}
