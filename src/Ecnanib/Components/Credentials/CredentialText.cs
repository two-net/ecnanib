using System.Globalization;
using Ecnanib.Kernel;
using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Modules.Credentials.Contracts;

namespace Ecnanib.Components.Credentials;

/// <summary>
/// What a credential's provider, status and permissions are called. Public so that a test can prove every
/// status and problem has text: an issue that adds one adds its text here, or the page fails.
/// </summary>
public static class CredentialText
{
    public static string StatusOf(CredentialStatus status) => status switch
    {
        CredentialStatus.Valid => Strings.CredentialStatusValid,
        CredentialStatus.Invalid => Strings.CredentialStatusInvalid,
        CredentialStatus.InsufficientPermissions => Strings.CredentialStatusInsufficientPermissions,
        CredentialStatus.Unverified => Strings.CredentialStatusUnverified,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Give the status its text in Strings.resx."),
    };

    /// <summary>An AI key's models, as its card shows them.</summary>
    public static string ModelsOf(DisplayTime time, CredentialModels? models) =>
        models is { } synced
            ? string.Format(CultureInfo.CurrentCulture, Strings.CredentialModelsSynced, synced.Available, time.Format(synced.SyncedAt))
            : Strings.CredentialModelsNever;

    /// <summary>What a user's sync of their keys did (FR-CAT-03).</summary>
    public static string SyncResultOf(UserSyncResult result) => result.Outcome switch
    {
        UserSyncOutcome.Synced when result.KeysFailed == 0 =>
            string.Format(CultureInfo.CurrentCulture, Strings.CredentialsSynced, result.Models),
        UserSyncOutcome.Synced =>
            string.Format(CultureInfo.CurrentCulture, Strings.CredentialsSyncedPartly, result.Models, result.KeysFailed),
        UserSyncOutcome.NoKeys => Strings.CredentialsSyncNoKeys,
        UserSyncOutcome.TooSoon => Strings.CredentialsSyncTooSoon,
        UserSyncOutcome.Failed => Strings.CredentialsSyncFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(result), result.Outcome, "Give the outcome its text in Strings.resx."),
    };

    public static string ProblemOf(CredentialProblem problem) => problem switch
    {
        CredentialProblem.Refused => Strings.CredentialProblemRefused,
        CredentialProblem.PermissionDenied => Strings.CredentialProblemPermissionDenied,
        CredentialProblem.NoModels => Strings.CredentialProblemNoModels,
        CredentialProblem.WithdrawalsEnabled => Strings.CredentialProblemWithdrawalsEnabled,
        CredentialProblem.NoTradingPermission => Strings.CredentialProblemNoTradingPermission,
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, "Give the problem its text in Strings.resx."),
    };

    /// <summary>What the user should know about its status, or null when it's valid.</summary>
    public static string? ExplanationOf(CredentialRecord credential) => credential switch
    {
        { Status: CredentialStatus.Valid } => null,
        { Problem: { } problem } => ProblemOf(problem),
        { Status: CredentialStatus.Unverified } => Strings.CredentialUnverifiedHint,
        _ => null,
    };

    public static string EnvironmentOf(TradingEnvironment environment) => environment switch
    {
        TradingEnvironment.Live => Strings.CredentialEnvironmentLive,
        TradingEnvironment.Demo => Strings.CredentialEnvironmentDemo,
        _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, "Give the environment its text in Strings.resx."),
    };

    /// <summary>An AI provider by its own name, and Binance with its environment, as FR-KEY-01 lists them.</summary>
    public static string ProviderOf(CredentialRecord credential) =>
        credential is { Kind: CredentialKind.Exchange, Environment: { } environment }
            ? string.Format(CultureInfo.CurrentCulture, Strings.CredentialBinanceProvider, EnvironmentOf(environment))
            : credential.Provider;

    public static string KeyTypeOf(BinanceKeyType type) => type switch
    {
        BinanceKeyType.Hmac => Strings.CredentialKeyTypeHmac,
        BinanceKeyType.Ed25519 => Strings.CredentialKeyTypeEd25519,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Give the key type its text in Strings.resx."),
    };

    /// <summary>Whether Binance enabled the key for a market, or that it hasn't said.</summary>
    public static string MarketOf(bool? enabled) => enabled switch
    {
        true => Strings.CredentialMarketEnabled,
        false => Strings.CredentialMarketNotEnabled,
        null => Strings.CredentialMarketUnknown,
    };

    /// <summary>The last four characters, written so that it's plain they're the end of something longer.</summary>
    public static string Ending(string lastFour) => string.Format(CultureInfo.CurrentCulture, Strings.CredentialEnding, lastFour);

    public static string Time(DisplayTime time, DateTimeOffset? value) => value is { } at ? time.Format(at) : Strings.CredentialNever;
}
