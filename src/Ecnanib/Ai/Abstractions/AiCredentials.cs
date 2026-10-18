using System.Text.RegularExpressions;

namespace Ecnanib.Ai.Abstractions;

/// <summary>
/// A user-supplied provider API key (BYOK, C-05). The value is readable only inside this assembly, is never
/// serialised and never appears in <see cref="ToString"/> (FR-KEY-04, FR-LOG-05).
/// </summary>
public sealed partial class AiCredentials
{
    private readonly string _escapedApiKey;

    public AiCredentials(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key is required.", nameof(apiKey));

        var trimmed = apiKey.Trim();
        if (trimmed.Any(c => c is < '!' or > '~'))
            throw new ArgumentException("API key must consist of printable ASCII characters.", nameof(apiKey));

        ApiKey = trimmed;
        MaskedApiKey = Mask(trimmed);
        _escapedApiKey = Uri.EscapeDataString(trimmed);
    }

    internal string ApiKey { get; }

    /// <summary>At most the last four characters of the key, prefixed with an ellipsis (FR-KEY-04).</summary>
    public string MaskedApiKey { get; }

    public override string ToString() => $"AiCredentials({MaskedApiKey})";

    public static string Mask(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "…";
        return value.Length <= 4 ? "…" + new string('*', value.Length) : "…" + value[^4..];
    }

    /// <summary>
    /// Replaces the key in provider text with its mask: the exact value, its URL-encoded form and any provider-masked
    /// variant (a prefix and suffix of the key around asterisks), so that provider error text can be logged.
    /// </summary>
    internal string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var result = text.Replace(ApiKey, MaskedApiKey, StringComparison.Ordinal);
        if (!string.Equals(_escapedApiKey, ApiKey, StringComparison.Ordinal))
            result = result.Replace(_escapedApiKey, MaskedApiKey, StringComparison.Ordinal);
        return result.Contains('*') ? MaskedTokenPattern().Replace(result, ReplaceMaskedToken) : result;
    }

    private string ReplaceMaskedToken(Match match)
    {
        var token = match.Value;
        var prefix = token[..token.IndexOf('*')];
        var suffix = token[(token.LastIndexOf('*') + 1)..];
        var trailing = suffix.Length - suffix.TrimEnd('.', ',', ';', ':').Length;
        if (trailing > 0)
            suffix = suffix[..^trailing];
        var known = prefix.Length + suffix.Length;
        if (known >= 4 && known <= ApiKey.Length &&
            ApiKey.StartsWith(prefix, StringComparison.Ordinal) && ApiKey.EndsWith(suffix, StringComparison.Ordinal))
        {
            return MaskedApiKey + token[^trailing..];
        }

        return token;
    }

    /// <summary>A run of key-like characters around one or more asterisks; sentence punctuation after it is kept.</summary>
    [GeneratedRegex(@"[A-Za-z0-9_\-+/=.:]*\*+[A-Za-z0-9_\-+/=.:]*")]
    private static partial Regex MaskedTokenPattern();
}
