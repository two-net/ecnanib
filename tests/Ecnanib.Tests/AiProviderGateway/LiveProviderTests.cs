using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.AiProviderGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.AiProviderGateway;

/// <summary>
/// The adapters against the real providers. Every other test of the gateway answers in a
/// provider's place, with what its documentation says it sends. These ask the provider itself,
/// so they show that it takes the requests and answers as the adapters read it.
/// </summary>
/// <remarks>
/// <para>
/// They are skipped unless whoever runs them gives a key and a model for a provider, in the
/// environment of the run: <c>ECNANIB_LIVE_CLAUDE_KEY</c> and <c>ECNANIB_LIVE_CLAUDE_MODEL</c>,
/// and the same with <c>GEMINI</c>, <c>GPT</c>, and <c>GROK</c>. So the suite never reaches a
/// provider by itself, and no key is stored. A run costs what three small calls to a model cost,
/// on the key that was given. See docs/ai-providers.md.
/// </para>
/// <para>
/// The variables are read here, by the tests. The application reads none.
/// </para>
/// </remarks>
public class LiveProviderTests
{
    private const string Prompt =
        "Bitcoin closed higher on each of the last five days, on rising volume. " +
        "Say whether the bias is bullish, bearish, or neutral, and how confident you are, from 0 to 1.";

    public static TheoryData<string> Providers => [.. ModelCalls.Providers];

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_provider_answers_a_call_with_a_result_of_the_schema_and_its_usage(string code)
    {
        var (key, model) = GivenFor(code);
        using var application = OnTheNetwork();
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var result = await gateway.CompleteAsync(
            new AiProviderCode(code), new AiCallRequest(model, Prompt, ModelCalls.Schema), key, TestContext.Current.CancellationToken);

        Assert.Equal(AiCallOutcome.Succeeded, result.Outcome);

        using var answer = JsonDocument.Parse(result.ResultJson!);
        Assert.Contains(answer.RootElement.GetProperty("bias").GetString(), new[] { "bullish", "bearish", "neutral" });
        Assert.Equal(JsonValueKind.Number, answer.RootElement.GetProperty("confidence").ValueKind);
        Assert.Equal(2, answer.RootElement.EnumerateObject().Count());

        Assert.NotNull(result.Usage);
        Assert.True(result.Usage.Value.InputTokens > 0, "The provider counted no input tokens.");
        Assert.True(result.Usage.Value.OutputTokens > 0, "The provider counted no output tokens.");
        Assert.InRange(result.Latency, TimeSpan.FromMilliseconds(1), TimeSpan.FromMinutes(1));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_provider_answers_a_wrong_key_as_the_adapter_expects(string code)
    {
        var (_, model) = GivenFor(code);
        using var application = OnTheNetwork();
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var result = await gateway.CompleteAsync(
            new AiProviderCode(code),
            new AiCallRequest(model, Prompt, ModelCalls.Schema),
            "not-a-key-0123456789-abcdefghij-klmnop",
            TestContext.Current.CancellationToken);

        Assert.Equal(AiCallOutcome.AuthenticationFailed, result.Outcome);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_provider_answers_a_model_it_does_not_have_as_the_adapter_expects(string code)
    {
        var (key, _) = GivenFor(code);
        using var application = OnTheNetwork();
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var result = await gateway.CompleteAsync(
            new AiProviderCode(code),
            new AiCallRequest("no-such-model-of-ecnanib-0000", Prompt, ModelCalls.Schema),
            key,
            TestContext.Current.CancellationToken);

        Assert.Equal(AiCallOutcome.ModelNotFound, result.Outcome);
    }

    /// <summary>The key and the model that the run was given for a provider. Without both, the test is skipped.</summary>
    private static (string Key, string Model) GivenFor(string code)
    {
        var name = $"ECNANIB_LIVE_{code.ToUpperInvariant()}";
        var key = Environment.GetEnvironmentVariable($"{name}_KEY");
        var model = Environment.GetEnvironmentVariable($"{name}_MODEL");

        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(model),
            $"Set {name}_KEY and {name}_MODEL to call {code} for real. See docs/ai-providers.md.");

        return (key!.Trim(), model!.Trim());
    }

    /// <summary>
    /// The application with the network behind the client of the gateway, and with the providers
    /// as the tracked configuration file has them: their real addresses, and its time limit.
    /// </summary>
    private static LoggedApplication OnTheNetwork()
    {
        // The test project copies the tracked file into its output under this name.
        var tracked = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.tracked.json")))!;

        return new LoggedApplication(
            LoggedApplication.Settings().With("AiProviders", tracked["AiProviders"]!.DeepClone()),
            host => host.ConfigureServices(services => services
                .AddHttpClient(AiProviderGatewayModule.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler())));
    }
}
