using System.Text.Json;
using Ecnanib.Modules.AiAdapters.Contracts;
using static Ecnanib.Tests.Ai.ProviderSamples;

namespace Ecnanib.Tests.Ai;

/// <summary>CON-13, EXT-AI-01, EXT-AI-02, EXT-AI-04: GPT, through the Responses API.</summary>
public sealed class OpenAiAdapterTests
{
    private static readonly IAiProviderAdapter Adapter = Adapters.Get("OpenAI");

    /// <summary>Nothing is stored at OpenAI, which otherwise keeps a response for 30 days.</summary>
    [Fact]
    public void A_completion_sends_the_instructions_the_conversation_and_the_chart_and_stores_nothing()
    {
        using var key = Adapters.Key();
        using var request = Adapter.Complete(key, Request());

        Assert.Equal((HttpMethod.Post, "/v1/responses"), (request.Method, request.RequestUri!.OriginalString));
        Assert.Equal("Bearer " + Adapters.ApiKey, Adapters.Header(request, "Authorization"));
        Adapters.AssertKeyOnlyInHeader(request);
        Adapters.AssertBody($$"""
            {"model":"model-x","instructions":"You are the Technical Analyst.","max_output_tokens":2000,"store":false,
             "input":[
               {"role":"user","content":[{"type":"input_text","text":"Read this chart."},
                 {"type":"input_image","image_url":"data:image/png;base64,{{RedSquareBase64}}"}]},
               {"role":"assistant","content":"The trend is up."},
               {"role":"user","content":[{"type":"input_text","text":"Answer as JSON."}]}]}
            """, request);
    }

    [Fact]
    public void A_schema_is_strict_and_live_search_is_the_web_search_tool_beside_it()
    {
        using var key = Adapters.Key();
        using var request = Adapter.Complete(key, Request(schema: true, search: true, instructions: ""));
        var body = Adapters.Body(request);

        Assert.Null(body["instructions"]);
        Assert.Null(body["include"]);
        Assert.Equal("""[{"type":"web_search"}]""", body["tools"]?.ToJsonString());
        Assert.Equal(("json_schema", "technical_assessment", true), (
            (string?)body["text"]?["format"]?["type"],
            (string?)body["text"]?["format"]?["name"],
            (bool?)body["text"]?["format"]?["strict"]));
        Assert.Equal("""["up","down","range"]""", body["text"]?["format"]?["schema"]?["properties"]?["trend"]?["enum"]?.ToJsonString());
    }

    [Fact]
    public void A_tiny_output_limit_is_raised_to_OpenAIs_least()
    {
        using var key = Adapters.Key();
        using var request = Adapter.Complete(key, Request() with { MaxOutputTokens = 5 });

        Assert.Equal(16, (int?)Adapters.Body(request)["max_output_tokens"]);
    }

    [Fact]
    public void Models_that_dont_write_text_are_left_out_of_the_list()
    {
        using var key = Adapters.Key();
        using var request = Adapter.ListModels(key, null);

        Assert.Equal((HttpMethod.Get, "/v1/models"), (request.Method, request.RequestUri!.OriginalString));
        Assert.Equal("Bearer " + Adapters.ApiKey, Adapters.Header(request, "Authorization"));

        var page = Adapter.ReadModels(Bytes(OpenAi.Models(
            "gpt-6.1-terra",
            "text-embedding-3-large",
            "gpt-4o-mini-tts",
            "whisper-1",
            "dall-e-3",
            "gpt-image-1",
            "omni-moderation-latest",
            "gpt-realtime",
            "gpt-4o-audio-preview",
            "gpt-4o-transcribe",
            "sora-2",
            "davinci-002",
            "gpt-6-luna")));

        Assert.Null(page.NextPageToken);
        Assert.Equal(["gpt-6.1-terra", "gpt-6-luna"], page.Models.Select(model => model.Id));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), page.Models[0].CreatedAt);
    }

    /// <summary>
    /// A fine-tune belongs to one account, and its ID names the organisation, so it never reaches the catalog
    /// every user sees (NFR-SEC-04).
    /// </summary>
    [Fact]
    public void Fine_tuned_models_are_left_out_of_the_list()
    {
        var page = Adapter.ReadModels(Bytes(OpenAi.Models("gpt-6-luna", "ft:gpt-6-luna:acme-trading::Ab12Cd34", "ft:gpt-6-luna:acme-trading:ckpt:step-100")));

        Assert.Equal(["gpt-6-luna"], page.Models.Select(model => model.Id));
        Assert.Equal(AiCapabilities.Unknown, page.Models[0].Capabilities);
    }

    [Fact]
    public void An_answer_is_the_last_messages_text_and_reasoning_is_already_in_the_output()
    {
        var completion = Adapter.ReadCompletion(Bytes(Responses.Answer()));

        Assert.Equal("""{"trend":"up","confidence":0.7}""", completion.Text);
        Assert.Equal(new AiUsage(1200, 900), completion.Usage);
        Assert.Equal((false, false), (completion.Truncated, completion.Refused));
    }

    [Theory]
    [InlineData("incomplete", "max_output_tokens", null, true, false)]
    [InlineData("incomplete", "content_filter", null, false, true)]
    [InlineData("completed", null, "I can't help with that.", false, true)]
    public void An_unfinished_or_refused_answer_says_so(string status, string? reason, string? refusal, bool truncated, bool refused)
    {
        var completion = Adapter.ReadCompletion(Bytes(Responses.Answer(status: status, reason: reason, refusal: refusal)));

        Assert.Equal((truncated, refused), (completion.Truncated, completion.Refused));
    }

    [Fact]
    public void A_failed_response_cant_be_read()
    {
        Assert.Throws<JsonException>(() => Adapter.ReadCompletion(Bytes(Responses.Answer(status: "failed"))));
        Assert.Throws<JsonException>(() => Adapter.ReadCompletion(Bytes("""{"output":[]}""")));
    }

    /// <summary>A key with no money left gets a 429, like a rate limit, but waiting won't help, so it's Other and not retried.</summary>
    [Theory]
    [InlineData("insufficient_quota", "insufficient_quota")]
    [InlineData("insufficient_quota", "credit_balance_exhausted")]
    [InlineData("requests", "project_spend_limit_exceeded")]
    [InlineData("insufficient_quota", null)]
    public void A_key_out_of_money_is_Other(string type, string? code)
    {
        var error = Adapter.ReadError(429, Bytes(OpenAi.Error("You exceeded your current quota.", type, code)));

        Assert.Equal(new AiProviderError(AiFailure.Other, code ?? type, "You exceeded your current quota."), error);
    }

    [Fact]
    public void Other_errors_are_left_to_their_status()
    {
        Assert.Equal(
            new AiProviderError(null, "invalid_api_key", "Incorrect API key provided: sk-ab***yz."),
            Adapter.ReadError(401, Bytes(OpenAi.InvalidKey)));
        Assert.Null(Adapter.ReadError(429, Bytes(OpenAi.Error("Rate limit reached.", "requests", null)))?.Failure);
        Assert.Null(Adapter.ReadError(502, Bytes("""{"detail":"bad gateway"}""")));
    }
}
