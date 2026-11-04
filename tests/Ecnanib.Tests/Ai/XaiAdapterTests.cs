using Ecnanib.Modules.AiAdapters.Contracts;
using static Ecnanib.Tests.Ai.ProviderSamples;

namespace Ecnanib.Tests.Ai;

/// <summary>CON-13, EXT-AI-01, EXT-AI-02, EXT-AI-04: Grok, through xAI's Responses API.</summary>
public sealed class XaiAdapterTests
{
    private static readonly IAiProviderAdapter Adapter = Adapters.Get("xAI");

    [Fact]
    public void A_completion_is_the_same_request_as_OpenAIs()
    {
        using var key = Adapters.Key();
        using var xai = Adapter.Complete(key, Request(schema: true));
        using var openAi = Adapters.Get("OpenAI").Complete(key, Request(schema: true));

        Assert.Equal((HttpMethod.Post, "/v1/responses"), (xai.Method, xai.RequestUri!.OriginalString));
        Assert.Equal("Bearer " + Adapters.ApiKey, Adapters.Header(xai, "Authorization"));
        Adapters.AssertKeyOnlyInHeader(xai);
        Assert.Equal(Adapters.Body(openAi).ToJsonString(), Adapters.Body(xai).ToJsonString());
    }

    /// <summary>Inline citations, on by default, would break a JSON answer.</summary>
    [Fact]
    public void Live_search_is_web_and_X_search_without_inline_citations()
    {
        using var key = Adapters.Key();
        using var request = Adapter.Complete(key, Request(schema: true, search: true));
        var body = Adapters.Body(request);

        Assert.Equal("""[{"type":"web_search"},{"type":"x_search"}]""", body["tools"]?.ToJsonString());
        Assert.Equal("""["no_inline_citations"]""", body["include"]?.ToJsonString());
        Assert.Equal("json_schema", (string?)body["text"]?["format"]?["type"]);
    }

    [Fact]
    public void Only_language_models_that_write_text_are_listed()
    {
        using var key = Adapters.Key();
        using var request = Adapter.ListModels(key, null);

        Assert.Equal((HttpMethod.Get, "/v1/language-models"), (request.Method, request.RequestUri!.OriginalString));
        Assert.Equal("Bearer " + Adapters.ApiKey, Adapters.Header(request, "Authorization"));

        var page = Adapter.ReadModels(Bytes(Xai.Models(("grok-4.7", "text"), ("grok-imagine-image", "image"), ("grok-4.7-fast", "text"))));

        Assert.Null(page.NextPageToken);
        Assert.Equal(["grok-4.7", "grok-4.7-fast"], page.Models.Select(model => model.Id));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), page.Models[0].CreatedAt);
    }

    /// <summary>xAI's output_tokens leaves out reasoning, which it bills as output all the same.</summary>
    [Fact]
    public void Reasoning_is_added_to_the_output()
    {
        var completion = Adapter.ReadCompletion(Bytes(Responses.Answer()));

        Assert.Equal("""{"trend":"up","confidence":0.7}""", completion.Text);
        Assert.Equal(new AiUsage(1200, 1600), completion.Usage);
    }

    /// <summary>FR-KEY-04: xAI sends a wrong key back as 400 with no code of its own, so its message decides.</summary>
    [Fact]
    public void A_wrong_key_is_an_authentication_failure_though_its_status_is_400()
    {
        var error = Adapter.ReadError(400, Bytes(Xai.InvalidKey));

        Assert.Equal(AiFailure.Authentication, error?.Failure);
        Assert.Equal("Client specified an invalid argument", error?.Code);
        Assert.StartsWith("Incorrect API key provided", error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_errors_are_left_to_their_status()
    {
        Assert.Equal(
            new AiProviderError(null, "not-found", "The model model-x does not exist or your team does not have access to it."),
            Adapter.ReadError(404, Bytes(Xai.Error("not-found", "The model model-x does not exist or your team does not have access to it."))));
        Assert.Null(Adapter.ReadError(400, Bytes(Xai.Error("Client specified an invalid argument", "max_output_tokens must be positive.")))?.Failure);
        Assert.Equal(
            new AiProviderError(null, "rate_limit_exceeded", "Too many requests."),
            Adapter.ReadError(429, Bytes("""{"error":{"message":"Too many requests.","type":"rate_limit_exceeded"}}""")));
        Assert.Null(Adapter.ReadError(502, Bytes("""{"detail":"bad gateway"}""")));
    }
}
