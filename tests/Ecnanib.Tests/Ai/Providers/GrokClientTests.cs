using System.Net;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Tests.Support;

namespace Ecnanib.Tests.Ai.Providers;

public class GrokClientTests
{
    private static readonly AiKeyHandle Handle = AiTestHost.Handle(AiProvider.Grok);

    [Fact]
    public async Task UsesTheXaiEndpointAndMapsReasoningUsage()
    {
        var handler = new FakeHttpMessageHandler().When(r => r.Uri.AbsolutePath.EndsWith("/responses"), _ => AiTestHost.Json("grok/responses-completed.json"));
        await using var services = AiTestHost.BuildServices(handler);

        var completion = await services.Client(AiProvider.Grok).CompleteAsync(Handle, new AiRequest("model-x", "Be terse.", "Say OK"), AiTestHost.Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.x.ai/v1/responses", request.Uri.ToString());
        Assert.Equal("Bearer " + AiTestHost.Key, request.Header("Authorization"));
        Assert.False(request.Json.TryGetProperty("text", out _));
        Assert.Equal(AiProvider.Grok, completion.Provider);
        Assert.Equal("OK", completion.Text);
        Assert.False(completion.IsStructured);
        Assert.Equal(AiFinishReason.Stop, completion.FinishReason);
        Assert.Equal(new AiTokenUsage(12, 40, 0, 38), completion.Usage);
        Assert.Equal("resp_x1", completion.ResponseId);
    }

    [Fact]
    public async Task ListsLanguageModelsWithTextCapability()
    {
        var handler = new FakeHttpMessageHandler().When(r => r.Uri.AbsolutePath.EndsWith("/language-models"), _ => AiTestHost.Json("grok/language-models.json"));
        await using var services = AiTestHost.BuildServices(handler);

        var models = await services.Client(AiProvider.Grok).ListModelsAsync(Handle, AiTestHost.Ct);

        Assert.Equal("https://api.x.ai/v1/language-models", Assert.Single(handler.Requests).Uri.ToString());
        Assert.Equal(["model-x", "model-image"], models.Select(m => m.Id));
        Assert.True(models[0].SupportsTextGeneration);
        Assert.False(models[1].SupportsTextGeneration);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1750000000), models[0].CreatedAt);
        Assert.Equal("model-x-latest", models[0].Capabilities.GetProperty("aliases")[0].GetString());
    }

    [Fact]
    public async Task FlatErrorBodiesAreTranslatedAndScrubbed()
    {
        var handler = new FakeHttpMessageHandler()
            .When(r => r.Uri.AbsolutePath.EndsWith("/language-models"), _ => AiTestHost.Json("grok/error-400-invalid-key-flat.json", HttpStatusCode.BadRequest))
            .When(r => r.Uri.AbsolutePath.EndsWith("/responses"), _ => AiTestHost.Json("grok/error-404-model-flat.json", HttpStatusCode.NotFound));
        await using var services = AiTestHost.BuildServices(handler);
        var client = services.Client(AiProvider.Grok);

        var auth = await Assert.ThrowsAsync<AiAuthException>(() => client.ListModelsAsync(Handle, AiTestHost.Ct));
        var model = await Assert.ThrowsAsync<AiRequestException>(() => client.CompleteAsync(Handle, new AiRequest("ecnanib-no-such-model", "s", "u"), AiTestHost.Ct));

        Assert.Equal(400, auth.StatusCode);
        Assert.Contains(AiTestHost.Masked, auth.Message);
        Assert.DoesNotContain(AiTestHost.Key, auth.Message);
        Assert.DoesNotContain(AiTestHost.ProviderMaskedKey, auth.Message);
        Assert.Equal(AiErrorKind.UnknownModel, model.Kind);
        Assert.Equal("ecnanib-no-such-model", model.Model);
    }
}
