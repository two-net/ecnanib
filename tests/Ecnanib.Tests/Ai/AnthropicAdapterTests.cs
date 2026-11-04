using Ecnanib.Modules.AiAdapters.Contracts;
using static Ecnanib.Tests.Ai.ProviderSamples;

namespace Ecnanib.Tests.Ai;

/// <summary>CON-13, EXT-AI-01, EXT-AI-02, EXT-AI-04: Claude, through the Messages API.</summary>
public sealed class AnthropicAdapterTests
{
    private static readonly IAiProviderAdapter Adapter = Adapters.Get("Anthropic");

    [Fact]
    public void A_completion_sends_the_instructions_the_conversation_and_the_chart()
    {
        using var key = Adapters.Key();
        using var request = Adapter.Complete(key, Request());

        Assert.Equal((HttpMethod.Post, "/v1/messages"), (request.Method, request.RequestUri!.OriginalString));
        Assert.Equal((Adapters.ApiKey, "2023-06-01"), (Adapters.Header(request, "x-api-key"), Adapters.Header(request, "anthropic-version")));
        Adapters.AssertKeyOnlyInHeader(request);
        Adapters.AssertBody($$"""
            {"model":"model-x","max_tokens":2000,"system":"You are the Technical Analyst.",
             "messages":[
               {"role":"user","content":[{"type":"text","text":"Read this chart."},
                 {"type":"image","source":{"type":"base64","media_type":"image/png","data":"{{RedSquareBase64}}"} }]},
               {"role":"assistant","content":[{"type":"text","text":"The trend is up."}]},
               {"role":"user","content":[{"type":"text","text":"Answer as JSON."}]}]}
            """, request);
    }

    [Fact]
    public void A_schema_is_sent_as_structured_output()
    {
        using var key = Adapters.Key();
        using var request = Adapter.Complete(key, Request(schema: true, instructions: ""));
        var body = Adapters.Body(request);

        Assert.Null(body["system"]);
        Assert.Equal("json_schema", (string?)body["output_config"]?["format"]?["type"]);
        Assert.Equal("""["up","down","range"]""", body["output_config"]?["format"]?["schema"]?["properties"]?["trend"]?["enum"]?.ToJsonString());
    }

    /// <summary>Web search always carries citations, and structured output refuses them, so search wins and the council validates.</summary>
    [Fact]
    public void Live_search_is_the_web_search_tool_and_takes_the_place_of_the_schema()
    {
        using var key = Adapters.Key();
        using var request = Adapter.Complete(key, Request(schema: true, search: true));
        var body = Adapters.Body(request);

        Assert.Null(body["output_config"]);
        Assert.Equal("""[{"type":"web_search_20250305","name":"web_search","max_uses":5}]""", body["tools"]?.ToJsonString());
    }

    [Fact]
    public void Models_are_listed_a_thousand_at_a_time_from_the_last_ID()
    {
        using var key = Adapters.Key();
        using var first = Adapter.ListModels(key, null);
        using var second = Adapter.ListModels(key, "claude-opus-5");

        Assert.Equal("/v1/models?limit=1000", first.RequestUri!.OriginalString);
        Assert.Equal("/v1/models?limit=1000&after_id=claude-opus-5", second.RequestUri!.OriginalString);
        Assert.Equal((HttpMethod.Get, Adapters.ApiKey), (first.Method, Adapters.Header(first, "x-api-key")));
        Adapters.AssertKeyOnlyInHeader(second);

        var page = Adapter.ReadModels(Bytes(Anthropic.Models("claude-sonnet-5", "claude-opus-5", "claude-sonnet-5")));
        var model = page.Models[0];

        Assert.Equal("claude-sonnet-5", page.NextPageToken);
        Assert.Equal(["claude-opus-5", "claude-sonnet-5"], page.Models.Select(listed => listed.Id));
        Assert.Equal(("Claude claude-opus-5", 1_000_000, 128_000), (model.DisplayName, model.ContextWindow, model.MaxOutputTokens));
        Assert.Equal(new DateTimeOffset(2026, 5, 14, 0, 0, 0, TimeSpan.Zero), model.CreatedAt);
        Assert.Null(Adapter.ReadModels(Bytes(Anthropic.Models(null, "claude-opus-5"))).NextPageToken);
    }

    /// <summary>FR-CAT-01: what the model can do, as Anthropic lists it, with false kept apart from not said.</summary>
    [Fact]
    public void A_model_s_line_and_capabilities_are_read_from_the_list()
    {
        var model = Adapter.ReadModels(Bytes(Anthropic.Models(null, "claude-opus-5"))).Models.Single();

        Assert.Equal("opus", model.Family);
        Assert.Equal(new AiCapabilities(ImageInput: true, StructuredOutput: true, LiveSearch: false, ReasoningEffort: true), model.Capabilities);
    }

    /// <summary>Anthropic sends the epoch for a release date it doesn't know, and an older answer has no capabilities.</summary>
    [Fact]
    public void An_unknown_release_date_and_missing_capabilities_stay_unknown()
    {
        var model = Adapter.ReadModels(Bytes("""
            {"data":[{"type":"model","id":"claude-old","display_name":"Claude Old","created_at":"1970-01-01T00:00:00Z"}],
             "has_more":false,"first_id":"claude-old","last_id":"claude-old"}
            """)).Models.Single();

        Assert.Null(model.CreatedAt);
        Assert.Null(model.Family);
        Assert.Equal(AiCapabilities.Unknown, model.Capabilities);
    }

    [Fact]
    public void An_answer_is_its_text_without_the_thinking()
    {
        var completion = Adapter.ReadCompletion(Bytes(Anthropic.Answer()));

        Assert.Equal("""{"trend":"up","confidence":0.7}""", completion.Text);
        Assert.Equal(new AiUsage(120, 40), completion.Usage);
        Assert.Equal((false, false), (completion.Truncated, completion.Refused));
    }

    [Fact]
    public void An_answer_after_a_search_leaves_out_what_came_before_it_and_counts_cached_input()
    {
        var completion = Adapter.ReadCompletion(Bytes(Anthropic.SearchAnswer));

        Assert.Equal("Bitcoin rose on ETF inflows.", completion.Text);
        Assert.Equal(new AiUsage(2150, 300), completion.Usage);
    }

    [Theory]
    [InlineData("max_tokens", true, false)]
    [InlineData("pause_turn", true, false)]
    [InlineData("model_context_window_exceeded", true, false)]
    [InlineData("refusal", false, true)]
    [InlineData("end_turn", false, false)]
    public void The_stop_reason_says_whether_the_answer_is_cut_short_or_refused(string stop, bool truncated, bool refused)
    {
        var completion = Adapter.ReadCompletion(Bytes(Anthropic.Answer(stop: stop)));

        Assert.Equal((truncated, refused), (completion.Truncated, completion.Refused));
    }

    [Theory]
    [InlineData("overloaded_error", AiFailure.Overloaded)]
    [InlineData("rate_limit_error", AiFailure.RateLimit)]
    [InlineData("authentication_error", AiFailure.Authentication)]
    [InlineData("permission_error", AiFailure.Authentication)]
    [InlineData("invalid_request_error", null)]
    [InlineData("api_error", null)]
    public void An_error_keeps_its_type_and_message(string type, AiFailure? failure)
    {
        var error = Adapter.ReadError(400, Bytes(Anthropic.Error(type, "Something about the prompt.")));

        Assert.Equal(new AiProviderError(failure, type, "Something about the prompt."), error);
    }

    [Fact]
    public void A_body_that_isnt_an_error_says_nothing()
    {
        Assert.Null(Adapter.ReadError(500, Bytes("""{"message":"upstream"}""")));
        Assert.ThrowsAny<Exception>(() => Adapter.ReadCompletion(Bytes("""{"type":"message"}""")));
    }
}
