namespace Ecnanib.Exchange.Abstractions;

/// <summary>
/// A user-supplied Binance API key pair (BYOK, C-05). The secret is only readable inside this assembly, is never
/// serialised and never appears in <see cref="ToString"/> (FR-KEY-04, FR-LOG-05).
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

    public string ApiKey { get; }

    internal string ApiSecret { get; }

    /// <summary>At most the last four characters of the key, prefixed with an ellipsis (FR-KEY-04).</summary>
    public string MaskedApiKey { get; }

    public override string ToString() => $"ExchangeCredentials({MaskedApiKey})";

    public static string Mask(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "…";
        return value.Length <= 4 ? "…" + new string('*', value.Length) : "…" + value[^4..];
    }
}
