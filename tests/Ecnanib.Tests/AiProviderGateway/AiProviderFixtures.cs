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
