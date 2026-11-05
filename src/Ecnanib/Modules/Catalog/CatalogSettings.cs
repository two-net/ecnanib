using System.Globalization;
using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Modules.Catalog.Contracts;

namespace Ecnanib.Modules.Catalog;

/// <summary>
/// Metadata for one model that its provider doesn't expose, or gets wrong (FR-CAT-06, ASM-04). Every field
/// but the provider and model is optional, and a set field beats what the provider reports.
/// </summary>
internal sealed record ModelOverride(
    string Provider,
    string Model,
    string? DisplayName,
    string? Family,
    int? ContextWindow,
    int? MaxOutputTokens,
    ModelPrice? Price,
    bool? ImageInput,
    bool? StructuredOutput,
    bool? ToolUse,
    bool? LiveSearch,
    bool? ReasoningEffort);

/// <summary>
/// The Catalog section (SRS 7.3). Read() is the only way in. A missing or invalid value stops startup with a
/// message that names it (CFG-03). An override names one exact model, because a prefix such as gpt-5 would
/// also match gpt-5-mini and give it the wrong price.
/// </summary>
internal sealed class CatalogSettings
{
    public const string Section = "Catalog";

    public const string SyncIntervalKey = Section + ":SyncIntervalHours";

    public const string RetireAfterKey = Section + ":RetireAfterMissingDays";

    public const string OverridesKey = Section + ":Overrides";

    public const int TextMaxLength = 100;

    public const int ModelMaxLength = 256;

    private const int DefaultSyncIntervalHours = 12;
    private const int MaximumSyncIntervalHours = 168;
    private const int DefaultRetireAfterDays = 7;
    private const int MaximumRetireAfterDays = 365;
    private const int MaximumTokens = 100_000_000;
    private const decimal MaximumPrice = 1_000_000m;

    private static readonly string[] Fields =
    [
        "Provider", "Model", "DisplayName", "Family", "ContextWindow", "MaxOutputTokens", "InputPricePerMillion",
        "OutputPricePerMillion", "ImageInput", "StructuredOutput", "ToolUse", "LiveSearch", "ReasoningEffort",
    ];

    private CatalogSettings(
        TimeSpan syncInterval, TimeSpan retireAfter, IReadOnlyDictionary<(string Provider, string Model), ModelOverride> overrides)
    {
        SyncInterval = syncInterval;
        RetireAfter = retireAfter;
        Overrides = overrides;
    }

    public TimeSpan SyncInterval { get; }

    /// <summary>How long a model stays Deprecated before it retires (FR-CAT-04), in whole days.</summary>
    public TimeSpan RetireAfter { get; }

    /// <summary>By provider and exact model ID, both compared exactly.</summary>
    public IReadOnlyDictionary<(string Provider, string Model), ModelOverride> Overrides { get; }

    /// <exception cref="InvalidOperationException">A setting is invalid. The message names it.</exception>
    public static CatalogSettings Read(IConfiguration configuration, IAiProviders providers)
    {
        var interval = ReadInteger(configuration, SyncIntervalKey, 1, MaximumSyncIntervalHours) ?? DefaultSyncIntervalHours;
        var retireAfter = ReadInteger(configuration, RetireAfterKey, 1, MaximumRetireAfterDays) ?? DefaultRetireAfterDays;
        var names = providers.All.Select(provider => provider.Name).ToList();
        var overrides = new Dictionary<(string Provider, string Model), ModelOverride>();

        foreach (var entry in configuration.GetSection(OverridesKey).GetChildren())
        {
            var model = ReadOverride(configuration, $"{OverridesKey}:{entry.Key}", entry, names);

            Require(overrides.TryAdd((model.Provider, model.Model), model), $"{OverridesKey}:{entry.Key} repeats {model.Provider} {model.Model}.");
        }

        return new CatalogSettings(TimeSpan.FromHours(interval), TimeSpan.FromDays(retireAfter), overrides);
    }

    private static ModelOverride ReadOverride(IConfiguration configuration, string prefix, IConfigurationSection entry, IReadOnlyList<string> providers)
    {
        // A misspelt field would otherwise set nothing without a word, such as a price nobody pays attention to.
        foreach (var field in entry.GetChildren())
        {
            Require(Fields.Contains(field.Key, StringComparer.Ordinal), $"{prefix}:{field.Key} isn't a setting. Use {string.Join(", ", Fields)}.");
        }

        var provider = configuration[prefix + ":Provider"]?.Trim();
        Require(
            provider is not null && providers.Contains(provider, StringComparer.Ordinal),
            $"{prefix}:Provider must be one of {string.Join(", ", providers)}.");

        // A * would read as a prefix that matches nothing, so it's refused rather than ignored.
        var model = configuration[prefix + ":Model"]?.Trim();
        Require(
            !string.IsNullOrEmpty(model)
                && model.Length <= ModelMaxLength
                && !model.Any(character => character == '*' || char.IsControl(character) || char.IsWhiteSpace(character)),
            $"{prefix}:Model must be one model's exact ID, of at most {ModelMaxLength} characters, with no spaces or wildcards.");

        var input = ReadPrice(configuration, prefix + ":InputPricePerMillion");
        var output = ReadPrice(configuration, prefix + ":OutputPricePerMillion");
        Require(
            input.HasValue == output.HasValue,
            $"{prefix}:InputPricePerMillion and {prefix}:OutputPricePerMillion must be set together, because a cost needs both.");

        return new ModelOverride(
            provider!,
            model!,
            ReadText(configuration, prefix + ":DisplayName"),
            ReadText(configuration, prefix + ":Family"),
            ReadInteger(configuration, prefix + ":ContextWindow", 1, MaximumTokens),
            ReadInteger(configuration, prefix + ":MaxOutputTokens", 1, MaximumTokens),
            input is { } inputPrice && output is { } outputPrice ? new ModelPrice(inputPrice, outputPrice) : null,
            ReadBool(configuration, prefix + ":ImageInput"),
            ReadBool(configuration, prefix + ":StructuredOutput"),
            ReadBool(configuration, prefix + ":ToolUse"),
            ReadBool(configuration, prefix + ":LiveSearch"),
            ReadBool(configuration, prefix + ":ReasoningEffort"));
    }

    private static string? ReadText(IConfiguration configuration, string key)
    {
        var value = configuration[key]?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        Require(
            value.Length <= TextMaxLength && !value.Any(char.IsControl),
            $"{key} must be at most {TextMaxLength} characters, with no control characters.");

        return value;
    }

    private static int? ReadInteger(IConfiguration configuration, string key, int minimum, int maximum)
    {
        var value = configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Require(int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number), $"{key} must be a whole number.");
        Require(number >= minimum && number <= maximum, $"{key} must be between {minimum} and {maximum}.");

        return number;
    }

    /// <summary>US dollars per million tokens, as an exact decimal (DATA-R-02).</summary>
    private static decimal? ReadPrice(IConfiguration configuration, string key)
    {
        var value = configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Require(
            decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var price) && price <= MaximumPrice,
            $"{key} must be a price in US dollars per million tokens, from 0 to {MaximumPrice.ToString(CultureInfo.InvariantCulture)}, such as 2.5.");

        return price;
    }

    private static bool? ReadBool(IConfiguration configuration, string key)
    {
        var value = configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Require(bool.TryParse(value, out var flag), $"{key} must be true or false.");

        return flag;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
