using System.Net;
using System.Text.Json;
using Ecnanib.AiProviderGateway;
using Ecnanib.Tests.Logging;

namespace Ecnanib.Tests.AiProviderGateway;

/// <summary>
/// What the tests of the gateway send, and what each provider answers a call to a model with, in
/// the form that its documentation gives. No call leaves the tests: a <see cref="StandInSystem"/>
/// answers with these.
/// </summary>
internal static class ModelCalls
{
    /// <summary>The four providers, by code, in the order of the configuration file.</summary>
    public static readonly string[] Providers = ["claude", "gemini", "gpt", "grok"];

    /// <summary>A key that the rules accept. It has the shape of no real key, so that nothing masks it for its shape.</summary>
    public const string Key = "a-users-key-0123456789-abcdefghij-Kx4P";

    /// <summary>A prompt with what a body must carry unharmed: line breaks, quotes, braces, and letters beyond ASCII.</summary>
    public const string Prompt =
        "Read the candles of BTCUSDT.\nSay \"bullish\", \"bearish\", or \"neutral\": {no} guess, <no> tags & nothing else. Zürich, 東京.";

    /// <summary>A schema inside what all four providers take.</summary>
    public const string Schema =
        """
        {
          "type": "object",
          "properties": {
            "bias": { "type": "string", "enum": [ "bullish", "bearish", "neutral" ] },
            "confidence": { "type": "number" }
          },
          "required": [ "bias", "confidence" ],
          "additionalProperties": false
        }
        """;

    /// <summary>A result that matches the schema, as a model writes it.</summary>
    public const string Result = """{"bias":"bullish","confidence":0.7}""";

    /// <summary>The model that a test calls at a provider.</summary>
    public static string ModelOf(string provider) => $"{provider}-test-model";

    public static AiCallRequest RequestTo(string provider) => new(ModelOf(provider), Prompt, Schema);

    /// <summary>The same request, of a model that may search live for it.</summary>
    public static AiCallRequest SearchingRequestTo(string provider) => RequestTo(provider) with { LiveSearch = true };

    /// <summary>What a model says before it searches, which is no part of its answer.</summary>
    public const string BeforeSearching = "I will look for today's news about Bitcoin first.";

    /// <summary>The provider that a request went to, by the host of the test's configuration file, such as <c>claude.example</c>.</summary>
    public static string ProviderOf(HttpRequestMessage request) => request.RequestUri!.Host.Split('.')[0];

    /// <summary>A provider that answers every call with a finished answer of its own form, which holds <see cref="Result"/>.</summary>
    public static StandInSystem ThatAnswer() =>
        new(request => StandInSystem.Json(HttpStatusCode.OK, Finished(ProviderOf(request), Result)));

    /// <summary>The answer of a provider whose model finished, with the text it wrote. It used 100 input and 50 output tokens.</summary>
    public static string Finished(string provider, string text) => provider switch
    {
        "claude" => Claude(text),
        "gemini" => Gemini(text),
        "gpt" => Responses(text),
        "grok" => Responses(text),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>An answer of Claude's Messages API.</summary>
    public static string Claude(
        string text, string stopReason = "end_turn", int input = 100, int cacheWritten = 0, int cacheRead = 0, int output = 50) =>
        $$"""
        {
          "id": "msg_01", "type": "message", "role": "assistant", "model": "claude-test-model",
          "content": [ { "type": "thinking", "thinking": "", "signature": "c2ln" }, { "type": "text", "text": {{Quoted(text)}} } ],
          "stop_reason": "{{stopReason}}", "stop_sequence": null,
          "usage": {
            "input_tokens": {{input}}, "cache_creation_input_tokens": {{cacheWritten}}, "cache_read_input_tokens": {{cacheRead}},
            "output_tokens": {{output}}
          }
        }
        """;

    /// <summary>An answer of Gemini's <c>generateContent</c>.</summary>
    public static string Gemini(string text, string finishReason = "STOP", int prompt = 100, int candidates = 50, int thoughts = 0) =>
        $$"""
        {
          "candidates": [ {
            "content": { "role": "model", "parts": [ { "text": "What to say.", "thought": true }, { "text": {{Quoted(text)}} } ] },
            "finishReason": "{{finishReason}}", "index": 0
          } ],
          "usageMetadata": {
            "promptTokenCount": {{prompt}}, "candidatesTokenCount": {{candidates}}, "thoughtsTokenCount": {{thoughts}},
            "totalTokenCount": {{prompt + candidates + thoughts}}
          },
          "modelVersion": "gemini-test-model"
        }
        """;

    /// <summary>
    /// An answer of the Responses API, which GPT and Grok share. GPT counts what the model
    /// reasoned inside <c>output_tokens</c>, and Grok beside it: <paramref name="total"/> says
    /// which, and is input and output together unless given.
    /// </summary>
    public static string Responses(
        string text,
        string status = "completed",
        string? incompleteReason = null,
        int input = 100,
        int output = 50,
        int reasoning = 0,
        int? total = null)
    {
        var details = incompleteReason is null ? "null" : $$"""{ "reason": "{{incompleteReason}}" }""";

        return $$"""
            {
              "id": "resp_01", "object": "response", "status": "{{status}}", "incomplete_details": {{details}},
              "output": [
                { "id": "rs_01", "type": "reasoning", "summary": [] },
                { "id": "msg_01", "type": "message", "role": "assistant", "status": "completed",
                  "content": [ { "type": "output_text", "text": {{Quoted(text)}}, "annotations": [] } ] }
              ],
              "usage": {
                "input_tokens": {{input}}, "output_tokens": {{output}},
                "output_tokens_details": { "reasoning_tokens": {{reasoning}} }, "total_tokens": {{total ?? input + output}}
              }
            }
            """;
    }

    /// <summary>
    /// The answer of a provider whose model searched live before it finished, with the text it
    /// wrote after its last search, in the form that the provider's documentation gives. The
    /// model said <see cref="BeforeSearching"/> first.
    /// </summary>
    public static string Searched(string provider, string text) => provider switch
    {
        "claude" => ClaudeSearched(text),
        "gemini" => GeminiSearched(text),
        "gpt" => ResponsesSearched(text),
        "grok" => ResponsesSearched(text, countedByGrok: (Web: 1, X: 1)),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>
    /// An answer of Claude's Messages API with its web search: what the model said before it
    /// searched, each search with its results, and the answer, in two blocks, as an answer that
    /// names its sources is. The usage counts the searches.
    /// </summary>
    public static string ClaudeSearched(string text, int searches = 2, string stopReason = "end_turn")
    {
        var half = text.Length / 2;
        var searching = string.Concat(Enumerable.Range(1, searches).Select(number =>
            $$"""
            { "type": "server_tool_use", "id": "srvtoolu_0{{number}}", "name": "web_search", "input": { "query": "bitcoin news {{number}}" } },
            { "type": "web_search_tool_result", "tool_use_id": "srvtoolu_0{{number}}", "content": [
              { "type": "web_search_result", "url": "https://news.example/{{number}}", "title": "Bitcoin today", "encrypted_content": "RW5j", "page_age": "November 17, 2026" } ] },
            """));

        return $$"""
            {
              "id": "msg_01", "type": "message", "role": "assistant", "model": "claude-test-model",
              "content": [
                { "type": "text", "text": {{Quoted(BeforeSearching)}} },
                {{searching}}
                { "type": "text", "text": {{Quoted(text[..half])}} },
                { "type": "text", "text": {{Quoted(text[half..])}}, "citations": [
                  { "type": "web_search_result_location", "url": "https://news.example/1", "title": "Bitcoin today", "encrypted_index": "RW5j", "cited_text": "Bitcoin rose." } ] }
              ],
              "stop_reason": "{{stopReason}}", "stop_sequence": null,
              "usage": { "input_tokens": 100, "output_tokens": 50, "server_tool_use": { "web_search_requests": {{searches}} } }
            }
            """;
    }

    /// <summary>
    /// An answer of Gemini's <c>generateContent</c> that was grounded with Google Search: the
    /// queries that the model ran are listed with the candidate, and what the tool put before
    /// the model is counted apart from the prompt.
    /// </summary>
    public static string GeminiSearched(string text, string[]? queries = null, int toolUsePrompt = 400) =>
        $$"""
        {
          "candidates": [ {
            "content": { "role": "model", "parts": [ { "text": "What to look for.", "thought": true }, { "text": {{Quoted(text)}} } ] },
            "finishReason": "STOP", "index": 0,
            "groundingMetadata": {
              "webSearchQueries": {{JsonSerializer.Serialize(queries ?? ["bitcoin news today", "bitcoin etf flows"])}},
              "groundingChunks": [ { "web": { "uri": "https://news.example/1", "title": "news.example" } } ],
              "searchEntryPoint": { "renderedContent": "<div>Search</div>" }
            }
          } ],
          "usageMetadata": {
            "promptTokenCount": 100, "toolUsePromptTokenCount": {{toolUsePrompt}}, "candidatesTokenCount": 50, "thoughtsTokenCount": 0,
            "totalTokenCount": {{150 + toolUsePrompt}}
          },
          "modelVersion": "gemini-test-model"
        }
        """;

    /// <summary>
    /// An answer of the Responses API whose model searched: what it said before, a search of the
    /// web, a page that it opened, which is no search, a search that Grok ran on X, and the
    /// answer. GPT counts no searches in its usage, and Grok does where
    /// <paramref name="countedByGrok"/> says how many.
    /// </summary>
    public static string ResponsesSearched(string text, (int Web, int X)? countedByGrok = null, string status = "completed")
    {
        var counted = countedByGrok is { } searches
            ? $$""", "server_side_tool_usage_details": { "web_search_calls": {{searches.Web}}, "x_search_calls": {{searches.X}}, "x_posts_fetched": 44 }"""
            : string.Empty;

        return $$"""
            {
              "id": "resp_01", "object": "response", "status": "{{status}}", "incomplete_details": null,
              "output": [
                { "id": "rs_01", "type": "reasoning", "summary": [] },
                { "id": "msg_00", "type": "message", "role": "assistant", "status": "completed",
                  "content": [ { "type": "output_text", "text": {{Quoted(BeforeSearching)}}, "annotations": [] } ] },
                { "id": "ws_01", "type": "web_search_call", "status": "completed", "action": { "type": "search", "query": "bitcoin news today" } },
                { "id": "ws_02", "type": "web_search_call", "status": "completed", "action": { "type": "open_page", "url": "https://news.example/1" } },
                { "id": "ctc_01", "type": "custom_tool_call", "call_id": "xs_01", "name": "x_keyword_search", "status": "completed" },
                { "id": "msg_01", "type": "message", "role": "assistant", "status": "completed",
                  "content": [ { "type": "output_text", "text": {{Quoted(text)}},
                    "annotations": [ { "type": "url_citation", "start_index": 1, "end_index": 5, "url": "https://news.example/1", "title": "1" } ] } ] }
              ],
              "usage": { "input_tokens": 100, "output_tokens": 50, "total_tokens": 150{{counted}} }
            }
            """;
    }

    private static string Quoted(string text) => JsonSerializer.Serialize(text);
}

/// <summary>
/// A clock that keeps the timers the application asks it for, so that a test can see how long a
/// limit is and let it run out at once. Everything else is the time of the system: a timer that
/// no test fires still fires when it is due.
/// </summary>
internal sealed class TimerClock : TimeProvider
{
    private readonly List<(TimeSpan Due, TimerCallback Callback, object? State)> _timers = [];

    /// <summary>How long each timer was set for, in the order they were asked for.</summary>
    public IReadOnlyList<TimeSpan> Timers
    {
        get
        {
            lock (_timers)
            {
                return [.. _timers.Select(timer => timer.Due)];
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_timers)
        {
            _timers.Add((dueTime, callback, state));
        }

        return base.CreateTimer(callback, state, dueTime, period);
    }

    /// <summary>Lets the latest timer that was set for this long run out now. It must be there.</summary>
    public void RunOut(TimeSpan due)
    {
        (TimeSpan Due, TimerCallback Callback, object? State) timer;
        lock (_timers)
        {
            timer = _timers.Last(set => set.Due == due);
        }

        timer.Callback(timer.State);
    }
}
