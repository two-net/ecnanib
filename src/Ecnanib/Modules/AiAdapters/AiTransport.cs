using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;

namespace Ecnanib.Modules.AiAdapters;

internal enum AiOperation
{
    Complete,
    ListModels,
}

/// <summary>One call, as it's limited and logged.</summary>
/// <param name="Model">Null for a model list.</param>
/// <param name="Role">Null for a model list.</param>
internal sealed record AiCall(AiKey Key, AiOperation Operation, string? Model, AiRole? Role, AiModelPrice? Price);

/// <summary>What an adapter read from a successful body.</summary>
internal sealed record Parsed<T>(T Value, AiUsage Usage, bool Refused);

/// <summary>A call that succeeded, with every attempt's tokens and cost added up.</summary>
internal sealed record Sent<T>(T Value, AiUsage Usage, decimal? Cost, int Attempts, TimeSpan Latency);

/// <summary>
/// The one way to an AI provider. Every call waits for a slot on its credential, is sent to the configured
/// base URL with its own timeout, has its failure normalized, is retried with backoff, and is logged once
/// per attempt. Adapters only build requests and read answers. See AI adapters in docs/architecture.md.
/// </summary>
internal sealed partial class AiTransport
{
    /// <summary>The named client, which follows no redirects, keeps no cookies and logs nothing of its own.</summary>
    public const string ClientName = "AiProviders";

    /// <summary>The name SRS 7.3 gives AI calls' log events.</summary>
    public const string Category = "AiCouncil";

    /// <summary>
    /// A longer Retry-After isn't waited for: the council's fallback model is a better use of a cycle's
    /// deadline (FR-ROLE-06). A backoff is capped at this too.
    /// </summary>
    public static readonly TimeSpan MaximumRetryWait = TimeSpan.FromSeconds(60);

    /// <summary>Enough to explain a failure in a decision trace.</summary>
    private const int MaximumProviderMessageLength = 500;

    private readonly IHttpClientFactory _clients;
    private readonly CredentialGates _gates;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Dictionary<string, (IAiProviderAdapter Adapter, AiProviderOptions Options)> _providers;

    public AiTransport(
        AiProviderSettings settings,
        IEnumerable<IAiProviderAdapter> adapters,
        IHttpClientFactory clients,
        CredentialGates gates,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        var byName = adapters.ToDictionary(adapter => adapter.Name, StringComparer.Ordinal);

        _providers = settings.Providers.ToDictionary(options => options.Name, options => (byName[options.Name], options), StringComparer.Ordinal);
        _clients = clients;
        _gates = gates;
        _time = time;
        _logger = loggerFactory.CreateLogger(Category);
        Providers = [.. settings.Providers.Select(options => new AiProviderInfo(options.Name, options.Enabled))];
    }

    public IReadOnlyList<AiProviderInfo> Providers { get; }

    /// <summary>Sends one call, retrying it per its provider's settings.</summary>
    /// <param name="build">Builds the request, revealing the key. Runs once per attempt, inside the credential's slot.</param>
    /// <param name="read">Reads a successful body. Anything it throws means the body can't be read.</param>
    /// <exception cref="ArgumentException">No adapter has the key's provider name.</exception>
    /// <exception cref="InvalidOperationException">The adapter gave an absolute URI, so nothing was sent.</exception>
    /// <exception cref="AiException">It failed after its retries.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task<Sent<T>> SendAsync<T>(
        AiCall call,
        Func<IAiProviderAdapter, HttpRequestMessage> build,
        Func<IAiProviderAdapter, byte[], Parsed<T>> read,
        CancellationToken cancellationToken)
    {
        if (!_providers.TryGetValue(call.Key.Provider, out var provider))
        {
            throw new ArgumentException($"No AI provider adapter is named {call.Key.Provider}.", nameof(call));
        }

        var (adapter, options) = provider;

        if (!options.Enabled)
        {
            _logger.LogWarning(
                "AI {Provider} {Operation} {Model} for {Role} wasn't sent, because the provider is disabled in the configuration",
                options.Name,
                call.Operation,
                call.Model,
                call.Role);

            throw new AiException(options.Name, AiFailure.Other, $"{options.Name} is disabled in the configuration, so {call.Operation} wasn't sent.");
        }

        var started = Stopwatch.GetTimestamp();
        var usage = AiUsage.Zero;
        decimal? cost = call.Price is null ? null : 0m;

        for (var attempt = 1; ; attempt++)
        {
            var outcome = await AttemptAsync(call, adapter, options, attempt, build, read, cancellationToken);
            usage += outcome.Usage;
            cost += outcome.Cost;

            if (outcome.Failure is not { } failure)
            {
                return new Sent<T>(outcome.Value!, usage, cost, attempt, Stopwatch.GetElapsedTime(started));
            }

            var wait = failure.Retryable && attempt <= options.MaxRetries
                ? RetryWait(failure.RetryAfter, attempt, options.RetryBackoff)
                : (TimeSpan?)null;

            if (wait is null || wait > MaximumRetryWait)
            {
                throw new AiException(options.Name, failure.Failure, failure.Message, failure.Inner)
                {
                    StatusCode = failure.StatusCode,
                    ErrorCode = failure.ErrorCode,
                    ProviderMessage = failure.ProviderMessage,
                    RetryAfter = failure.RetryAfter,
                    Usage = usage,
                    Cost = cost,
                    Attempts = attempt,
                };
            }

            await Task.Delay(wait.Value, _time, cancellationToken);
        }
    }

    private async Task<Attempt<T>> AttemptAsync<T>(
        AiCall call,
        IAiProviderAdapter adapter,
        AiProviderOptions options,
        int attempt,
        Func<IAiProviderAdapter, HttpRequestMessage> build,
        Func<IAiProviderAdapter, byte[], Parsed<T>> read,
        CancellationToken cancellationToken)
    {
        // Held for this attempt only, and given back before any backoff, so a retry doesn't block others.
        using var slot = await _gates.EnterAsync(options.Name, call.Key.CredentialId, options.MaxConcurrentPerCredential, cancellationToken);
        using var request = build(adapter);
        request.RequestUri = Resolve(options, request.RequestUri);

        using var deadline = new CancellationTokenSource(options.Timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var sent = Stopwatch.GetTimestamp();
        HttpResponseMessage? response = null;
        byte[] body;

        try
        {
            response = await _clients.CreateClient(ClientName).SendAsync(request, linked.Token);
            body = await response.Content.ReadAsByteArrayAsync(linked.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            Log(LogLevel.Information, call, options, attempt, "Cancelled", null, Stopwatch.GetElapsedTime(sent), AiUsage.Zero, null, null);

            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            response?.Dispose();

            return Fail<T>(call, options, attempt, Stopwatch.GetElapsedTime(sent), new AttemptFailure(
                AiFailure.Timeout,
                Retryable: true,
                $"{options.Name} didn't answer {call.Operation} within {options.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s."));
        }
        catch (Exception exception)
        {
            // A broken connection, a body too large to buffer, or one that can't be decoded. The message
            // names at most the host, never the path or a header.
            response?.Dispose();

            return Fail<T>(call, options, attempt, Stopwatch.GetElapsedTime(sent), new AttemptFailure(
                AiFailure.Other,
                Retryable: true,
                $"{options.Name} {call.Operation} got no usable answer.",
                Inner: exception));
        }

        using (response)
        {
            return Handle(call, adapter, options, attempt, response!, body, Stopwatch.GetElapsedTime(sent), read);
        }
    }

    private Attempt<T> Handle<T>(
        AiCall call,
        IAiProviderAdapter adapter,
        AiProviderOptions options,
        int attempt,
        HttpResponseMessage response,
        byte[] body,
        TimeSpan latency,
        Func<IAiProviderAdapter, byte[], Parsed<T>> read)
    {
        var status = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            Parsed<T> parsed;

            try
            {
                parsed = read(adapter, body);
            }
            catch (Exception exception)
            {
                // Only the type: the message of an adapter's exception could quote the body, and that's
                // the model's answer, which belongs in the decision trace (FR-LOG-07). A format this
                // version can't read won't change on a retry.
                return Fail<T>(call, options, attempt, latency, new AttemptFailure(
                    AiFailure.Other,
                    Retryable: false,
                    $"{options.Name} returned a {call.Operation} answer this version can't read ({exception.GetType().Name}).",
                    status));
            }

            var cost = call.Price?.CostOf(parsed.Usage);

            if (parsed.Refused)
            {
                return Fail<T>(
                    call,
                    options,
                    attempt,
                    latency,
                    new AttemptFailure(AiFailure.Refusal, Retryable: true, $"{options.Name} declined to answer.", status),
                    parsed.Usage,
                    cost);
            }

            Log(LogLevel.Information, call, options, attempt, "Success", status, latency, parsed.Usage, cost, null);

            return new Attempt<T>(parsed.Value, parsed.Usage, cost, null);
        }

        AiProviderError? error;

        try
        {
            error = body.Length > 0 ? adapter.ReadError(status, body) : null;
        }
        catch (Exception)
        {
            // The status still says enough.
            error = null;
        }

        var failure = error?.Failure ?? FromStatus(status);
        var code = PlainCode(error?.Code);

        return Fail<T>(call, options, attempt, latency, new AttemptFailure(
            failure,
            Retryable(failure, status),
            $"{options.Name} answered {call.Operation} with HTTP {status.ToString(CultureInfo.InvariantCulture)}: {failure}{(code is null ? "" : $" ({code})")}.",
            status,
            code,
            Shorten(error?.Message),
            RetryAfter(response)));
    }

    private Attempt<T> Fail<T>(
        AiCall call,
        AiProviderOptions options,
        int attempt,
        TimeSpan latency,
        AttemptFailure failure,
        AiUsage? usage = null,
        decimal? cost = null)
    {
        var billed = usage ?? AiUsage.Zero;
        var charged = cost ?? call.Price?.CostOf(billed);

        Log(
            failure.Failure == AiFailure.Other ? LogLevel.Error : LogLevel.Warning,
            call,
            options,
            attempt,
            failure.Failure.ToString(),
            failure.StatusCode,
            latency,
            billed,
            charged,
            failure.ErrorCode);

        return new Attempt<T>(default, billed, charged, failure);
    }

    /// <summary>One event per attempt, which is one billed call (FR-LOG-07). Never a prompt, an answer, a key or a provider's message.</summary>
    private void Log(
        LogLevel level,
        AiCall call,
        AiProviderOptions options,
        int attempt,
        string outcome,
        int? status,
        TimeSpan latency,
        AiUsage usage,
        decimal? cost,
        string? code) =>
        _logger.Log(
            level,
            "AI {Provider} {Operation} {Model} for {Role}, attempt {Attempt}: {Outcome}, status {StatusCode} in {LatencyMs:0.0} ms, "
            + "{InputTokens} input and {OutputTokens} output tokens, cost {Cost}, code {ErrorCode}",
            options.Name,
            call.Operation,
            call.Model,
            call.Role,
            attempt,
            outcome,
            status,
            latency.TotalMilliseconds,
            usage.InputTokens,
            usage.OutputTokens,
            cost,
            code);

    /// <summary>
    /// The configured base URL in front of the adapter's path, so that only the configuration decides
    /// where a key goes (NFR-SEC-10). The original string is used because on Unix a string that starts
    /// with a slash parses as an absolute file URI.
    /// </summary>
    private static Uri Resolve(AiProviderOptions options, Uri? requestUri)
    {
        var authority = options.BaseUrl!.GetLeftPart(UriPartial.Authority);
        var path = requestUri?.OriginalString;

        if (path is null || !path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal)
            || !Uri.TryCreate(authority + path, UriKind.Absolute, out var resolved)
            || resolved.GetLeftPart(UriPartial.Authority) != authority)
        {
            throw new InvalidOperationException(
                $"The {options.Name} adapter must give a path that starts with a single /, so that only {AiProviderSettings.Section}:{options.Name}:BaseUrl decides where a request goes (NFR-SEC-10).");
        }

        return resolved;
    }

    /// <summary>The status alone, for when the adapter's error body says nothing more (EXT-AI-04).</summary>
    private static AiFailure FromStatus(int status) => status switch
    {
        401 or 403 => AiFailure.Authentication,
        400 or 404 or 413 or 422 => AiFailure.InvalidRequest,
        429 => AiFailure.RateLimit,
        503 or 529 => AiFailure.Overloaded,
        408 or 504 => AiFailure.Timeout,
        _ => AiFailure.Other,
    };

    /// <summary>
    /// What another attempt could fix. Other is only retried when the provider failed on its side: a
    /// redirect is never followed, and an unexpected 4XX would come back the same.
    /// </summary>
    private static bool Retryable(AiFailure failure, int status) => failure switch
    {
        AiFailure.RateLimit or AiFailure.Overloaded or AiFailure.Timeout or AiFailure.Refusal => true,
        AiFailure.Other => status >= 500,
        _ => false,
    };

    /// <summary>The provider's Retry-After when it gave one, otherwise an exponential backoff with equal jitter, capped.</summary>
    private static TimeSpan RetryWait(TimeSpan? retryAfter, int attempt, TimeSpan backoff)
    {
        if (retryAfter is { } asked && asked > TimeSpan.Zero)
        {
            return asked;
        }

        var full = TimeSpan.FromTicks(Math.Min(backoff.Ticks << (attempt - 1), MaximumRetryWait.Ticks));

        return (full / 2) + (full / 2 * Random.Shared.NextDouble());
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } when delta > TimeSpan.Zero => delta,
            { Date: { } date } when date > _time.GetUtcNow() => date - _time.GetUtcNow(),
            _ => null,
        };

    /// <summary>
    /// A provider's error code is logged, so only a plain token is kept, such as "rate_limit_error". Anything
    /// else could be text from the request.
    /// </summary>
    private static string? PlainCode(string? code) => code is not null && PlainCodePattern().IsMatch(code) ? code : null;

    private static string? Shorten(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var trimmed = message.Trim();

        return trimmed.Length > MaximumProviderMessageLength ? trimmed[..MaximumProviderMessageLength] : trimmed;
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainCodePattern();

    /// <summary>Why an attempt failed.</summary>
    /// <param name="Inner">For a broken connection, whose message names at most the host.</param>
    private sealed record AttemptFailure(
        AiFailure Failure,
        bool Retryable,
        string Message,
        int? StatusCode = null,
        string? ErrorCode = null,
        string? ProviderMessage = null,
        TimeSpan? RetryAfter = null,
        Exception? Inner = null);

    /// <summary>One attempt: its value, or why it failed, and what it was billed.</summary>
    private sealed record Attempt<T>(T? Value, AiUsage Usage, decimal? Cost, AttemptFailure? Failure);
}
