namespace Ecnanib.Exchange.Abstractions;

/// <summary>
/// A user-supplied Binance API key pair (BYOK, C-05). The key and the secret are readable inside this assembly only,
/// are never serialised (no public property carries them) and never appear in <see cref="ToString"/> (FR-KEY-04, FR-LOG-05).
/// </summary>
public sealed class ExchangeCredentials
{
    public ExchangeCredentials(string apiKey, string apiSecret)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key is required.", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(apiSecret))
            throw new ArgumentException("API secret is required.", nameof(apiSecret));

        ApiKey = apiKey.Trim();
        ApiSecret = apiSecret.Trim();
        MaskedApiKey = Mask(ApiKey);
    }

    internal string ApiKey { get; }

    internal string ApiSecret { get; }

    /// <summary>At most the last four characters of the key, prefixed with an ellipsis (FR-KEY-04).</summary>
    public string MaskedApiKey { get; }

    public override string ToString() => $"ExchangeCredentials({MaskedApiKey})";

    /// <summary>
    /// Replaces the secret and the key in exchange text with the mask, so that a reason built from an exchange message
    /// can be stored or shown (FR-KEY-04, FR-LOG-05). Binance echoes neither today; this is the guard for the day it does.
    /// </summary>
    internal string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return text.Replace(ApiSecret, "…", StringComparison.Ordinal).Replace(ApiKey, MaskedApiKey, StringComparison.Ordinal);
    }

    public static string Mask(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "…";
        return value.Length <= 4 ? "…" + new string('*', value.Length) : "…" + value[^4..];
    }
}
