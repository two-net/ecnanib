using System.Net;
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
/// <para>
/// Before anything is sent the breakers are asked, and they are told how the call ended
/// (NFR-REL-4). A call that they hold back is not made, and ends as paused.
/// </para>
/// </remarks>
internal sealed class ModelCall(
    IHttpClientFactory clients,
    AiProvidersSettings settings,
    AiCallBreakers breakers,
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

        // The calls to the provider, or the calls with this key, may be paused. Nothing is sent
        // then, and no time is taken.
        var pass = breakers.Enter(format.Provider, apiKey);
        if (pass.Paused is { } paused)
        {
            return Without(paused, TimeSpan.Zero);
        }

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

            var body = await response.ReadObjectAsync(either.Token);
            var latency = clock.GetElapsedTime(started);

            if (!response.IsSuccessStatusCode)
            {
                var outcome = format.OutcomeOfError(response.StatusCode, body);

                return breakers.Leave(pass, Without(outcome, latency), EndOf(outcome, response.StatusCode));
            }

            // A success that is not the provider's answer: the page of something in between, or
            // an answer of a kind that this code does not know.
            if (body is not { } answer || format.Read(answer) is not { } read)
            {
                return breakers.Leave(pass, Without(AiCallOutcome.Failed, latency), CallEnd.ProviderFault);
            }

            if (read.End == AnswerEnd.Complete && IsJsonObject(read.Text))
            {
                // The model was free to search and did not, or the provider counts its searches
                // where this code does not look. Either way the result rests on no live data
                // that anyone can show, and its caller treats it so (FR-CYC-3).
                if (request.LiveSearch && read.LiveSearches == 0)
                {
                    logger.LogWarning("The model was offered a live search, and its answer shows none");
                }

                return breakers.Leave(
                    pass, new AiCallResult(AiCallOutcome.Succeeded, read.Text, read.Usage, latency, read.LiveSearches), CallEnd.Healthy);
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

            // The searches ran, and are charged for, as the tokens are. The provider answered
            // the key: that the model gave no result is no failure of either.
            return breakers.Leave(
                pass, Without(AiCallOutcome.InvalidResult, latency, read.Usage, read.LiveSearches), CallEnd.Healthy);
        }
        catch (HttpRequestException)
        {
            // No answer. The call is in the log of outbound calls, with why.
            return breakers.Leave(pass, Without(AiCallOutcome.Failed, clock.GetElapsedTime(started)), CallEnd.ProviderFault);
        }
        catch (IOException)
        {
            // The answer broke off while it was read.
            return breakers.Leave(pass, Without(AiCallOutcome.Failed, clock.GetElapsedTime(started)), CallEnd.ProviderFault);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The provider took longer than its limit. The caller did not cancel.
            return breakers.Leave(pass, Without(AiCallOutcome.TimedOut, clock.GetElapsedTime(started)), CallEnd.ProviderFault);
        }

        // A caller that cancels ends the call with its error, and the breakers hear nothing of
        // it: a call that was not waited for says nothing about the provider or the key.
    }

    /// <summary>
    /// How a call that the provider answered with an error counts for the breakers. A refusal of
    /// the key and a model that is not there have a handling of their own, and say that the
    /// provider answers. A fault that the provider reports of itself, or a time limit of its own
    /// that ran out, is a fault of the provider. Any other refusal is of the calls with this key:
    /// a limit on its requests, an account that has run out, or a request that is not taken.
    /// </summary>
    private static CallEnd EndOf(AiCallOutcome outcome, HttpStatusCode status)
    {
        if (outcome is AiCallOutcome.AuthenticationFailed or AiCallOutcome.ModelNotFound)
        {
            return CallEnd.Answered;
        }

        var refused = (int)status is >= 400 and < 500 && status != HttpStatusCode.RequestTimeout;

        return refused ? CallEnd.KeyRefused : CallEnd.ProviderFault;
    }

    /// <summary>A call without a result. Only an answer that was read says its usage and its searches.</summary>
    private static AiCallResult Without(
        AiCallOutcome outcome, TimeSpan latency, AiTokenUsage? usage = null, int? liveSearches = null) =>
        new(outcome, ResultJson: null, usage, latency, liveSearches);

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
