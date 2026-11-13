using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ecnanib.AiProviderGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.AiProviderGateway;

/// <summary>
/// Every call to a model is made through the gateway: one adapter each for Claude, Gemini, GPT,
/// and Grok behind the same interface, which callers reach by the provider's code (NFR-ARC-3). A
/// call takes prompt text and a result schema, and returns the structured result, the token
/// usage, and the latency. The prompt is the same for every provider: only how the structured
/// result is asked for is a provider's own (FR-PRM-10). The key comes with each call, and is kept
/// nowhere (FR-KEY-9).
/// </summary>
public class AiCallTests
{
    private const string AnotherKey = "another-users-key-9876543210-zyxwvutsrq-Jm2T";

    public static TheoryData<string> Providers => [.. ModelCalls.Providers];

    /// <summary>An answer of each provider, with the usage that it stands for when counted the same way for all.</summary>
    public static TheoryData<string, string, int, int> Usages => new()
    {
        // Claude counts what it read from a cache, and what it wrote to one, apart from the rest of the input.
        { "claude", ModelCalls.Claude(ModelCalls.Result, input: 100, cacheWritten: 20, cacheRead: 30, output: 40), 150, 40 },

        // Gemini counts what the model reasoned apart from its answer.
        { "gemini", ModelCalls.Gemini(ModelCalls.Result, prompt: 100, candidates: 30, thoughts: 12), 100, 42 },

        // GPT counts what the model reasoned inside the output, and Grok beside it.
        { "gpt", ModelCalls.Responses(ModelCalls.Result, input: 100, output: 50, reasoning: 30), 100, 50 },
        { "grok", ModelCalls.Responses(ModelCalls.Result, input: 100, output: 9, reasoning: 41, total: 150), 100, 50 },

        // An answer without a total is taken by its output.
        { "grok", WithoutTotal(ModelCalls.Responses(ModelCalls.Result, input: 7, output: 3)), 7, 3 },
    };

    [Fact]
    public async Task Each_provider_is_called_at_its_address_of_the_file_with_the_key_in_its_own_header()
    {
        var providers = ModelCalls.ThatAnswer();
        using var application = providers.Behind(AiProviderGatewayModule.HttpClientName);
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        // The caller does the same for every provider, which it names by its code.
        foreach (var code in ModelCalls.Providers)
        {
            var result = await gateway.CompleteAsync(
                new AiProviderCode(code), ModelCalls.RequestTo(code), ModelCalls.Key, TestContext.Current.CancellationToken);

            Assert.Equal(AiCallOutcome.Succeeded, result.Outcome);
            Assert.Equal(ModelCalls.Result, result.ResultJson);
        }

        Assert.Equal(ModelCalls.Providers, gateway.Providers.Select(provider => provider.Value).Order());

        // The addresses are those of the configuration file.
        Assert.Equal(
            [
                "POST https://claude.example/v1/messages",
                "POST https://gemini.example/v1beta/models/gemini-test-model:generateContent",
                "POST https://gpt.example/v1/responses",
                "POST https://grok.example/v1/responses",
            ],
            providers.Calls.Select(call => $"{call.Method} {call.Address}"));

        var (claude, gemini, gpt, grok) = (providers.Calls[0], providers.Calls[1], providers.Calls[2], providers.Calls[3]);
        Assert.Equal(ModelCalls.Key, claude.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", claude.Headers["anthropic-version"]);
        Assert.Equal(ModelCalls.Key, gemini.Headers["x-goog-api-key"]);
        Assert.Equal($"Bearer {ModelCalls.Key}", gpt.Headers["Authorization"]);
        Assert.Equal($"Bearer {ModelCalls.Key}", grok.Headers["Authorization"]);

        Assert.All(providers.Calls, call => Assert.Equal("application/json; charset=utf-8", call.Headers["Content-Type"]));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task The_prompt_goes_out_as_it_is_and_only_how_the_result_is_asked_for_is_the_providers_own(string code)
    {
        var provider = ModelCalls.ThatAnswer();
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        await gateway.CompleteAsync(
            new AiProviderCode(code), ModelCalls.RequestTo(code), ModelCalls.Key, TestContext.Current.CancellationToken);

        // The whole body: the model, the prompt once and unchanged, the schema where the provider
        // takes it, and nothing that would be text of the application's own beside the prompt.
        var schema = JsonNode.Parse(ModelCalls.Schema);
        JsonNode expected = code switch
        {
            "claude" => new JsonObject
            {
                ["model"] = "claude-test-model",
                ["max_tokens"] = 16000,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = ModelCalls.Prompt }),
                ["output_config"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = schema } },
            },
            "gemini" => new JsonObject
            {
                ["contents"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = ModelCalls.Prompt }),
                }),
                ["generationConfig"] = new JsonObject { ["responseMimeType"] = "application/json", ["responseJsonSchema"] = schema },
            },
            _ => new JsonObject
            {
                ["model"] = ModelCalls.ModelOf(code),
                ["input"] = ModelCalls.Prompt,
                ["text"] = new JsonObject
                {
                    ["format"] = new JsonObject { ["type"] = "json_schema", ["name"] = "result", ["strict"] = true, ["schema"] = schema },
                },
                ["store"] = false,
            },
        };

        var call = Assert.Single(provider.Calls);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(call.Body!)), $"{code} was sent another body than expected: {call.Body}");
    }

    [Theory]
    [MemberData(nameof(Usages))]
    public async Task An_answer_becomes_the_result_the_usage_and_the_latency(string code, string answer, int inputTokens, int outputTokens)
    {
        var provider = new StandInSystem(_ => StandInSystem.Json(HttpStatusCode.OK, answer));
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var result = await gateway.CompleteAsync(
            new AiProviderCode(code), ModelCalls.RequestTo(code), ModelCalls.Key, TestContext.Current.CancellationToken);

        // The result is what the model wrote, and not what it reasoned on the way.
        Assert.Equal(AiCallOutcome.Succeeded, result.Outcome);
        Assert.Equal(ModelCalls.Result, result.ResultJson);
        Assert.Equal(new AiTokenUsage(inputTokens, outputTokens), result.Usage);
        Assert.InRange(result.Latency, TimeSpan.FromTicks(1), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task An_answer_that_does_not_count_its_tokens_leaves_the_usage_unknown()
    {
        var provider = new StandInSystem(_ => StandInSystem.Json(
            HttpStatusCode.OK,
            """{ "content": [ { "type": "text", "text": "{\"bias\":\"bearish\",\"confidence\":0.9}" } ], "stop_reason": "end_turn" }"""));
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var result = await gateway.CompleteAsync(
            new AiProviderCode("claude"), ModelCalls.RequestTo("claude"), ModelCalls.Key, TestContext.Current.CancellationToken);

        // Unknown, and not none.
        Assert.Equal(AiCallOutcome.Succeeded, result.Outcome);
        Assert.Null(result.Usage);
    }

    [Theory]
    [InlineData("gemini-test-model", "/v1beta/models/gemini-test-model:generateContent")]

    // The list of Gemini's models names a model with a prefix, and the address takes its id.
    [InlineData("models/gemini-test-model", "/v1beta/models/gemini-test-model:generateContent")]

    // An id cannot say where the call goes: it is one part of the path, whatever it holds.
    [InlineData("../v1/other?x=1#y", "/v1beta/models/..%2Fv1%2Fother%3Fx%3D1%23y:generateContent")]
    public async Task The_model_of_gemini_is_part_of_the_address_and_nothing_more(string model, string path)
    {
        var provider = ModelCalls.ThatAnswer();
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        await gateway.CompleteAsync(
            new AiProviderCode("gemini"),
            new AiCallRequest(model, ModelCalls.Prompt, ModelCalls.Schema),
            ModelCalls.Key,
            TestContext.Current.CancellationToken);

        var call = Assert.Single(provider.Calls);
        Assert.Equal("gemini.example", call.Address.Host);
        Assert.Equal(path, call.Address.AbsolutePath);
        Assert.Empty(call.Address.Query);
    }

    [Fact]
    public async Task The_key_of_a_call_is_sent_with_that_call_and_is_nowhere_else()
    {
        // With everything that can be logged: the debug level, at which bodies are written too.
        var providers = ModelCalls.ThatAnswer();
        using var application = providers.Behind(
            AiProviderGatewayModule.HttpClientName, LoggedApplication.Settings().With("Logging:MinimumLevel", "Debug"));
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        // Two users, one after the other, through the same adapters.
        foreach (var code in ModelCalls.Providers)
        {
            foreach (var key in new[] { ModelCalls.Key, AnotherKey })
            {
                await gateway.CompleteAsync(
                    new AiProviderCode(code), ModelCalls.RequestTo(code), key, TestContext.Current.CancellationToken);
            }
        }

        Assert.Equal(8, providers.Calls.Count);
        for (var index = 0; index < providers.Calls.Count; index++)
        {
            var call = providers.Calls[index];
            var (own, other) = index % 2 == 0 ? (ModelCalls.Key, AnotherKey) : (AnotherKey, ModelCalls.Key);

            // Each call carries the key it was given, in one header, and not the key of the call before it.
            Assert.Single(call.Headers.Values, value => value.Contains(own, StringComparison.Ordinal));
            Assert.DoesNotContain(call.Headers.Values, value => value.Contains(other, StringComparison.Ordinal));

            // Never in the address, which is logged, and never in the body.
            Assert.DoesNotContain(own, call.Address.ToString());
            Assert.DoesNotContain(own, call.Body);
        }

        // The bodies are in the log at this level, and the keys are not, in any form.
        var log = application.LogText();
        Assert.Contains("Read the candles of BTCUSDT.", log);
        foreach (var key in new[] { ModelCalls.Key, AnotherKey })
        {
            Assert.DoesNotContain(key, log);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(key)), log);
        }
    }

    [Theory]
    [InlineData("", ModelCalls.Prompt, ModelCalls.Schema, ModelCalls.Key)]
    [InlineData("claude-test-model", " ", ModelCalls.Schema, ModelCalls.Key)]
    [InlineData("claude-test-model", ModelCalls.Prompt, "", ModelCalls.Key)]
    [InlineData("claude-test-model", ModelCalls.Prompt, ModelCalls.Schema, "")]

    // A schema is a JSON object.
    [InlineData("claude-test-model", ModelCalls.Prompt, "not a schema", ModelCalls.Key)]
    [InlineData("claude-test-model", ModelCalls.Prompt, """[ { "type": "object" } ]""", ModelCalls.Key)]
    public async Task A_call_without_a_model_a_prompt_a_schema_or_a_key_is_not_made(string model, string prompt, string schema, string key)
    {
        var provider = ModelCalls.ThatAnswer();
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var error = await Assert.ThrowsAnyAsync<ArgumentException>(() => gateway.CompleteAsync(
            new AiProviderCode("claude"), new AiCallRequest(model, prompt, schema), key, TestContext.Current.CancellationToken));

        // It says what is wrong, and not what it was given.
        Assert.DoesNotContain(ModelCalls.Key, error.Message);
        Assert.DoesNotContain(ModelCalls.Prompt, error.Message);
        Assert.DoesNotContain("not a schema", error.Message);
        Assert.Empty(provider.Calls);
    }

    /// <summary>An answer of the Responses API as a provider sends it that does not add its tokens up.</summary>
    private static string WithoutTotal(string answer)
    {
        var read = JsonNode.Parse(answer)!;
        read["usage"]!.AsObject().Remove("total_tokens");

        return read.ToJsonString();
    }
}
