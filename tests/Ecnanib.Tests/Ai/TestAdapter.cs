using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;

namespace Ecnanib.Tests.Ai;

/// <summary>
/// A provider adapter with a format of its own, which <see cref="FakeAiProvider"/> speaks. It's the only
/// adapter until #19, and it proves that the pipeline needs nothing more from one than the public
/// interface (NFR-MNT-02).
/// </summary>
/// <remarks>
/// Model list: GET /v1/models, ?page= for later pages, answering {"models":[{"id","name","created"}],"next"},
/// where created is optional, in Unix seconds.
/// Completion: POST /v1/complete, answering {"text","input_tokens","output_tokens","stop"}, where stop is
/// "end", "max_tokens" or "refusal". Error: {"error":{"type","message"}}, where the type "busy" is an
/// overload, whatever the status.
/// </remarks>
public sealed class TestAdapter(string name = "Test") : IAiProviderAdapter
{
    public string Name => name;

    /// <summary>Makes every request absolute, on another host, which the pipeline must refuse.</summary>
    public bool Escape { get; set; }

    public HttpRequestMessage ListModels(SecretValue apiKey, string? pageToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Address("/v1/models" + (pageToken is null ? "" : "?page=" + Uri.EscapeDataString(pageToken))));
        request.Headers.TryAddWithoutValidation(FakeAiProvider.KeyHeader, apiKey.Reveal());

        return request;
    }

    public AiModelPage ReadModels(byte[] body)
    {
        var root = JsonNode.Parse(body)!.AsObject();
        var models = root["models"]!.AsArray()
            .Select(model => new AiModel(
                model!["id"]!.GetValue<string>(),
                model["name"]?.GetValue<string>(),
                model["created"] is { } created ? DateTimeOffset.FromUnixTimeSeconds(created.GetValue<long>()) : null))
            .ToList();

        return new AiModelPage(models, root["next"]?.GetValue<string>());
    }

    public HttpRequestMessage Complete(SecretValue apiKey, AiRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["role"] = request.Role.ToString(),
            ["instructions"] = request.Instructions,
            ["messages"] = new JsonArray([.. request.Messages.Select(message => (JsonNode)new JsonObject
            {
                ["speaker"] = message.Speaker.ToString(),
                ["text"] = string.Concat(message.Content.OfType<AiText>().Select(text => text.Text)),
            })]),
            ["max_tokens"] = request.MaxOutputTokens,
            ["search"] = request.LiveSearch,
        };

        var message = new HttpRequestMessage(HttpMethod.Post, Address("/v1/complete"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation(FakeAiProvider.KeyHeader, apiKey.Reveal());

        return message;
    }

    public AiCompletion ReadCompletion(byte[] body)
    {
        var root = JsonNode.Parse(body)!.AsObject();
        var stop = root["stop"]!.GetValue<string>();

        return new AiCompletion(
            root["text"]!.GetValue<string>(),
            new AiUsage(root["input_tokens"]!.GetValue<long>(), root["output_tokens"]!.GetValue<long>()),
            Truncated: stop == "max_tokens",
            Refused: stop == "refusal");
    }

    public AiProviderError? ReadError(int statusCode, byte[] body)
    {
        using var document = JsonDocument.Parse(body);

        if (!document.RootElement.TryGetProperty("error", out var error))
        {
            return null;
        }

        var type = error.TryGetProperty("type", out var value) ? value.GetString() : null;
        var message = error.TryGetProperty("message", out var text) ? text.GetString() : null;

        return new AiProviderError(type == "busy" ? AiFailure.Overloaded : null, type, message);
    }

    private Uri Address(string path) => Escape ? new Uri("https://elsewhere.test" + path) : new Uri(path, UriKind.Relative);
}
