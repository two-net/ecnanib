using System.Text.Json;
using Ecnanib.Ai.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Ai.Live;

/// <summary>
/// Keyed live checks (AI-01..04) shared by the four providers. Each run costs roughly 150 output tokens per provider.
/// </summary>
public abstract class AiProviderLiveTestsBase
{
    private const string NoSuchModel = "ecnanib-no-such-model";
    private readonly AiProvider _provider;

    protected AiProviderLiveTestsBase(AiProvider provider)
    {
        _provider = provider;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListModelsIsNonEmptyAndContainsTheConfiguredModel()
    {
        var (handle, model) = AiLiveTestKeys.Require(_provider);
        await using var services = AiLiveTestKeys.BuildServices();

        var models = await Client(services).ListModelsAsync(handle, Ct);

        Assert.NotEmpty(models);
        Assert.All(models, m => Assert.False(string.IsNullOrWhiteSpace(m.Id)));
        Assert.All(models, m => Assert.False(m.Id.StartsWith("models/", StringComparison.Ordinal)));
        Assert.Contains(models, m => m.Id == model);
    }

    [Fact]
    public async Task PlainCompletionReportsUsage()
    {
        var (handle, model) = AiLiveTestKeys.Require(_provider);
        await using var services = AiLiveTestKeys.BuildServices();

        var completion = await Client(services).CompleteAsync(handle, new AiRequest(model, "Answer with the single word OK and nothing else.", "Reply with exactly: OK", MaxOutputTokens: 64, Purpose: "live-test"), Ct);

        Assert.Equal(AiFinishReason.Stop, completion.FinishReason);
        Assert.Contains("OK", completion.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(completion.Usage.InputTokens > 0);
        Assert.True(completion.Usage.OutputTokens > 0);
        Assert.True(completion.Latency > TimeSpan.Zero);
        Assert.False(string.IsNullOrWhiteSpace(completion.Model));
        Assert.False(string.IsNullOrWhiteSpace(completion.ResponseId));
    }

    [Fact]
    public async Task StructuredCompletionMatchesATinySchema()
    {
        var (handle, model) = AiLiveTestKeys.Require(_provider);
        await using var services = AiLiveTestKeys.BuildServices();

        var completion = await Client(services).CompleteAsync(handle, new AiRequest(model, "You answer arithmetic questions.", "What is 2+2? Put the answer in the `answer` field.", AiTestHost.Schema(), MaxOutputTokens: 256, Purpose: "live-test"), Ct);

        Assert.Equal(AiFinishReason.Stop, completion.FinishReason);
        Assert.True(completion.IsStructured);
        using var document = JsonDocument.Parse(completion.Text);
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("answer").ValueKind);
        Assert.Contains("4", document.RootElement.GetProperty("answer").GetString());
    }

    [Fact]
    public async Task TruncationIsReportedAsMaxTokens()
    {
        var (handle, model) = AiLiveTestKeys.Require(_provider);
        await using var services = AiLiveTestKeys.BuildServices();
        var cap = _provider is AiProvider.Gpt or AiProvider.Grok ? 16 : 1;

        var completion = await Client(services).CompleteAsync(handle, new AiRequest(model, "You are verbose.", "Write 300 words about the sea.", MaxOutputTokens: cap, Purpose: "live-test"), Ct);

        Assert.Equal(AiFinishReason.MaxTokens, completion.FinishReason);
        Assert.True(completion.Usage.InputTokens > 0);
    }

    [Fact]
    public async Task InvalidKeyIsUnauthorized()
    {
        AiLiveTestKeys.Require(_provider);
        await using var services = AiLiveTestKeys.BuildServices();
        var bogus = new AiKeyHandle("bogus", _provider, new AiCredentials("invalid-key-0000"));

        var ex = await Assert.ThrowsAsync<AiAuthException>(() => Client(services).ListModelsAsync(bogus, Ct));

        Assert.Equal(AiErrorKind.Unauthorized, ex.Kind);
        Assert.DoesNotContain("invalid-key-0000", ex.Message);
    }

    [Fact]
    public async Task UnknownModelIsRejectedByName()
    {
        var (handle, _) = AiLiveTestKeys.Require(_provider);
        await using var services = AiLiveTestKeys.BuildServices();

        var ex = await Assert.ThrowsAsync<AiRequestException>(() => Client(services).CompleteAsync(handle, new AiRequest(NoSuchModel, "s", "Hi", MaxOutputTokens: 16), Ct));

        Assert.Equal(AiErrorKind.UnknownModel, ex.Kind);
        Assert.Equal(NoSuchModel, ex.Model);
        Assert.Contains(NoSuchModel, ex.Message);
    }

    private IAiProviderClient Client(ServiceProvider services) => services.GetRequiredService<IAiClientFactory>().GetClient(_provider);
}

[Trait("Category", "Live")]
public sealed class ClaudeLiveTests() : AiProviderLiveTestsBase(AiProvider.Claude);

[Trait("Category", "Live")]
public sealed class GeminiLiveTests() : AiProviderLiveTestsBase(AiProvider.Gemini);

[Trait("Category", "Live")]
public sealed class GptLiveTests() : AiProviderLiveTestsBase(AiProvider.Gpt);

[Trait("Category", "Live")]
public sealed class GrokLiveTests() : AiProviderLiveTestsBase(AiProvider.Grok);
