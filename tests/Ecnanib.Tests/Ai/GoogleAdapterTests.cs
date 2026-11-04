using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;
using static Ecnanib.Tests.Ai.ProviderSamples;

namespace Ecnanib.Tests.Ai;

/// <summary>CON-13, EXT-AI-01, EXT-AI-02, EXT-AI-04: Gemini, through generateContent.</summary>
public sealed class GoogleAdapterTests
{
    private static readonly IAiProviderAdapter Adapter = Adapters.Get("Google");

    [Fact]
    public void A_completion_sends_the_instructions_the_conversation_and_the_chart()
    {
        using var key = Adapters.Key();
        using var request = Adapter.Complete(key, Request());

        Assert.Equal((HttpMethod.Post, "/v1beta/models/model-x:generateContent"), (request.Method, request.RequestUri!.OriginalString));
        Assert.Equal(Adapters.ApiKey, Adapters.Header(request, "x-goog-api-key"));
        Adapters.AssertKeyOnlyInHeader(request);
        Adapters.AssertBody($$"""
            {"systemInstruction":{"parts":[{"text":"You are the Technical Analyst."}]},
             "contents":[
               {"role":"user","parts":[{"text":"Read this chart."},{"inlineData":{"mimeType":"image/png","data":"{{RedSquareBase64}}"} }]},
               {"role":"model","parts":[{"text":"The trend is up."}]},
               {"role":"user","parts":[{"text":"Answer as JSON."}]}],
             "generationConfig":{"maxOutputTokens":2000} }
            """, request);
    }

    [Fact]
    public void A_schema_is_sent_as_a_JSON_answer_with_its_schema()
    {
        using var key = Adapters.Key();
        using var request = Adapter.Complete(key, Request(schema: true, instructions: " "));
        var body = Adapters.Body(request);

        Assert.Null(body["systemInstruction"]);
        Assert.Equal("application/json", (string?)body["generationConfig"]?["responseMimeType"]);
        Assert.Equal("""["trend","confidence"]""", body["generationConfig"]?["responseJsonSchema"]?["required"]?.ToJsonString());
    }

    /// <summary>Gemini combines search with a schema only from Gemini 3, as a preview, so search wins and the council validates.</summary>
    [Fact]
    public void Live_search_is_Google_Search_and_takes_the_place_of_the_schema()
    {
        using var key = Adapters.Key();
        using var request = Adapter.Complete(key, Request(schema: true, search: true));
        var body = Adapters.Body(request);

        Assert.Equal("""{"maxOutputTokens":2000}""", body["generationConfig"]?.ToJsonString());
        Assert.Equal("""[{"googleSearch":{}}]""", body["tools"]?.ToJsonString());
    }

    [Fact]
    public void A_model_ID_cant_change_the_path()
    {
        using var key = Adapters.Key();
        using var request = Adapter.Complete(key, Request() with { Model = "../files?x=1" });

        Assert.Equal("/v1beta/models/..%2Ffiles%3Fx%3D1:generateContent", request.RequestUri!.OriginalString);
    }

    [Fact]
    public void Models_that_dont_write_text_are_left_out_of_the_list()
    {
        using var key = Adapters.Key();
        using var first = Adapter.ListModels(key, null);
        using var second = Adapter.ListModels(key, "page/2");

        Assert.Equal("/v1beta/models?pageSize=1000", first.RequestUri!.OriginalString);
        Assert.Equal("/v1beta/models?pageSize=1000&pageToken=page%2F2", second.RequestUri!.OriginalString);
        Assert.Equal(Adapters.ApiKey, Adapters.Header(first, "x-goog-api-key"));
        Adapters.AssertKeyOnlyInHeader(second);

        var page = Adapter.ReadModels(Bytes(Google.Models(
            "page-2",
            "gemini-3-pro",
            "gemini-embedding-001",
            "gemini-2.5-flash-preview-tts",
            "gemini-2.5-flash-image",
            "gemini-live-2.5-flash",
            "gemini-2.5-flash-native-audio-preview",
            "imagen-4.0-generate-001",
            "gemini-2.5-flash")));
        var model = page.Models[0];

        Assert.Equal("page-2", page.NextPageToken);
        Assert.Equal(["gemini-3-pro", "gemini-2.5-flash"], page.Models.Select(listed => listed.Id));
        Assert.Equal(("Gemini gemini-3-pro", 1_048_576, 65_536), (model.DisplayName, model.ContextWindow, model.MaxOutputTokens));
    }

    [Fact]
    public void A_key_with_no_models_gets_an_empty_list()
    {
        var page = Adapter.ReadModels(Bytes("{}"));

        Assert.Equal((0, null), (page.Models.Count, page.NextPageToken));
    }

    [Fact]
    public void An_answer_is_its_parts_without_the_thoughts_which_count_as_output()
    {
        var completion = Adapter.ReadCompletion(Bytes(Google.Answer()));

        Assert.Equal("""{"trend":"up","confidence":0.7}""", completion.Text);
        Assert.Equal(new AiUsage(400, 100), completion.Usage);
        Assert.Equal((false, false), (completion.Truncated, completion.Refused));
    }

    [Theory]
    [InlineData("MAX_TOKENS", true, false)]
    [InlineData("CONTINUATION", true, false)]
    [InlineData("SAFETY", false, true)]
    [InlineData("PROHIBITED_CONTENT", false, true)]
    [InlineData("RECITATION", false, true)]
    [InlineData("STOP", false, false)]
    [InlineData("SOMETHING_NEW", false, false)]
    public void The_finish_reason_says_whether_the_answer_is_cut_short_or_refused(string finish, bool truncated, bool refused)
    {
        var completion = Adapter.ReadCompletion(Bytes(Google.Answer(finish: finish)));

        Assert.Equal((truncated, refused), (completion.Truncated, completion.Refused));
    }

    [Fact]
    public void A_blocked_prompt_is_a_refusal_and_its_tokens_count()
    {
        var completion = Adapter.ReadCompletion(Bytes(Google.BlockedPrompt));

        Assert.Equal(("", true, new AiUsage(12, 0)), (completion.Text, completion.Refused, completion.Usage));
        Assert.ThrowsAny<Exception>(() => Adapter.ReadCompletion(Bytes("""{"usageMetadata":{}}""")));
    }

    /// <summary>FR-KEY-04: an invalid key arrives as 400 INVALID_ARGUMENT, and must still read as Authentication.</summary>
    [Fact]
    public void An_invalid_key_is_an_authentication_failure_though_its_status_is_400()
    {
        var error = Adapter.ReadError(400, Bytes(Google.InvalidKey));

        Assert.Equal(new AiProviderError(AiFailure.Authentication, "API_KEY_INVALID", "API key not valid. Please pass a valid API key."), error);
    }

    [Theory]
    [InlineData(429, "RESOURCE_EXHAUSTED", AiFailure.RateLimit)]
    [InlineData(503, "UNAVAILABLE", AiFailure.Overloaded)]
    [InlineData(504, "DEADLINE_EXCEEDED", AiFailure.Timeout)]
    [InlineData(403, "PERMISSION_DENIED", AiFailure.Authentication)]
    [InlineData(401, "UNAUTHENTICATED", AiFailure.Authentication)]
    [InlineData(400, "INVALID_ARGUMENT", null)]
    [InlineData(500, "INTERNAL", null)]
    public void An_error_is_read_by_its_status_name(int code, string status, AiFailure? failure)
    {
        var error = Adapter.ReadError(code, Bytes(Google.Error(code, status, "Something about the prompt.")));

        Assert.Equal(new AiProviderError(failure, status, "Something about the prompt."), error);
    }

    [Fact]
    public void An_error_in_a_list_is_read_too()
    {
        var error = Adapter.ReadError(503, Bytes($"[{Google.Error(503, "UNAVAILABLE", "The model is overloaded.")}]"));

        Assert.Equal(AiFailure.Overloaded, error?.Failure);
        Assert.Null(Adapter.ReadError(502, Bytes("""{"message":"bad gateway"}""")));
    }

    [Fact]
    public void Nothing_reads_a_key_from_anywhere_but_the_call()
    {
        using var first = SecretValue.FromString("first-key");
        using var second = SecretValue.FromString("second-key");
        using var one = Adapter.Complete(first, Request());
        using var two = Adapter.Complete(second, Request());

        Assert.Equal(("first-key", "second-key"), (Adapters.Header(one, "x-goog-api-key"), Adapters.Header(two, "x-goog-api-key")));
    }
}
