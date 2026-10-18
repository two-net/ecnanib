using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Ai.Configuration;
using Ecnanib.Ai.Resilience;
using Microsoft.Extensions.Options;

namespace Ecnanib.Ai.Providers;

/// <summary>
/// The pipeline every provider client runs: provider guard, output-token cap, one JSON exchange per attempt with the
/// body read once, error translation with the key scrubbed from provider text, latency measurement, one log line per
/// attempt (FR-LOG-04: provider, model, status, latency, tokens; never prompt content or key) and the retry policy
/// (FR-AI-13). The key is read only by <see cref="ApplyAuth"/> and travels only in that provider's auth header.
/// </summary>
internal abstract class AiProviderClientBase : IAiProviderClient
{
    public const string HttpClientName = "Ecnanib.Ai";

    protected const string ListOperation = "list models";
    protected const string CompleteOperation = "complete";
    protected const int MaxPages = 50;

    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IAiErrorBodyParser _errorParser;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<AiOptions> _options;
    private readonly AiRetryPolicy _retry;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    protected AiProviderClientBase(
        AiProvider provider,
        IAiErrorBodyParser errorParser,
        IHttpClientFactory httpClientFactory,
        IOptions<AiOptions> options,
        AiRetryPolicy retry,
        TimeProvider time,
        ILogger logger)
    {
        Provider = provider;
        _errorParser = errorParser;
        _httpClientFactory = httpClientFactory;
        _options = options;
        _retry = retry;
        _time = time;
        _logger = logger;
    }

    public AiProvider Provider { get; }

    protected AiOptions Options => _options.Value;

    protected AiEndpointOptions Endpoint => Options.EndpointsFor(Provider);

    protected TimeProvider Time => _time;

    protected IAiErrorBodyParser ErrorParser => _errorParser;

    public Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(AiKeyHandle key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        EnsureProvider(key);

        return _retry.ExecuteAsync(async token =>
        {
            var start = _time.GetTimestamp();
            try
            {
                var models = await ListModelsCoreAsync(key, token).ConfigureAwait(false);
                LogSuccess(key, ListOperation, null, null, $"{models.Count} models", null, _time.GetElapsedTime(start));
                return models;
            }
            catch (AiException ex)
            {
                LogFailure(key, ListOperation, null, null, ex, _time.GetElapsedTime(start));
                throw;
            }
        }, ListOperation, ct);
    }

    public Task<AiCompletion> CompleteAsync(AiKeyHandle key, AiRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(request);
        EnsureProvider(key);
        var maxOutputTokens = Math.Min(request.MaxOutputTokens ?? Options.MaxOutputTokens, Options.MaxOutputTokens);

        return _retry.ExecuteAsync(async token =>
        {
            var start = _time.GetTimestamp();
            try
            {
                var completion = await CompleteCoreAsync(key, request, maxOutputTokens, token).ConfigureAwait(false);
                var latency = _time.GetElapsedTime(start);
                completion = completion with { Latency = latency };
                LogSuccess(key, CompleteOperation, request.Model, request.Purpose, completion.FinishReason.ToString(), completion.Usage, latency);
                return completion;
            }
            catch (AiException ex)
            {
                LogFailure(key, CompleteOperation, request.Model, request.Purpose, ex, _time.GetElapsedTime(start));
                throw;
            }
        }, CompleteOperation, ct);
    }

    /// <summary>Puts the key into this provider's auth header. The only place the key value is read.</summary>
    protected abstract void ApplyAuth(HttpRequestMessage message, AiKeyHandle key);

    protected abstract Task<IReadOnlyList<AiModelInfo>> ListModelsCoreAsync(AiKeyHandle key, CancellationToken ct);

    /// <summary>Returns the completion with <see cref="AiCompletion.Latency"/> left at zero; the base fills it in.</summary>
    protected abstract Task<AiCompletion> CompleteCoreAsync(AiKeyHandle key, AiRequest request, int maxOutputTokens, CancellationToken ct);

    /// <summary>
    /// One HTTP exchange. Transport failures become <see cref="AiUnavailableException"/>, a timeout of this attempt
    /// (the caller's token not cancelled) counts as transient, non-2xx goes through the translator with the key
    /// scrubbed from the body, and a 2xx body that is empty or not the expected JSON is an invalid response.
    /// </summary>
    protected async Task<TResponse> SendAsync<TResponse>(AiKeyHandle key, HttpMethod method, string relativePath, object? body, string operation, string? model, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, BuildUri(relativePath));
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        ApplyAuth(request, key);

        HttpResponseMessage response;
        try
        {
            response = await _httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw Transport(key, operation, model, AiErrorKind.Network, ex);
        }
        catch (IOException ex)
        {
            throw Transport(key, operation, model, AiErrorKind.Network, ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw Transport(key, operation, model, AiErrorKind.Timeout, ex);
        }

        using (response)
        {
            string responseBody;
            try
            {
                responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw Transport(key, operation, model, AiErrorKind.Network, ex);
            }
            catch (IOException ex)
            {
                throw Transport(key, operation, model, AiErrorKind.Network, ex);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw Transport(key, operation, model, AiErrorKind.Timeout, ex);
            }

            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                throw AiErrorTranslator.Translate(
                    Provider, _errorParser, operation, model, response.StatusCode, response.ReasonPhrase, response.Headers,
                    key.Credentials.Scrub(responseBody), _time.GetUtcNow());
            }

            var requestId = AiErrorTranslator.RequestId(response.Headers);
            if (string.IsNullOrWhiteSpace(responseBody))
                throw new AiRequestException($"{Provider} {operation} failed: empty response body (HTTP {status})", Provider, AiErrorKind.InvalidResponse, status, model: model, requestId: requestId);

            try
            {
                return JsonSerializer.Deserialize<TResponse>(responseBody, Json)
                    ?? throw new AiRequestException($"{Provider} {operation} failed: response body is null (HTTP {status})", Provider, AiErrorKind.InvalidResponse, status, model: model, requestId: requestId);
            }
            catch (JsonException ex)
            {
                throw new AiRequestException($"{Provider} {operation} failed: response is not the expected JSON ({ex.Message})", Provider, AiErrorKind.InvalidResponse, status, model: model, requestId: requestId, innerException: ex);
            }
        }
    }

    protected Uri BuildUri(string relativePath) => new(Endpoint.BaseUrl.TrimEnd('/') + "/" + relativePath.TrimStart('/'));

    protected AiRequestException PaginationLoop(string operation) =>
        new($"{Provider} {operation} failed: model listing did not terminate after {MaxPages} pages", Provider, AiErrorKind.InvalidResponse);

    protected AiTokenUsage UsageOrWarn(AiTokenUsage? usage, string model)
    {
        if (usage is not null)
            return usage;

        _logger.LogWarning("AI {Provider} returned no usage for model {Model}; recording zero tokens", Provider, model);
        return AiTokenUsage.None;
    }

    protected static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    protected static DateTimeOffset? FromUnixSeconds(long? value) =>
        value is { } seconds && seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private void EnsureProvider(AiKeyHandle key)
    {
        if (key.Provider != Provider)
            throw new ArgumentException($"Key {key.KeyId} belongs to {key.Provider} but was passed to the {Provider} client.", nameof(key));
    }

    private AiUnavailableException Transport(AiKeyHandle key, string operation, string? model, AiErrorKind kind, Exception inner)
    {
        var detail = kind == AiErrorKind.Timeout
            ? $"no response within {Options.CallTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s"
            : key.Credentials.Scrub(inner.Message);
        return new AiUnavailableException($"{Provider} {operation} failed: {detail}", Provider, kind, model: model, innerException: inner);
    }

    private void LogSuccess(AiKeyHandle key, string operation, string? model, string? purpose, string status, AiTokenUsage? usage, TimeSpan latency) =>
        _logger.LogInformation(
            "AI {Provider} {Operation} {Purpose} {Model} {Status} in {LatencyMs} ms, tokens {InputTokens}/{OutputTokens} (key {KeyId} {MaskedApiKey})",
            Provider, operation, purpose ?? "-", model ?? "-", status, (long)latency.TotalMilliseconds,
            usage?.InputTokens ?? 0, usage?.OutputTokens ?? 0, key.KeyId, key.Credentials.MaskedApiKey);

    private void LogFailure(AiKeyHandle key, string operation, string? model, string? purpose, AiException ex, TimeSpan latency) =>
        _logger.Log(
            ex.IsTransient ? LogLevel.Warning : LogLevel.Error,
            ex,
            "AI {Provider} {Operation} {Purpose} {Model} failed with {Kind} (HTTP {StatusCode}, {ProviderCode}) after {LatencyMs} ms (key {KeyId} {MaskedApiKey})",
            Provider, operation, purpose ?? "-", model ?? "-", ex.Kind, ex.StatusCode, ex.ProviderCode ?? "-", (long)latency.TotalMilliseconds,
            key.KeyId, key.Credentials.MaskedApiKey);
}
