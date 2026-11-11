using System.Text.RegularExpressions;
using Ecnanib.Platform.Settings;

namespace Ecnanib.Platform.Logging;

/// <summary>
/// Decides what must not be written to a log, and replaces it with <c>***</c>. Secrets, API keys,
/// tokens, and passwords are recognised by the name they are logged under, by their value when
/// they come from the configuration file, and by their shape in text (NFR-LOG-4).
/// </summary>
/// <remarks>
/// These rules are applied in one place, <see cref="MaskingSink"/>, which every event passes on
/// its way to an output. See docs/logging.md for what the rules cannot recognise.
/// </remarks>
public sealed partial class SecretMask
{
    /// <summary>What a masked value is replaced with.</summary>
    public const string Masked = "***";

    // A shorter value would also be found inside ordinary words and paths.
    private const int ShortestValueMaskedByItself = 8;

    // A name is sensitive when one of its words is one of these.
    private static readonly HashSet<string> SensitiveWords = new(StringComparer.Ordinal)
    {
        "password", "passwd", "pwd", "passphrase", "secret", "secrets", "signature", "authorization",
        "cookie", "credential", "credentials", "otp", "totp",
    };

    // Or when the whole name, without separators, ends in one of these.
    private static readonly string[] SensitiveEndings =
    [
        "connectionstring", "apikeys", "secretkeys", "masterkeys", "privatekeys", "accesskeys",
    ];

    private readonly string[] _values;

    /// <summary>Creates a mask that also replaces the given values wherever they appear in text.</summary>
    public SecretMask(IEnumerable<Secret> secrets)
    {
        // Longest first, so a value that contains another is replaced as a whole.
        _values =
        [
            .. secrets
                .Select(secret => secret.Reveal())
                .Where(value => value.Length >= ShortestValueMaskedByItself)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(value => value.Length),
        ];
    }

    /// <summary>A mask that knows every secret in the configuration file.</summary>
    public static SecretMask For(EcnanibSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        IEnumerable<Secret?> secrets =
        [
            settings.ConnectionStrings.EcnanibDbContext,
            .. settings.Security.MasterKeys.Values,
            settings.Email.Password,
            .. settings.AiProviders.ByCode.Values.Select(provider => provider.CatalogKey),
        ];

        return new SecretMask(secrets.OfType<Secret>());
    }

    /// <summary>
    /// True when a value logged under <paramref name="name"/> must be masked whatever it holds:
    /// the name of a property, of a member of a logged object, or of an entry such as a header.
    /// </summary>
    /// <remarks>
    /// A name is sensitive when it says password, secret, signature, authorization, cookie, or
    /// credential, or when it ends in key or token: <c>ApiKey</c>, <c>X-MBX-APIKEY</c>,
    /// <c>access_token</c>. Counts and identifiers are not: <c>InputTokens</c>, <c>ActiveKeyId</c>.
    /// </remarks>
    public bool IsSensitiveName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var words = WordsOf(name);
        if (words.Count == 0)
        {
            return false;
        }

        if (words.Any(SensitiveWords.Contains))
        {
            return true;
        }

        var last = words[^1];
        if (last.EndsWith("key", StringComparison.Ordinal) || last.EndsWith("token", StringComparison.Ordinal))
        {
            return true;
        }

        var joined = string.Concat(words);
        return SensitiveEndings.Any(ending => joined.EndsWith(ending, StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns <paramref name="text"/> with every secret in it replaced: the values from the
    /// configuration file, and anything shaped like a credential. Text without one is returned
    /// as the same instance.
    /// </summary>
    public string Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (var value in _values)
        {
            text = text.Replace(value, Masked, StringComparison.Ordinal);
        }

        text = AuthorizationScheme().Replace(text, "$1 " + Masked);
        text = QuotedPair().Replace(text, "${name}${between}" + Masked + "${close}");
        text = Pair().Replace(text, "${name}=" + Masked);
        text = ProviderKey().Replace(text, Masked);
        text = SignedToken().Replace(text, Masked);

        return text;
    }

    /// <summary>The words of a name in lower case: <c>X-MBX-APIKEY</c> is x, mbx, apikey; <c>ActiveKeyId</c> is active, key, id.</summary>
    private static List<string> WordsOf(string name)
    {
        var words = new List<string>();
        var start = -1;

        for (var index = 0; index < name.Length; index++)
        {
            var current = name[index];
            if (!char.IsLetterOrDigit(current))
            {
                End(index);
                continue;
            }

            if (start < 0)
            {
                start = index;
                continue;
            }

            var previous = name[index - 1];
            var startsWord = char.IsUpper(current)
                && (char.IsLower(previous)
                    || (char.IsUpper(previous) && index + 1 < name.Length && char.IsLower(name[index + 1])));
            if (startsWord)
            {
                End(index);
                start = index;
            }
        }

        End(name.Length);
        return words;

        void End(int at)
        {
            if (start >= 0)
            {
                words.Add(name[start..at].ToLowerInvariant());
                start = -1;
            }
        }
    }

    // "Authorization: Bearer abc.def" and "Basic dXNlcjpwYXNz".
    [GeneratedRegex(@"\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationScheme();

    // "password": "value", as in a JSON body.
    [GeneratedRegex(
        @"(?<name>""[A-Za-z0-9_.-]*(?:password|passwd|pwd|secret|signature|token|key)"")(?<between>\s*:\s*"")(?:\\.|[^""\\])*(?<close>"")",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuotedPair();

    // password=value, as in a connection string, a query string, or a form body.
    [GeneratedRegex(
        @"(?<name>(?<![A-Za-z0-9])[A-Za-z0-9_.-]*(?:password|passwd|pwd|secret|signature|token|key))\s*=\s*[^\s&;,""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pair();

    // The key formats AI providers issue: sk-..., xai-..., AIza...
    [GeneratedRegex(@"\b(?:sk|xai)-[A-Za-z0-9_-]{16,}|\bAIza[0-9A-Za-z_-]{30,}", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderKey();

    // A signed token in three Base64 parts, such as a session or access token.
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex SignedToken();
}
