using System.Text.RegularExpressions;
using Ecnanib.Platform.Secrets;
using Npgsql;

namespace Ecnanib.Platform.Logging;

/// <summary>
/// What counts as a secret (FR-LOG-05). Over-masking is the safe direction, so the rules prefer it,
/// with one deliberate exception: token counts, which FR-LOG-07 needs.
/// </summary>
internal sealed class SecretRules
{
    public const string Mask = "***";

    /// <summary>Shorter values would mask ordinary words, and settings such as a minimum length.</summary>
    private const int MinimumKnownValueLength = 8;

    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    /// <summary>In the name as written, except that header and config spellings are matched too.</summary>
    private const string SecretNames =
        "password|passwd|passphrase|pwd|secret|api[-_ ]?key|private[-_ ]?key|master[-_ ]?key|signing[-_ ]?key"
        + "|encryption[-_ ]?key|token|signature|hmac|totp|connection[-_ ]?string";

    /// <summary>Compared with a name that has been lowered and stripped of separators.</summary>
    private static readonly string[] SensitiveFragments =
    [
        "password", "passwd", "passphrase", "pwd", "secret", "apikey", "privatekey", "masterkey", "signingkey",
        "encryptionkey", "token", "bearer", "signature", "hmac", "authorization", "cookie", "totp", "twofactorcode",
        "recoverycode", "connectionstring",
    ];

    // Exchange and AI responses end up in log values, so these regexes run on untrusted text. That's
    // why they're NonBacktracking, which also rules out lookaround.

    /// <summary>Header values contain spaces, as in "Basic abc" and "a=b; c=d", so these run to the line end.</summary>
    private static readonly Regex Headers = new(
        """(?<name>[\w-]*(?:authorization|cookie))(?<separator>\\?["']?\s*[:=]\s*)(?:\\?"[^"]*"?|'[^']*'?|[^\r\n]*)""",
        Options);

    private static readonly Regex Pairs = new(
        $"""(?<name>[\w.-]*(?:{SecretNames})[\w.-]*|\b(?:sig|otp)\b)(?<separator>\\?["']?\s*[:=]\s*)(?<value>\\?"[^"]*"?|'[^']*'?|[^\s;&]+)""",
        Options);

    /// <summary>The way KeyValuePair renders when it's logged without @.</summary>
    private static readonly Regex RenderedPairs = new(
        $"""\[\s*(?<name>[\w.-]*(?:{SecretNames}|authorization|cookie)[\w.-]*)\s*,[^\]]*\]""",
        Options);

    /// <summary>Secrets recognisable without a name: 64 alphanumerics covers Binance keys and signatures.</summary>
    private static readonly Regex Shapes = new(
        """\bbearer\s+[\w\-.~+/]+=*|[a-z][a-z0-9+.-]*://[^\s/@]*:[^\s/@]*@|\bsk-[\w-]{16,}|\bxai-[\w-]{16,}|\baiza[\w-]{30,}|\beyj[\w-]+\.[\w-]+\.[\w-]*|\b[a-z0-9]{64,}\b""",
        Options);

    private readonly string[] _knownValues;

    private SecretRules(IEnumerable<string> knownValues) =>
        // Longest first, so a connection string goes before the password inside it.
        _knownValues = [.. knownValues.Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length)];

    /// <summary>
    /// Reads the secrets the application itself holds, so that they're masked as exact values even
    /// where no rule would recognise them. New secret settings are picked up by their key names.
    /// </summary>
    public static SecretRules From(IConfiguration configuration) => new(KnownValues(configuration));

    public bool IsSensitiveName(string name)
    {
        var normalized = Normalize(name);

        return SensitiveFragments.Any(normalized.Contains) || normalized.EndsWith("otp", StringComparison.Ordinal);
    }

    /// <summary>Usage figures such as InputTokens, as opposed to a numeric secret such as a TOTP code in Token.</summary>
    public static bool IsTokenCount(string name)
    {
        var normalized = Normalize(name);

        return normalized.Contains("tokens", StringComparison.Ordinal)
            || normalized.Contains("tokencount", StringComparison.Ordinal);
    }

    public string Scrub(string text)
    {
        foreach (var value in _knownValues)
        {
            text = text.Replace(value, Mask, StringComparison.Ordinal);
        }

        text = Headers.Replace(text, match => $"{match.Groups["name"].Value}{match.Groups["separator"].Value}{Mask}");
        text = Pairs.Replace(text, MaskPair);
        text = RenderedPairs.Replace(text, match => $"[{match.Groups["name"].Value}, {Mask}]");

        return Shapes.Replace(text, Mask);
    }

    private static string MaskPair(Match match)
    {
        var name = match.Groups["name"].Value;
        var value = match.Groups["value"].Value;

        return IsTokenCount(name) && IsCount(value)
            ? match.Value
            : $"{name}{match.Groups["separator"].Value}{Mask}";
    }

    /// <summary>A number, or a template placeholder that the property rules will judge by its value.</summary>
    private static bool IsCount(string value) =>
        value.All(char.IsAsciiDigit) || (value.StartsWith('{') && value.EndsWith('}'));

    private static string Normalize(string name) =>
        string.Concat(name.Where(char.IsAsciiLetterOrDigit)).ToLowerInvariant();

    private static IEnumerable<string> KnownValues(IConfiguration configuration)
    {
        foreach (var (key, value) in configuration.AsEnumerable())
        {
            // A key with no section is an environment variable, and PWD would mask every path. In
            // Logging, level names would count once an override mentions a sensitive word, as
            // Microsoft.AspNetCore.Authorization does.
            if (value is null || !key.Contains(':') || key.StartsWith("Logging:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A master key's ID is a label, which logs, audit records and the health page show.
            if (IsMasterKeyId(key))
            {
                continue;
            }

            if (key.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase)
                && PasswordIn(value) is { Length: >= MinimumKnownValueLength } password)
            {
                yield return password;
            }

            if (IsSensitiveKey(key) && IsSecretValue(value))
            {
                yield return value;
            }
        }
    }

    /// <summary>The whole path counts, because the last segment of Security:EncryptionKeys:0:Key is just Key.</summary>
    private static bool IsSensitiveKey(string key) =>
        SensitiveFragments.Any(Normalize(key).Contains);

    private static bool IsMasterKeyId(string key) =>
        key.StartsWith(MasterKeys.Section + ":", StringComparison.OrdinalIgnoreCase)
        && key.EndsWith(":Id", StringComparison.OrdinalIgnoreCase);

    /// <summary>Settings about secrets, such as Security:PasswordMinLength, are numbers and flags.</summary>
    private static bool IsSecretValue(string value) =>
        value.Length >= MinimumKnownValueLength && !double.TryParse(value, out _) && !bool.TryParse(value, out _);

    private static string? PasswordIn(string connectionString)
    {
        try
        {
            return new NpgsqlConnectionStringBuilder(connectionString).Password;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
