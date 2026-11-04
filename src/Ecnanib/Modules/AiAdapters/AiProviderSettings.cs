using System.Globalization;
using System.Text.RegularExpressions;
using Ecnanib.Modules.AiAdapters.Contracts;

namespace Ecnanib.Modules.AiAdapters;

/// <summary>One provider's settings (EXT-AI-03). A disabled provider's other settings aren't read.</summary>
/// <param name="BaseUrl">Null only when the provider is disabled.</param>
internal sealed record AiProviderOptions(
    string Name,
    bool Enabled,
    Uri? BaseUrl,
    TimeSpan Timeout,
    int MaxRetries,
    TimeSpan RetryBackoff,
    int MaxConcurrentPerCredential);

/// <summary>
/// The AiProviders section (SRS 7.3). Read() is the only way in. Every adapter needs its own section, and
/// its base URL comes from there and nowhere else (NFR-SEC-10), with no default in code. A missing or
/// invalid value stops startup with a message that names the setting (CFG-03).
/// </summary>
internal sealed partial class AiProviderSettings
{
    public const string Section = "AiProviders";

    private const int DefaultTimeoutSeconds = 120;
    private const int MaximumTimeoutSeconds = 600;
    private const int DefaultMaxRetries = 2;
    private const int MaximumMaxRetries = 5;
    private const int DefaultRetryBackoffMs = 1000;
    private const int MaximumRetryBackoffMs = 60_000;
    private const int DefaultMaxConcurrentPerCredential = 4;
    private const int MaximumMaxConcurrentPerCredential = 32;

    private AiProviderSettings(IReadOnlyList<AiProviderOptions> providers, IReadOnlyList<string> unmatched)
    {
        Providers = providers;
        Unmatched = unmatched;
    }

    /// <summary>One per adapter, in the order the adapters were registered.</summary>
    public IReadOnlyList<AiProviderOptions> Providers { get; }

    /// <summary>
    /// Sections that no adapter has. They're only warned about: a misspelt name can't hide, because the
    /// adapter's own section is then missing, which stops startup.
    /// </summary>
    public IReadOnlyList<string> Unmatched { get; }

    /// <exception cref="InvalidOperationException">A setting is missing or invalid, or two adapters share a name. The message names it.</exception>
    public static AiProviderSettings Read(IConfiguration configuration, IHostEnvironment environment, IEnumerable<IAiProviderAdapter> adapters)
    {
        var names = adapters.Select(adapter => adapter.Name).ToList();

        foreach (var name in names)
        {
            Require(name is not null && NamePattern().IsMatch(name), $"An AI provider adapter's name must be 1 to 32 letters and digits, starting with a letter, but {name ?? "null"} isn't.");
        }

        var duplicate = names.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        Require(duplicate is null, $"Two AI provider adapters are named {duplicate?.Key}.");

        var providers = names.Select(name => ReadProvider(configuration, environment, name)).ToList();
        var unmatched = configuration.GetSection(Section).GetChildren()
            .Select(section => section.Key)
            .Where(key => !names.Contains(key, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return new AiProviderSettings(providers, unmatched);
    }

    private static AiProviderOptions ReadProvider(IConfiguration configuration, IHostEnvironment environment, string name)
    {
        var prefix = $"{Section}:{name}:";
        var baseUrlKey = prefix + "BaseUrl";

        Require(configuration.GetSection(Section + ":" + name).Exists(), $"{baseUrlKey} is required, because the {name} adapter is registered.");

        var enabledKey = prefix + "Enabled";
        var enabledValue = configuration[enabledKey];
        var enabled = true;

        if (!string.IsNullOrWhiteSpace(enabledValue))
        {
            Require(bool.TryParse(enabledValue, out enabled), $"{enabledKey} must be true or false.");
        }

        if (!enabled)
        {
            return new AiProviderOptions(name, false, null, TimeSpan.Zero, 0, TimeSpan.Zero, 1);
        }

        var baseUrl = ReadBaseUrl(configuration, environment, baseUrlKey);
        var timeout = ReadInteger(configuration, prefix + "TimeoutSeconds", DefaultTimeoutSeconds, 1, MaximumTimeoutSeconds);
        var retries = ReadInteger(configuration, prefix + "MaxRetries", DefaultMaxRetries, 0, MaximumMaxRetries);
        var backoff = ReadInteger(configuration, prefix + "RetryBackoffMs", DefaultRetryBackoffMs, 0, MaximumRetryBackoffMs);
        var concurrency = ReadInteger(
            configuration,
            prefix + "MaxConcurrentPerCredential",
            DefaultMaxConcurrentPerCredential,
            1,
            MaximumMaxConcurrentPerCredential);

        return new AiProviderOptions(
            name,
            true,
            baseUrl,
            TimeSpan.FromSeconds(timeout),
            retries,
            TimeSpan.FromMilliseconds(backoff),
            concurrency);
    }

    /// <summary>
    /// A server's address and nothing more: an absolute URL with no path, user name, query or fragment,
    /// over https outside Development. Adapters own their paths, including the API version.
    /// </summary>
    private static Uri ReadBaseUrl(IConfiguration configuration, IHostEnvironment environment, string key)
    {
        var value = configuration[key]?.Trim();

        Require(!string.IsNullOrEmpty(value), $"{key} is required.");
        Require(
            Uri.TryCreate(value, UriKind.Absolute, out var url)
                && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
                && url.UserInfo.Length == 0
                && url.AbsolutePath == "/"
                && url.Query.Length == 0
                && url.Fragment.Length == 0,
            $"{key} must be a server's address only, such as https://api.openai.com.");
        Require(
            url!.Scheme == Uri.UriSchemeHttps || environment.IsDevelopment(),
            $"{key} must use https outside Development, because users' keys and prompts travel to it.");

        return url;
    }

    private static int ReadInteger(IConfiguration configuration, string key, int defaultValue, int minimum, int maximum)
    {
        var value = configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        Require(int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number), $"{key} must be a whole number.");
        Require(number >= minimum && number <= maximum, $"{key} must be between {minimum} and {maximum}.");

        return number;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
