using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.Platform.Settings;

namespace Ecnanib.AiProviderGateway.Adapters;

/// <summary>
/// The one exchange that every provider's adapter makes for a call to a model: a POST to the
/// provider's address of the configuration file, with the key in a header, within the provider's
/// time limit of that file. What differs between providers each adapter says, as an
/// <see cref="IModelCallFormat"/>. See docs/ai-providers.md.
/// </summary>
/// <remarks>
/// <para>
/// What happens on the network leaves here as an <see cref="AiCallOutcome"/>, and never as an
/// error of the network, which nothing outside an adapter may name. Only a caller that cancels
/// ends the call with an error.
/// </para>
/// <para>
/// The key is put into the headers of the one request and is kept nowhere else. Nothing of an
/// answer's text reaches a log event from here: a provider's error may repeat parts of a key, and
/// bodies are for the debug level of the log of outbound calls (NFR-LOG-5).
/// </para>
/// <para>
/// The call is made once. Whether it is made again is the caller's to decide (FR-TEAM-9).
/// </para>
/// </remarks>
internal sealed class ModelCall(
    IHttpClientFactory clients,
    AiProvidersSettings settings,
    TimeProvider clock,
    ILogger<ModelCall> logger)
{
    // The longest word of a provider that is logged as it is. A longer one is no word.
    private const int LongestWord = 64;

    /// <summary>Makes the call, and says how it ended.</summary>
    /// <param name="format">The provider's own way of asking and answering.</param>
    /// <param name="request">The model, the prompt, and the schema of the result.</param>
    /// <param name="apiKey">The user's key for the provider.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentException">The schema of the request is not a JSON object.</exception>
    /// <exception cref="InvalidOperationException">The configuration file has no entry for the provider.</exception>
    public async Task<AiCallResult> AskAsync(
        IModelCallFormat format,
        AiCallRequest request,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (!settings.ByCode.TryGetValue(format.Provider, out var endpoint))
        {
            throw new InvalidOperationException($"The configuration file has no entry for AI provider '{format.Provider}'.");
        }

        var schema = SchemaOf(request);

        // The one limit on the call: the client of the gateway has none of its own. It runs on
        // the application's clock, and covers the sending and the reading of the answer.
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(endpoint.TimeoutSeconds), clock);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);

        var started = clock.GetTimestamp();

        try
        {
            using var message = new HttpRequestMessage(
                HttpMethod.Post, $"{endpoint.BaseUrl.AbsoluteUri.TrimEnd('/')}/{format.PathOf(request.Model)}");
            message.Content = new StringContent(format.BodyOf(request, schema).ToJsonString(), Encoding.UTF8, "application/json");
            format.Authorize(message.Headers, apiKey);

            var client = clients.CreateClient(AiProviderGatewayModule.HttpClientName);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, either.Token);

            var body = await ReadAsync(response, either.Token);
            var latency = clock.GetElapsedTime(started);

            if (!response.IsSuccessStatusCode)
            {
                return Without(format.OutcomeOfError(response.StatusCode, body), latency);
            }

            // A success that is not the provider's answer: the page of something in between, or
            // an answer of a kind that this code does not know.
            if (body is not { } answer || format.Read(answer) is not { } read)
            {
                return Without(AiCallOutcome.Failed, latency);
            }

            if (read.End == AnswerEnd.Complete && IsJsonObject(read.Text))
            {
                return new AiCallResult(AiCallOutcome.Succeeded, read.Text, read.Usage, latency);
            }

            // The status says that the call went well, so the log of outbound calls does too.
            // This says why there is no result all the same.
            if (WordOf(read.ProviderReason) is { } word)
            {
                logger.LogWarning(
                    "The answer of the model holds no structured result: {Reason}, which the provider calls {ProviderReason}",
                    WhyNot(read),
                    word);
            }
            else
            {
                logger.LogWarning("The answer of the model holds no structured result: {Reason}", WhyNot(read));
            }

            return Without(AiCallOutcome.InvalidResult, latency, read.Usage);
        }
        catch (HttpRequestException)
        {
            // No answer. The call is in the log of outbound calls, with why.
            return Without(AiCallOutcome.Failed, clock.GetElapsedTime(started));
        }
        catch (IOException)
        {
            // The answer broke off while it was read.
            return Without(AiCallOutcome.Failed, clock.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The provider took longer than its limit. The caller did not cancel.
            return Without(AiCallOutcome.TimedOut, clock.GetElapsedTime(started));
        }
    }

    private static AiCallResult Without(AiCallOutcome outcome, TimeSpan latency, AiTokenUsage? usage = null) =>
        new(outcome, ResultJson: null, usage, latency);

    /// <summary>The schema of a request, which is a JSON object. It is read anew for each call: a node belongs to one body.</summary>
    private static JsonNode SchemaOf(AiCallRequest request)
    {
        try
        {
            if (JsonNode.Parse(request.ResultSchema) is JsonObject schema)
            {
                return schema;
            }
        }
        catch (JsonException)
        {
            // Said below, without the text.
        }

        throw new ArgumentException("The schema of the result must be a JSON object.", nameof(request));
    }

    /// <summary>The body of an answer as the JSON object that a provider sends, or null when it is something else.</summary>
    private static async Task<JsonElement?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var answer = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

            return answer.RootElement.ValueKind == JsonValueKind.Object ? answer.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var result = JsonDocument.Parse(text);

            return result.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Why an answer is no result, in a word of this code's own.</summary>
    private static string WhyNot(ModelAnswer answer) => answer.End switch
    {
        AnswerEnd.Refused => "Refused",
        AnswerEnd.Truncated => "Truncated",
        AnswerEnd.Other => "NotFinished",
        _ => string.IsNullOrWhiteSpace(answer.Text) ? "Empty" : "NotJson",
    };

    /// <summary>
    /// A provider's word for how an answer ended, such as <c>max_tokens</c>, or null when what it
    /// sent there is no single short word: text of an answer is not for the log.
    /// </summary>
    private static string? WordOf(string? reason) =>
        reason is { Length: > 0 and <= LongestWord } && reason.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '_' or '-' or '.')
            ? reason
            : null;
}
