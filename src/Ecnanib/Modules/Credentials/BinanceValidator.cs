using Ecnanib.Kernel;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.ExchangeAdapter.Contracts;

namespace Ecnanib.Modules.Credentials;

/// <summary>
/// Validates a Binance key with authenticated account requests (FR-KEY-04), and reads its permissions
/// where Binance exposes them (FR-KEY-02, FR-KEY-05). See Credentials in docs/architecture.md.
/// </summary>
/// <remarks>
/// Binance documents a key's restrictions only on its Live wallet API, so a Demo key's are read from its
/// accounts alone: each market is enabled when its account answers. A market counts as enabled only when
/// a signed request to it succeeded, which also settles whether Futures takes an Ed25519 key.
/// </remarks>
internal sealed class BinanceValidator(IExchangeAccount account, ILogger<BinanceValidator> logger)
{
    public async Task<Verdict> ValidateAsync(
        Guid credentialId,
        TradingEnvironment environment,
        BinanceKeyType type,
        SecretValue apiKey,
        SecretValue secret,
        CancellationToken cancellationToken)
    {
        var key = new ExchangeKey(
            credentialId,
            environment,
            type == BinanceKeyType.Ed25519 ? ExchangeKeyType.Ed25519 : ExchangeKeyType.Hmac,
            apiKey,
            secret);

        try
        {
            return environment == TradingEnvironment.Live
                ? await LiveAsync(key, cancellationToken)
                : await DemoAsync(key, cancellationToken);
        }
        catch (ArgumentException)
        {
            // The signer couldn't import the Ed25519 private key, so nothing was sent.
            return Verdict.Refused;
        }
        catch (ExchangeException exception)
        {
            // The adapter logged the call already, with its status and code.
            logger.LogInformation(
                "Binance couldn't validate credential {CredentialId}: {Failure}", credentialId, exception.Failure);

            return Verdict.Unreachable;
        }
    }

    private async Task<Verdict> LiveAsync(ExchangeKey key, CancellationToken cancellationToken)
    {
        ApiRestrictions restrictions;

        try
        {
            restrictions = await account.GetApiRestrictionsAsync(key, cancellationToken);
        }
        catch (ExchangeException exception) when (exception.Failure == ExchangeFailure.Rejected)
        {
            return Verdict.Refused;
        }

        if (restrictions.Withdrawals)
        {
            return new Verdict(
                Reached: true, CredentialStatus.Invalid, CredentialProblem.WithdrawalsEnabled, IpRestricted: restrictions.IpRestricted);
        }

        var spot = await ReadAsync(Market.Spot, key, cancellationToken);
        var futures = restrictions.Futures ? await ReadAsync(Market.Futures, key, cancellationToken) : null;

        return Conclude(
            spotEnabled: restrictions.SpotAndMarginTrading && spot?.CanTrade == true,
            futuresEnabled: futures is not null,
            restrictions.IpRestricted,
            spot?.Uid);
    }

    private async Task<Verdict> DemoAsync(ExchangeKey key, CancellationToken cancellationToken)
    {
        var spot = await ReadAsync(Market.Spot, key, cancellationToken);
        var futures = await ReadAsync(Market.Futures, key, cancellationToken);

        if (spot is null && futures is null)
        {
            return Verdict.Refused;
        }

        return Conclude(spotEnabled: spot?.CanTrade == true, futuresEnabled: futures is not null, ipRestricted: null, spot?.Uid);
    }

    /// <summary>The account on one market, or null when Binance refused the key there.</summary>
    private async Task<ExchangeAccount?> ReadAsync(Market market, ExchangeKey key, CancellationToken cancellationToken)
    {
        try
        {
            return await account.GetAccountAsync(market, key, cancellationToken);
        }
        catch (ExchangeException exception) when (exception.Failure == ExchangeFailure.Rejected)
        {
            return null;
        }
    }

    private static Verdict Conclude(bool spotEnabled, bool futuresEnabled, bool? ipRestricted, long? uid) =>
        spotEnabled || futuresEnabled
            ? new Verdict(Reached: true, CredentialStatus.Valid, null, spotEnabled, futuresEnabled, ipRestricted, uid)
            : new Verdict(
                Reached: true, CredentialStatus.InsufficientPermissions, CredentialProblem.NoTradingPermission, false, false, ipRestricted, uid);
}
