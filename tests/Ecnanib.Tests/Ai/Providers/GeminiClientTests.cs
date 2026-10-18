using System.Net;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Tests.Support;

namespace Ecnanib.Tests.Ai.Providers;

public class GeminiClientTests
{
    private static readonly AiKeyHandle Handle = AiTestHost.Handle(AiProvider.Gemini);

    [Fact]
    public async Task CompleteUsesTheHeaderNotTheQueryAndSkipsThoughts()
    {
        var handler = new FakeHttpMessageHandler().When(r => r.Uri.AbsolutePath.EndsWith(":generateContent"), _ => AiTestHost.Json("gemini/generate-stop.json"));
        await using var services = AiTestHost.BuildServices(handler);

        var completion = await services.Client(AiProvider.Gemini).CompleteAsync(Handle, new AiRequest("model-flash", "Be terse.", "What is 2+2?", AiTestHost.Schema()), AiTestHost.Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/model-flash:generateContent", request.Uri.ToString());
        Assert.Equal(string.Empty, request.Uri.Query);
        Assert.Equal(AiTestHost.Key, request.Header("x-goog-api-key"));
        Assert.Null(request.Header("Authorization"));
        var json = request.Json;
        Assert.Equal("Be terse.", json.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        var content = Assert.Single(json.GetProperty("contents").EnumerateArray());
        Assert.Equal("user", content.GetProperty("role").GetString());
        Assert.Equal("What is 2+2?", content.GetProperty("parts")[0].GetProperty("text").GetString());
        var config = json.GetProperty("generationConfig");
        Assert.Equal(8192, config.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal("application/json", config.GetProperty("responseMimeType").GetString());
        Assert.Equal("answer", config.GetProperty("responseJsonSchema").GetProperty("required")[0].GetString());
        Assert.False(config.TryGetProperty("temperature", out _));
        Assert.False(config.TryGetProperty("thinkingConfig", out _));

        Assert.Equal("model-flash-001", completion.Model);
        Assert.Equal("{\"answer\":\"4\"}", completion.Text);
        Assert.True(completion.IsStructured);
        Assert.Equal(AiFinishReason.Stop, completion.FinishReason);
        Assert.Equal("STOP", completion.RawFinishReason);
        Assert.Equal(new AiTokenUsage(15, 27, 3, 20), completion.Usage);
        Assert.Equal("abc123", completion.ResponseId);
    }

    [Fact]
    public async Task WithoutSchemaOrSystemNothingExtraIsSent()
    {
        var handler = new FakeHttpMessageHandler().When(_ => true, _ => AiTestHost.Json("gemini/generate-stop.json"));
        await using var services = AiTestHost.BuildServices(handler);

        await services.Client(AiProvider.Gemini).CompleteAsync(Handle, new AiRequest("model-flash", " ", "Hi"), AiTestHost.Ct);

        var json = Assert.Single(handler.Requests).Json;
        Assert.False(json.TryGetProperty("systemInstruction", out _));
        var config = json.GetProperty("generationConfig");
        Assert.False(config.TryGetProperty("responseMimeType", out _));
        Assert.False(config.TryGetProperty("responseJsonSchema", out _));
    }

    [Theory]
    [InlineData("gemini/generate-max-tokens.json", AiFinishReason.MaxTokens, "MAX_TOKENS", "{\"answer\":\"4", 3)]
    [InlineData("gemini/generate-safety.json", AiFinishReason.ContentFiltered, "SAFETY", "", 0)]
    [InlineData("gemini/generate-prompt-blocked.json", AiFinishReason.ContentFiltered, "blocked:PROHIBITED_CONTENT", "", 0)]
    public async Task TruncationAndSafetyBlocksAreResultsNotErrors(string fixture, AiFinishReason finish, string raw, string text, int outputTokens)
    {
        var handler = new FakeHttpMessageHandler().When(_ => true, _ => AiTestHost.Json(fixture));
        await using var services = AiTestHost.BuildServices(handler);

        var completion = await services.Client(AiProvider.Gemini).CompleteAsync(Handle, new AiRequest("model-flash", "s", "u", AiTestHost.Schema()), AiTestHost.Ct);

        Assert.Equal(finish, completion.FinishReason);
        Assert.Equal(raw, completion.RawFinishReason);
        Assert.Equal(text, completion.Text);
        Assert.False(completion.IsStructured);
        Assert.Equal(outputTokens, completion.Usage.OutputTokens);
        Assert.True(completion.Usage.InputTokens > 0);
    }

    [Fact]
    public async Task NoCandidatesAndNoBlockReasonIsInvalid()
    {
        var handler = new FakeHttpMessageHandler().When(_ => true, _ => AiTestHost.Json("gemini/generate-no-candidates.json"));
        await using var services = AiTestHost.BuildServices(handler);

        var ex = await Assert.ThrowsAsync<AiRequestException>(() => services.Client(AiProvider.Gemini).CompleteAsync(Handle, new AiRequest("model-flash", "s", "u"), AiTestHost.Ct));

        Assert.Equal(AiErrorKind.InvalidResponse, ex.Kind);
        Assert.Equal("r4", ex.RequestId);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListModelsFollowsPageTokenStripsPrefixAndFlagsGeneration()
    {
        var handler = new FakeHttpMessageHandler()
            .When(r => r.Uri.Query.Contains("pageToken=page2"), _ => AiTestHost.Json("gemini/models-page2.json"))
            .When(r => r.Uri.AbsolutePath.EndsWith("/models"), _ => AiTestHost.Json("gemini/models-page1.json"));
        await using var services = AiTestHost.BuildServices(handler);

        var models = await services.Client(AiProvider.Gemini).ListModelsAsync(Handle, AiTestHost.Ct);

        Assert.Equal(["model-flash", "embedding-x", "model-pro"], models.Select(m => m.Id));
        Assert.Equal("Model Flash", models[0].DisplayName);
        Assert.Equal(1048576, models[0].InputTokenLimit);
        Assert.Equal(65536, models[0].OutputTokenLimit);
        Assert.True(models[0].SupportsTextGeneration);
        Assert.False(models[1].SupportsTextGeneration);
        Assert.Null(models[0].CreatedAt);
        Assert.Equal("?pageSize=1000", handler.Requests[0].Uri.Query);
        Assert.Equal("?pageSize=1000&pageToken=page2", handler.Requests[1].Uri.Query);
        Assert.All(handler.Requests, r => Assert.DoesNotContain("key=", r.Uri.ToString()));
    }

    [Fact]
    public async Task InvalidKeyArrivesAs400ButIsAnAuthFailure()
    {
        var handler = new FakeHttpMessageHandler().When(_ => true, _ => AiTestHost.Json("gemini/error-400-api-key-invalid.json", HttpStatusCode.BadRequest));
        await using var services = AiTestHost.BuildServices(handler);

        var ex = await Assert.ThrowsAsync<AiAuthException>(() => services.Client(AiProvider.Gemini).ListModelsAsync(Handle, AiTestHost.Ct));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("INVALID_ARGUMENT", ex.ProviderCode);
        Assert.Contains(AiTestHost.Masked, ex.Message);
        Assert.DoesNotContain(AiTestHost.Key, ex.Message);
        Assert.Single(handler.Requests);
    }
}
