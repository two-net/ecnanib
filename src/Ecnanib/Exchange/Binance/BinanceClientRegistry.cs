using System.Collections.Concurrent;
using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Interfaces.Clients;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Configuration;
using Microsoft.Extensions.Options;

namespace Ecnanib.Exchange.Binance;

internal sealed class BinanceClientRegistry : IBinanceClientRegistry, IAsyncDisposable, IDisposable
{
    public const string HttpClientName = "Ecnanib.Exchange.Binance";

    private readonly IOptions<ExchangeOptions> _options;
    private readonly BinanceEnvironmentFactory _environments;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<BinanceClientRegistry> _logger;
    private readonly ConcurrentDictionary<ExchangeEnvironment, Lazy<PublicClients>> _public = new();
    private readonly ConcurrentDictionary<string, Lazy<KeyedClients>> _keyed = new(StringComparer.Ordinal);

    public BinanceClientRegistry(
        IOptions<ExchangeOptions> options,
        BinanceEnvironmentFactory environments,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _options = options;
        _environments = environments;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<BinanceClientRegistry>();
    }

    public IBinanceRestClient GetPublicRest(ExchangeEnvironment environment) => Public(environment).Rest;

    public IBinanceSocketClient GetPublicSocket(ExchangeEnvironment environment) => Public(environment).Socket;

    public IBinanceRestClient GetKeyedRest(ExchangeKeyHandle key) => Keyed(key).Rest;

    public IBinanceSocketClient GetKeyedSocket(ExchangeKeyHandle key) => Keyed(key).Socket;

    public bool HasKey(string keyId) => _keyed.ContainsKey(keyId);

    public async Task ReleaseKeyAsync(string keyId, CancellationToken ct)
    {
        if (!_keyed.TryRemove(keyId, out var lazy) || !lazy.IsValueCreated)
            return;

        await lazy.Value.DisposeAsync().ConfigureAwait(false);
        _logger.LogInformation("Released exchange clients for key {KeyId} ({MaskedApiKey})", keyId, lazy.Value.MaskedApiKey);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var keyId in _keyed.Keys.ToArray())
            await ReleaseKeyAsync(keyId, CancellationToken.None).ConfigureAwait(false);

        foreach (var entry in _public.Values.ToArray())
        {
            if (entry.IsValueCreated)
                await entry.Value.DisposeAsync().ConfigureAwait(false);
        }

        _public.Clear();
    }

    /// <summary>Synchronous disposal for containers that do not dispose asynchronously; sockets are closed by their Dispose.</summary>
    public void Dispose()
    {
        foreach (var keyId in _keyed.Keys.ToArray())
        {
            if (_keyed.TryRemove(keyId, out var lazy) && lazy.IsValueCreated)
                lazy.Value.Dispose();
        }

        foreach (var entry in _public.Values.ToArray())
        {
            if (entry.IsValueCreated)
                entry.Value.Dispose();
        }

        _public.Clear();
    }

    private PublicClients Public(ExchangeEnvironment environment) =>
        _public.GetOrAdd(environment, env => new Lazy<PublicClients>(() => CreatePublic(env))).Value;

    private KeyedClients Keyed(ExchangeKeyHandle key)
    {
        var clients = _keyed.GetOrAdd(key.KeyId, _ => new Lazy<KeyedClients>(() => CreateKeyed(key))).Value;
        if (clients.Environment != key.Environment)
        {
            throw new InvalidOperationException(
                $"Exchange key {key.KeyId} is cached for {clients.Environment} but was requested for {key.Environment}. Release the key first.");
        }

        if (!string.Equals(clients.ApiKey, key.Credentials.ApiKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Exchange key {key.KeyId} is cached with different credentials ({clients.MaskedApiKey}). Release the key before rotating it.");
        }

        return clients;
    }

    private PublicClients CreatePublic(ExchangeEnvironment environment)
    {
        var options = _options.Value;
        var env = _environments.Get(environment);
        var rest = new BinanceRestClient(
            _httpClientFactory.CreateClient(HttpClientName),
            _loggerFactory,
            Options.Create(BinanceOptionsBuilder.CreateRestOptions(options, env, null)));
        var socket = new BinanceSocketClient(
            Options.Create(BinanceOptionsBuilder.CreateSocketOptions(options, env, null)),
            _loggerFactory);
        _logger.LogInformation("Created keyless Binance clients for {Environment}", environment);
        return new PublicClients(rest, socket);
    }

    private KeyedClients CreateKeyed(ExchangeKeyHandle key)
    {
        var options = _options.Value;
        var env = _environments.Get(key.Environment);
        var credentials = new BinanceCredentials(key.Credentials.ApiKey, key.Credentials.ApiSecret);
        var rest = new BinanceRestClient(
            _httpClientFactory.CreateClient(HttpClientName),
            _loggerFactory,
            Options.Create(BinanceOptionsBuilder.CreateRestOptions(options, env, credentials)));
        var socket = new BinanceSocketClient(
            Options.Create(BinanceOptionsBuilder.CreateSocketOptions(options, env, credentials)),
            _loggerFactory);
        _logger.LogInformation(
            "Created Binance clients for key {KeyId} ({MaskedApiKey}) on {Environment}",
            key.KeyId, key.Credentials.MaskedApiKey, key.Environment);
        return new KeyedClients(key.Environment, key.Credentials.ApiKey, key.Credentials.MaskedApiKey, rest, socket);
    }

    private sealed record PublicClients(IBinanceRestClient Rest, IBinanceSocketClient Socket) : IAsyncDisposable, IDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Socket.UnsubscribeAllAsync().ConfigureAwait(false);
            Dispose();
        }

        public void Dispose()
        {
            Socket.Dispose();
            Rest.Dispose();
        }
    }

    private sealed record KeyedClients(
        ExchangeEnvironment Environment,
        string ApiKey,
        string MaskedApiKey,
        IBinanceRestClient Rest,
        IBinanceSocketClient Socket) : IAsyncDisposable, IDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Socket.UnsubscribeAllAsync().ConfigureAwait(false);
            Dispose();
        }

        public void Dispose()
        {
            Socket.Dispose();
            Rest.Dispose();
        }

        /// <summary>Never print the key.</summary>
        public override string ToString() => $"KeyedClients({Environment}, {MaskedApiKey})";
    }
}
