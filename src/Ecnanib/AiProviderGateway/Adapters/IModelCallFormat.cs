using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters;

/// <summary>
/// What differs between providers in a call to a model: where it goes, how the key travels, how
/// the structured result is asked for, and how the answer reads. Each provider's adapter says
/// those, and <see cref="ModelCall"/> does the rest, which is the same for all.
/// </summary>
internal interface IModelCallFormat
{
    /// <summary>The provider, whose address and time limit the configuration file holds.</summary>
    AiProviderCode Provider { get; }

    /// <summary>The path of the call for a model, under the provider's address.</summary>
    string PathOf(string model);

    /// <summary>Puts the key into the headers of the request, as the provider takes it.</summary>
    void Authorize(HttpRequestHeaders headers, string apiKey);

    /// <summary>
    /// The body of the request: the model, the prompt as it is, and the schema where the provider
    /// takes it. How the structured result is asked for is all that a provider's body has of its
    /// own (FR-PRM-10).
    /// </summary>
    JsonObject BodyOf(AiCallRequest request, JsonNode schema);

    /// <summary>
    /// Reads an answer that came with a success status, or says null when it is not what the
    /// provider sends.
    /// </summary>
    ModelAnswer? Read(JsonElement answer);

    /// <summary>
    /// What an answer with an error status means: the provider's answer to a wrong key, its own
    /// word that it has no such model, or anything else, which is <see cref="AiCallOutcome.Failed"/>.
    /// </summary>
    /// <param name="status">The status of the answer.</param>
    /// <param name="error">The body of the answer, or null when it is not a JSON object.</param>
    AiCallOutcome OutcomeOfError(HttpStatusCode status, JsonElement? error);
}

/// <summary>How a model's answer ended.</summary>
internal enum AnswerEnd
{
    /// <summary>The model finished it.</summary>
    Complete,

    /// <summary>It was cut short: at a limit on its tokens, or at the end of what the model can hold.</summary>
    Truncated,

    /// <summary>The model, or a filter of the provider, would not answer.</summary>
    Refused,

    /// <summary>It ended in a way that this code does not know, which is then not taken for a finished answer.</summary>
    Other,
}

/// <summary>An answer of a provider, read.</summary>
/// <param name="End">How it ended.</param>
/// <param name="Text">The text the model wrote, which is the structured result when the answer is complete.</param>
/// <param name="Usage">The tokens the provider counted, or null when the answer does not say.</param>
/// <param name="ProviderReason">The provider's own word for how it ended, such as <c>max_tokens</c>, for the log.</param>
internal readonly record struct ModelAnswer(AnswerEnd End, string? Text, AiTokenUsage? Usage, string? ProviderReason);

/// <summary>Reads the parts of an answer, each of which may be missing or of another kind than expected.</summary>
internal static class AnswerReading
{
    /// <summary>The member of an object, or null when this is no object or has no such member.</summary>
    public static JsonElement? Member(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var member) ? member : null;

    /// <inheritdoc cref="Member(JsonElement, string)"/>
    public static JsonElement? Member(this JsonElement? element, string name) => element?.Member(name);

    /// <summary>The text of a member, or null when it is missing or is no text.</summary>
    public static string? Text(this JsonElement element, string name) =>
        element.Member(name) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    /// <inheritdoc cref="Text(JsonElement, string)"/>
    public static string? Text(this JsonElement? element, string name) => element?.Text(name);

    /// <summary>The count a member holds, or null when it is missing or is no whole number.</summary>
    public static int? Count(this JsonElement? element, string name) =>
        element.Member(name) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var count) ? count : null;

    /// <summary>The items of a member that is a list, or none when it is missing or is no list.</summary>
    public static IEnumerable<JsonElement> Items(this JsonElement element, string name) =>
        element.Member(name) is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray() : [];
}
