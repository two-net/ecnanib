using System.Net;
using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;

namespace Ecnanib.Tests.Ai;

/// <summary>
/// The four real providers' settings, on hosts that never resolve, so nothing reaches a provider by
/// accident. Also requests, and bodies shaped like each provider's documented answers, as of 2026-11.
/// </summary>
internal static class ProviderSamples
{
    public const string AnthropicHost = "anthropic.ai.test";
    public const string GoogleHost = "google.ai.test";
    public const string OpenAiHost = "openai.ai.test";
    public const string XaiHost = "xai.ai.test";

    public const string Model = "model-x";
    public const string Instructions = "You are the Technical Analyst.";

    /// <summary>A 32 by 32 red square, small enough to inline and big enough for any provider.</summary>
    public const string RedSquareBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAIAAAD8GO2jAAAAKklEQVR42mO4oKBAU8QwasGoBaMWjFowasGoBaMWjFowasGoBaMWDBULAE7YQD2B7wk2AAAAAElFTkSuQmCC";

    public static readonly byte[] RedSquare = Convert.FromBase64String(RedSquareBase64);

    /// <summary>In the subset every provider enforces. See the schema rules in docs/architecture.md.</summary>
    public static readonly AiOutputSchema Schema = AiOutputSchema.Parse("technical_assessment", """
        {"type":"object","additionalProperties":false,"required":["trend","confidence"],
         "properties":{"trend":{"type":"string","enum":["up","down","range"]},"confidence":{"type":"number"}}}
        """);

    /// <summary>Required by every application, now that the four adapters are registered.</summary>
    public static Dictionary<string, string?> Settings() => new()
    {
        ["AiProviders:Anthropic:BaseUrl"] = $"https://{AnthropicHost}",
        ["AiProviders:Anthropic:RetryBackoffMs"] = "0",
        ["AiProviders:Google:BaseUrl"] = $"https://{GoogleHost}",
        ["AiProviders:Google:RetryBackoffMs"] = "0",
        ["AiProviders:OpenAI:BaseUrl"] = $"https://{OpenAiHost}",
        ["AiProviders:OpenAI:RetryBackoffMs"] = "0",
        ["AiProviders:xAI:BaseUrl"] = $"https://{XaiHost}",
        ["AiProviders:xAI:RetryBackoffMs"] = "0",
    };

    /// <summary>A chart and a question, the model's answer that broke its contract, and the errors sent back (FR-CYC-05).</summary>
    public static AiRequest Request(bool schema = false, bool search = false, string instructions = Instructions) =>
        new(
            Model,
            AiRole.TechnicalAnalyst,
            instructions,
            [
                AiMessage.User(new AiText("Read this chart."), new AiImage("image/png", RedSquare)),
                AiMessage.Assistant("The trend is up."),
                AiMessage.User("Answer as JSON."),
            ],
            2000)
        {
            Schema = schema ? Schema : null,
            LiveSearch = search,
        };

    public static AiKey Key(string provider, string apiKey = AiHarness.ApiKey) => new(provider, Guid.NewGuid(), SecretValue.FromString(apiKey));

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => FakeAiProvider.Json(body, status);

    public static byte[] Bytes(string body) => System.Text.Encoding.UTF8.GetBytes(body);

    private static string Quote(string text) => JsonSerializer.Serialize(text);

    public static class Anthropic
    {
        /// <param name="next">The last ID on this page when more follow.</param>
        public static string Models(string? next, params string[] ids) => $$"""
            {"data":[{{string.Join(',', ids.Select(id => $$"""
                {"type":"model","id":{{Quote(id)}},"display_name":"Claude {{id}}","created_at":"2026-05-14T00:00:00Z",
                 "max_input_tokens":1000000,"max_tokens":128000,
                 "capabilities":{"image_input":{"supported":true},"structured_outputs":{"supported":true} } }
                """))}}],
             "has_more":{{(next is null ? "false" : "true")}},"first_id":{{Quote(ids.FirstOrDefault() ?? "")}},"last_id":{{(next is null ? "null" : Quote(next))}}}
            """;

        public static string Answer(string text = """{"trend":"up","confidence":0.7}""", string stop = "end_turn") => $$"""
            {"id":"msg_01","type":"message","role":"assistant","model":"claude-opus-5",
             "content":[{"type":"thinking","thinking":"","signature":"EuYBCkQ"},{"type":"text","text":{{Quote(text)}}}],
             "stop_reason":{{Quote(stop)}},"stop_sequence":null,
             "usage":{"input_tokens":120,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":40} }
            """;

        /// <summary>A sentence before the search, the search and its results, and an answer split by its citations.</summary>
        public const string SearchAnswer = """
            {"id":"msg_02","type":"message","role":"assistant","model":"claude-opus-5",
             "content":[
               {"type":"text","text":"I'll look for today's news."},
               {"type":"server_tool_use","id":"srvtoolu_01","name":"web_search","input":{"query":"bitcoin news"}},
               {"type":"web_search_tool_result","tool_use_id":"srvtoolu_01","content":[
                 {"type":"web_search_result","url":"https://news.example/btc","title":"BTC","encrypted_content":"Eq0","page_age":"1 hour ago"}]},
               {"type":"text","text":"Bitcoin rose "},
               {"type":"text","text":"on ETF inflows.","citations":[{"type":"web_search_result_location","url":"https://news.example/btc","title":"BTC","encrypted_index":"Eo8","cited_text":"..."}]}],
             "stop_reason":"end_turn","stop_sequence":null,
             "usage":{"input_tokens":2000,"cache_creation_input_tokens":100,"cache_read_input_tokens":50,"output_tokens":300,
                      "server_tool_use":{"web_search_requests":1}}}
            """;

        public static string Error(string type, string message) => $$"""
            {"type":"error","error":{"type":{{Quote(type)}},"message":{{Quote(message)}}},"request_id":"req_011"}
            """;
    }

    public static class Google
    {
        /// <param name="next">The next page's token, or null on the last page.</param>
        public static string Models(string? next, params string[] ids) => $$"""
            {"models":[{{string.Join(',', ids.Select(id => $$"""
                {"name":"models/{{id}}","baseModelId":{{Quote(id)}},"version":"001","displayName":"Gemini {{id}}",
                 "inputTokenLimit":1048576,"outputTokenLimit":65536,
                 "supportedGenerationMethods":[{{(id.Contains("embedding", StringComparison.Ordinal) ? "\"embedContent\"" : "\"generateContent\",\"countTokens\"")}}],
                 "thinking":true}
                """))}}]{{(next is null ? "" : $",\"nextPageToken\":{Quote(next)}")}}}
            """;

        public static string Answer(string text = """{"trend":"up","confidence":0.7}""", string finish = "STOP") => $$"""
            {"candidates":[{"content":{"role":"model","parts":[
                {"text":"Looking at the chart first.","thought":true},
                {"text":{{Quote(text[..(text.Length / 2)])}}},{"text":{{Quote(text[(text.Length / 2)..])}}}]},
              "finishReason":{{Quote(finish)}},"index":0}],
             "usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":20,"thoughtsTokenCount":80,"toolUsePromptTokenCount":300,"totalTokenCount":500},
             "modelVersion":"gemini-3-pro","responseId":"r1"}
            """;

        public const string BlockedPrompt = """
            {"promptFeedback":{"blockReason":"SAFETY","safetyRatings":[]},"usageMetadata":{"promptTokenCount":12,"totalTokenCount":12}}
            """;

        public static string Error(int code, string status, string message, string? reason = null) => $$"""
            {"error":{"code":{{code}},"message":{{Quote(message)}},"status":{{Quote(status)}},"details":[{{(reason is null ? "" : $$"""
                {"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":{{Quote(reason)}},"domain":"googleapis.com",
                 "metadata":{"service":"generativelanguage.googleapis.com"} }
                """)}}]} }
            """;

        public static string InvalidKey => Error(400, "INVALID_ARGUMENT", "API key not valid. Please pass a valid API key.", "API_KEY_INVALID");
    }

    /// <summary>The Responses API, which OpenAI and xAI share.</summary>
    public static class Responses
    {
        /// <param name="status">completed or incomplete.</param>
        /// <param name="reason">Why it's incomplete, such as max_output_tokens or content_filter.</param>
        /// <param name="refusal">The model's refusal, which takes the place of its text.</param>
        public static string Answer(
            string text = """{"trend":"up","confidence":0.7}""",
            string status = "completed",
            string? reason = null,
            string? refusal = null) => $$"""
            {"id":"resp_1","object":"response","status":{{Quote(status)}},
             "incomplete_details":{{(reason is null ? "null" : $$"""{"reason":{{Quote(reason)}}}""")}},"error":null,"store":false,
             "output":[
               {"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"gAAAA"},
               {"type":"web_search_call","id":"ws_1","status":"completed","action":{"type":"search","query":"bitcoin news"} },
               {"type":"message","id":"msg_0","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Let me check.","annotations":[]}]},
               {"type":"message","id":"msg_1","role":"assistant","status":"completed","content":[{{(refusal is null
                   ? $$"""{"type":"output_text","text":{{Quote(text)}},"annotations":[{"type":"url_citation","url":"https://news.example/btc","title":"BTC","start_index":0,"end_index":5}],"logprobs":[]}"""
                   : $$"""{"type":"refusal","refusal":{{Quote(refusal)}}}""")}}]}],
             "usage":{"input_tokens":1200,"input_tokens_details":{"cached_tokens":200},
                      "output_tokens":900,"output_tokens_details":{"reasoning_tokens":700},"total_tokens":2100} }
            """;
    }

    public static class OpenAi
    {
        public static string Models(params string[] ids) => $$"""
            {"object":"list","data":[{{string.Join(',', ids.Select(id => $$"""
                {"id":{{Quote(id)}},"object":"model","created":1790000000,"owned_by":"system","shutdown_date":null}
                """))}}]}
            """;

        public static string Error(string message, string? type, string? code) => $$"""
            {"error":{"message":{{Quote(message)}},"type":{{(type is null ? "null" : Quote(type))}},"param":null,"code":{{(code is null ? "null" : Quote(code))}}} }
            """;

        public static string InvalidKey => Error("Incorrect API key provided: sk-ab***yz.", "invalid_request_error", "invalid_api_key");
    }

    public static class Xai
    {
        /// <param name="models">Each model's ID and what it outputs.</param>
        public static string Models(params (string Id, string Output)[] models) => $$"""
            {"models":[{{string.Join(',', models.Select(model => $$"""
                {"id":{{Quote(model.Id)}},"fingerprint":"fp_1","created":1790000000,"object":"model","owned_by":"xai","version":"1.0",
                 "input_modalities":["text","image"],"output_modalities":[{{Quote(model.Output)}}],"aliases":[],
                 "prompt_text_token_price":12500,"completion_text_token_price":50000}
                """))}}]}
            """;

        public static string Error(string code, string message) => $$"""
            {"code":{{Quote(code)}},"error":{{Quote(message)}}}
            """;

        public static string InvalidKey =>
            Error("Client specified an invalid argument", "Incorrect API key provided: xa***yz. You can obtain an API key from https://console.x.ai.");
    }
}
