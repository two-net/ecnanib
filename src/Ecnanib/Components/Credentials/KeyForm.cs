using System.Globalization;
using Ecnanib.Kernel;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.Identity.Contracts;

namespace Ecnanib.Components.Credentials;

/// <summary>What the static key pages share: tidying what was pasted, and the text for each refusal.</summary>
internal static class KeyForm
{
    /// <summary>A key as pasted, without the spaces and line ends that copying often adds around it.</summary>
    public static string Clean(string? value) => value?.Trim() ?? string.Empty;

    /// <summary>A private key from a text area, which a browser sends with CRLF line ends.</summary>
    public static string Pem(string? value) => Clean(value).ReplaceLineEndings("\n");

    /// <summary>The key type is whichever secret field was filled in. Null when both or neither were.</summary>
    public static BinanceKeyType? TypeOf(string secret, string privateKey) => (secret.Length > 0, privateKey.Length > 0) switch
    {
        (true, false) => BinanceKeyType.Hmac,
        (false, true) => BinanceKeyType.Ed25519,
        _ => null,
    };

    /// <summary>Only an exact name counts, because Enum.TryParse would also take a number.</summary>
    public static bool TryEnvironment(string? value, out TradingEnvironment environment)
    {
        environment = default;

        return Enum.GetNames<TradingEnvironment>().Contains(value, StringComparer.Ordinal)
            && Enum.TryParse(value, out environment);
    }

    public static string? LabelErrorOf(SaveResult? result) =>
        result?.Outcome == SaveOutcome.LabelInvalid ? LabelError : null;

    public static string LabelError =>
        string.Format(CultureInfo.CurrentCulture, Strings.CredentialLabelInvalid, CredentialLimits.LabelMaxLength);

    public static string? TwoFactorErrorOf(SaveResult? result) => result?.TwoFactor switch
    {
        TwoFactorCheck.Refused => Strings.TwoFactorCodeRefused,
        TwoFactorCheck.Throttled => Strings.TwoFactorCodeThrottled,
        _ => null,
    };

    /// <summary>A refusal that belongs to the whole form rather than to one field.</summary>
    public static string? FormErrorOf(SaveResult? result, string provider) => result?.Outcome switch
    {
        SaveOutcome.KeyMalformed => Strings.CredentialKeyMalformed,
        SaveOutcome.UnknownProvider => Strings.CredentialUnknownProvider,
        SaveOutcome.Refused when provider == CredentialProviders.Binance => Strings.CredentialBinanceRefused,
        SaveOutcome.Refused => string.Format(CultureInfo.CurrentCulture, Strings.CredentialAiRefused, provider),
        SaveOutcome.WithdrawalsEnabled => Strings.CredentialWithdrawalsRefused,
        SaveOutcome.Unreachable => Strings.CredentialBinanceUnreachable,
        SaveOutcome.DifferentAccount => Strings.CredentialDifferentAccount,
        SaveOutcome.NotFound => Strings.CredentialGone,
        SaveOutcome.Changed => Strings.CredentialChanged,
        _ => null,
    };
}
