using Ecnanib.Kernel;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// FR-RISK-09, right before a Futures entry: the account must be in One-way Mode and not in Multi-Assets Mode, and
/// the symbol is set to isolated at the entry's leverage. It reads first and changes only what differs, which spends
/// no change and logs no rejection when the symbol is already set up. See Futures account settings in
/// docs/architecture.md.
/// </summary>
internal sealed class FuturesSetup(
    ICredentialKeys keys,
    IExchangeAccount accounts,
    IExchangeTrading trading,
    ILogger<FuturesSetup> logger) : IFuturesSetup
{
    /// <summary>Binance's codes for a margin type change, from its Futures error code page.</summary>
    private static class MarginTypeCodes
    {
        public const int AlreadySet = -4046;
        public const int OpenOrders = -4047;
        public const int OpenPosition = -4048;
        public const int MultiAssetsMode = -4168;
    }

    /// <summary>Binance's codes for a leverage it won't set: invalid, lower than an open isolated position's, and above the symbol's maximum.</summary>
    private static readonly HashSet<int> LeverageRefusals = [-4028, -4161, -4209];

    public async Task<FuturesSetupOutcome> PrepareEntryAsync(
        Guid userId,
        Guid credentialId,
        string symbol,
        SizedOrder entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry is not { Market: Market.Futures, Kind: SizedOrderKind.Entry, Leverage: >= 1 })
        {
            throw new ArgumentException("Only a Futures entry with a leverage readies a symbol.", nameof(entry));
        }

        if (!OrderRequest.IsSymbol(symbol))
        {
            throw new ArgumentException("A symbol is upper-case letters and digits, such as BTCUSDT.", nameof(symbol));
        }

        var leverage = entry.Leverage.Value;
        ExchangeKey key;

        try
        {
            key = await keys.OpenExchangeKeyAsync(userId, credentialId, Market.Futures, cancellationToken);
        }
        catch (CredentialUnusableException exception)
        {
            logger.LogInformation("Can't ready {Symbol} through exchange key {CredentialId}: {Reason}", symbol, credentialId, exception.Reason);
            return FuturesSetupOutcome.KeyUnusable;
        }

        try
        {
            return await PrepareAsync(key, symbol, leverage, cancellationToken);
        }
        catch (ExchangeException exception)
        {
            logger.LogWarning(
                "Couldn't ready {Symbol} through exchange key {CredentialId}: {Failure}, code {ErrorCode}",
                symbol,
                credentialId,
                exception.Failure,
                exception.Code);

            return FuturesSetupOutcome.ExchangeUnavailable;
        }
        finally
        {
            key.ApiKey.Dispose();
            key.Secret.Dispose();
        }
    }

    private async Task<FuturesSetupOutcome> PrepareAsync(ExchangeKey key, string symbol, int leverage, CancellationToken cancellationToken)
    {
        var account = await accounts.GetFuturesAccountConfigAsync(key, cancellationToken);

        if (Refusal(account) is { } refusal)
        {
            logger.LogWarning("Won't ready {Symbol} through exchange key {CredentialId}: {Outcome}", symbol, key.CredentialId, refusal);
            return refusal;
        }

        var current = await accounts.GetFuturesSymbolConfigAsync(key, symbol, cancellationToken);

        if (current?.MarginType != FuturesMarginType.Isolated && await IsolateAsync(key, symbol, cancellationToken) is { } locked)
        {
            return locked;
        }

        if (current?.Leverage != leverage)
        {
            try
            {
                var set = await trading.SetLeverageAsync(key, symbol, leverage, cancellationToken);
                logger.LogInformation(
                    "Set {Symbol}'s leverage from {Before} to {Leverage} through exchange key {CredentialId}", symbol, current?.Leverage, set, key.CredentialId);
            }
            catch (ExchangeException exception) when (exception is { Failure: ExchangeFailure.Rejected, Code: { } code } && LeverageRefusals.Contains(code))
            {
                logger.LogWarning(
                    "Binance refused leverage {Leverage} for {Symbol} through exchange key {CredentialId}, code {ErrorCode}", leverage, symbol, key.CredentialId, code);
                return FuturesSetupOutcome.LeverageRefused;
            }
        }

        return FuturesSetupOutcome.Ready;
    }

    /// <summary>What keeps the account from trading as FR-RISK-09 requires, or null.</summary>
    private static FuturesSetupOutcome? Refusal(FuturesAccountConfig account) =>
        !account.CanTrade ? FuturesSetupOutcome.TradingDisabled
        : account.HedgeMode ? FuturesSetupOutcome.HedgeMode
        : account.MultiAssetsMode ? FuturesSetupOutcome.MultiAssetsMode
        : null;

    /// <summary>Sets the symbol to isolated, or says why it can't be. Null when it is.</summary>
    private async Task<FuturesSetupOutcome?> IsolateAsync(ExchangeKey key, string symbol, CancellationToken cancellationToken)
    {
        try
        {
            await trading.SetMarginTypeAsync(key, symbol, FuturesMarginType.Isolated, cancellationToken);
            logger.LogInformation("Set {Symbol} to isolated margin through exchange key {CredentialId}", symbol, key.CredentialId);

            return null;
        }
        catch (ExchangeException exception) when (exception is { Failure: ExchangeFailure.Rejected, Code: MarginTypeCodes.AlreadySet })
        {
            // Something set it between the read and the change.
            return null;
        }
        catch (ExchangeException exception) when (exception is
        {
            Failure: ExchangeFailure.Rejected,
            Code: MarginTypeCodes.OpenOrders or MarginTypeCodes.OpenPosition or MarginTypeCodes.MultiAssetsMode,
        })
        {
            var outcome = exception.Code == MarginTypeCodes.MultiAssetsMode ? FuturesSetupOutcome.MultiAssetsMode : FuturesSetupOutcome.MarginTypeLocked;
            logger.LogWarning(
                "Binance won't set {Symbol} to isolated margin through exchange key {CredentialId}: {Outcome}", symbol, key.CredentialId, outcome);

            return outcome;
        }
    }
}
