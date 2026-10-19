using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Keys.Services;

/// <summary>
/// Proves a Binance key pair with the exchange (FR-KEY-02): one authenticated request per market the key may be used
/// for, then the key's permissions. Production reports them through <c>/sapi/v1/account/apiRestrictions</c>; the
/// testnets have no such endpoint and answer <see cref="ApiKeyPermissions.Unverified"/>, so withdrawal permission can
/// only be refused on production (testnet funds are not real). The whole check is bounded by
/// <see cref="KeysOptions.ValidationTimeout"/>.
/// </summary>
internal sealed class ExchangeKeyValidator
{
    /// <summary>Validations run under a throwaway key id so they never touch the clients cached for a stored key.</summary>
    internal const string ScratchPrefix = "validate:";

    private readonly IExchangeClientFactory _exchange;
    private readonly IOptions<KeysOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ExchangeKeyValidator> _logger;

    public ExchangeKeyValidator(IExchangeClientFactory exchange, IOptions<KeysOptions> options, TimeProvider time, ILogger<ExchangeKeyValidator> logger)
    {
        _exchange = exchange;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task<ExchangeKeyValidation> ValidateAsync(ExchangeEnvironment environment, ExchangeCredentials credentials, bool allowSpot, bool allowFutures, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (!allowSpot && !allowFutures)
            throw new ArgumentException("At least one market must be allowed.", nameof(allowSpot));

        // The factory caches account clients per key id and refuses an id reused with other credentials, so a check
        // under the stored id could poison the clients a running agent holds. A scratch id is released in finally.
        var handle = new ExchangeKeyHandle(ScratchPrefix + Guid.CreateVersion7().ToString("N"), environment, credentials);
        var timeout = _options.Value.ValidationTimeout;
        using var limit = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            IExchangeAccountClient? client = null;
            if (allowSpot)
            {
                var spot = _exchange.GetSpotClient(handle);
                await spot.PingAsync(linked.Token).ConfigureAwait(false);
                client = spot;
            }

            if (allowFutures)
            {
                var futures = _exchange.GetFuturesClient(handle);
                await futures.PingAsync(linked.Token).ConfigureAwait(false);
                client ??= futures;
            }

            var permissions = await client!.GetApiKeyPermissionsAsync(linked.Token).ConfigureAwait(false);
            if (permissions.Verified && permissions.EnableWithdrawals)
                return new ExchangeKeyValidation(ValidationVerdict.Invalid, permissions, KeyReasons.WithdrawalEnabled, WithdrawalEnabled: true);

            return new ExchangeKeyValidation(ValidationVerdict.Valid, permissions);
        }
        catch (ExchangeAuthException ex)
        {
            return new ExchangeKeyValidation(ValidationVerdict.Invalid, Reason: ex.Message);
        }
        catch (ExchangeException ex)
        {
            return new ExchangeKeyValidation(ValidationVerdict.Failed, Reason: ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ExchangeKeyValidation(ValidationVerdict.Failed, Reason: KeyReasons.Timeout("Binance", timeout));
        }
        finally
        {
            await ReleaseQuietlyAsync(handle.KeyId).ConfigureAwait(false);
        }
    }

    private async Task ReleaseQuietlyAsync(string keyId)
    {
        try
        {
            await _exchange.ReleaseAsync(keyId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not release the exchange clients used by validation {KeyId}", keyId);
        }
    }
}
