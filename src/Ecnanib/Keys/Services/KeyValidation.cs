using Ecnanib.Ai.Abstractions;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;

namespace Ecnanib.Keys.Services;

internal enum ValidationVerdict
{
    Valid,
    /// <summary>The service refused the key; the reason is its own explanation.</summary>
    Invalid,
    /// <summary>The check could not complete (outage, rate limit, timeout, unexpected answer); says nothing about the key.</summary>
    Failed,
}

internal sealed record ExchangeKeyValidation(ValidationVerdict Verdict, ApiKeyPermissions? Permissions = null, string? Reason = null, bool WithdrawalEnabled = false);

internal sealed record ProviderKeyValidation(ValidationVerdict Verdict, IReadOnlyList<AiModelInfo>? Models = null, string? Reason = null);

/// <summary>Texts stored as validation errors or state reasons and shown in the UI. None of them ever contains a key value.</summary>
internal static class KeyReasons
{
    public const string InvalidLabel = "Enter a label of at most 100 characters.";

    public const string InvalidExchangeCredentials = "Enter both the API key and the secret.";

    public const string InvalidProviderKey = "Enter the provider's API key: printable characters without spaces.";

    public const string WithdrawalEnabled =
        "Binance reports that this key can withdraw funds. Ecnanib refuses keys with withdrawal permission; create a key with reading and trading permissions only.";

    public const string MalformedStoredKey = "The stored key is malformed; replace it.";

    public const string StoppedExchangeKeyDeleted = "Stopped: its exchange key was deleted.";

    public static string StoppedProviderKeyDeleted(AiProvider provider) => $"Stopped: its {ProviderNames.Display(provider)} key was deleted.";

    public static string Timeout(string service, TimeSpan timeout) => $"{service} did not answer within {timeout.TotalSeconds:0} seconds.";

    public static string ExchangeKeyInvalid(string label, string reason) => $"Exchange key '{label}' is invalid: {reason}";

    public static string ProviderKeyInvalid(AiProvider provider, string reason) => $"{ProviderNames.Display(provider)} key is invalid: {reason}";
}
