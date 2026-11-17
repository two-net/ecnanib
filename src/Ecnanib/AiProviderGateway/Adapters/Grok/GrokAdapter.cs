using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters.Grok;

/// <summary>
/// Calls a model of Grok: <c>POST /v1/responses</c>, with the key as a bearer token. The
/// structured result is asked for with <c>text.format</c>: see <see cref="ResponsesFormat"/>.
/// </summary>
/// <remarks>
/// Grok does not document the body of its errors, so its answers are told by what it has been
/// seen to send: an object whose <c>error</c> is the message, as text. A wrong key is answered
/// with 401, or with 400 and a message about the API key. Unlike the check of a key, a call to a
/// model has parameters that can be wrong, so a 400 alone says nothing about the key. A model
/// that Grok does not have is answered with 404 and such an error.
/// </remarks>
internal sealed class GrokAdapter(ModelCall call) : IAiProviderAdapter, IModelCallFormat
{
    private const string AboutTheKey = "API key";

    // The most turns of searching one call may take, each of which the provider charges for. At
    // the limit the model answers from what it has found by then.
    private const int MostSearchTurns = 5;

    // How the id of a call begins that searched X, in an item of an answer.
    private const string SearchOfX = "xs_";

    public AiProviderCode Provider => GrokApi.Provider;

    public Task<AiCallResult> CompleteAsync(AiCallRequest request, string apiKey, CancellationToken cancellationToken) =>
        call.AskAsync(this, request, apiKey, cancellationToken);

    public string PathOf(string model) => ResponsesFormat.Path;

    public void Authorize(HttpRequestHeaders headers, string apiKey) => GrokApi.Authorize(headers, apiKey);

    /// <summary>
    /// The body of the format. A model searches live with two tools of the provider, the search
    /// of the web and the search of X, where much news of this market appears first (FR-CYC-3).
    /// </summary>
    /// <remarks>
    /// Grok writes its sources into the text of an answer unless told otherwise, as links beside
    /// what they support. The text is the structured result, which holds nothing but its fields,
    /// so they are turned off.
    /// </remarks>
    public JsonObject BodyOf(AiCallRequest request, JsonNode schema)
    {
        var body = ResponsesFormat.BodyOf(request, schema);

        if (request.LiveSearch)
        {
            body["tools"] = new JsonArray(ResponsesFormat.WebSearch(), new JsonObject { ["type"] = "x_search" });
            body["include"] = new JsonArray("no_inline_citations");
            body["max_turns"] = MostSearchTurns;
        }

        return body;
    }

    /// <summary>
    /// Reads an answer of the format. Grok counts the searches of a call in its usage, those of X
    /// beside those of the web. Where the usage does not say, the items of the answer do: a
    /// search of X is an item of a tool of Grok's own, which only the id of its call tells apart.
    /// </summary>
    public ModelAnswer? Read(JsonElement answer)
    {
        if (ResponsesFormat.Read(answer) is not { } read)
        {
            return null;
        }

        var counted = answer.Member("usage").Member("server_side_tool_usage_details");
        var ofTheWeb = counted.Count("web_search_calls");
        var ofX = counted.Count("x_search_calls");

        var searches = ofTheWeb is null && ofX is null
            ? read.LiveSearches + answer.Items("output").Count(IsSearchOfX)
            : (ofTheWeb ?? 0) + (ofX ?? 0);

        return read with { LiveSearches = searches };
    }

    public AiCallOutcome OutcomeOfError(HttpStatusCode status, JsonElement? error) => status switch
    {
        HttpStatusCode.Unauthorized => AiCallOutcome.AuthenticationFailed,
        HttpStatusCode.BadRequest when MessageOf(error)?.Contains(AboutTheKey, StringComparison.OrdinalIgnoreCase) == true
            => AiCallOutcome.AuthenticationFailed,
        HttpStatusCode.NotFound when MessageOf(error) is not null => AiCallOutcome.ModelNotFound,
        _ => AiCallOutcome.Failed,
    };

    /// <summary>
    /// The message of an error: <c>error</c> as text, or <c>error.message</c> where Grok answers
    /// in the form that GPT uses. It is read to tell what the error is about, and goes nowhere.
    /// </summary>
    private static string? MessageOf(JsonElement? error) =>
        error.Text("error") ?? error.Member("error").Text("message");

    private static bool IsSearchOfX(JsonElement item) =>
        item.Text("type") == "custom_tool_call" && item.Text("call_id")?.StartsWith(SearchOfX, StringComparison.Ordinal) == true;
}
