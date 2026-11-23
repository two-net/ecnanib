using Ecnanib.Shared;

namespace Ecnanib.Platform.Settings;

/// <summary>
/// The <c>AiProviders</c> section: one entry per AI provider, named by its code. Providers are an
/// open set, so a new provider is one more entry.
/// </summary>
public sealed record AiProvidersSettings(IReadOnlyDictionary<AiProviderCode, AiProviderSettings> ByCode)
{
    internal static AiProvidersSettings Read(SettingsReader file)
    {
        const string providersKey = "AiProviders";

        var byCode = new Dictionary<AiProviderCode, AiProviderSettings>();
        foreach (var name in file.Map(providersKey, emptyRule: "must configure at least one provider"))
        {
            var key = SettingsReader.Join(providersKey, name);
            var settings = AiProviderSettings.Read(file, key);

            if (string.IsNullOrWhiteSpace(name))
            {
                file.Problem(key, "must be named by the provider's code");
            }
            else if (!byCode.TryAdd(new AiProviderCode(name), settings))
            {
                file.Problem(key, "names the same provider as another entry");
            }
        }

        return new AiProvidersSettings(byCode);
    }
}

/// <summary>The settings of one AI provider.</summary>
/// <param name="BaseUrl">The address of the provider's API.</param>
/// <param name="CatalogKey">
/// The service's own key for syncing the model catalog, or null to sync with users' keys (FR-CAT-3).
/// </param>
/// <param name="TimeoutSeconds">How long a call may take.</param>
/// <param name="Allow">
/// Patterns of the models that may be selected (FR-CAT-6). A pattern is the identifier of a
/// model, in which <c>*</c> stands for any run of characters, and letter case is ignored. A
/// model that no pattern names may not be selected, so an empty list allows none.
/// </param>
/// <param name="Deny">
/// Patterns of the models that may not be selected, whatever <paramref name="Allow"/> says.
/// </param>
/// <param name="LiveSearch">
/// Patterns of the models that can search live. It says so of a model that may be selected, and
/// allows none by itself.
/// </param>
/// <param name="Prices">
/// What the tokens of the provider's models cost, for the models that the file gives a price
/// (FR-CAT-11). The price of a model is the first entry whose pattern names it, so a pattern
/// that names fewer models goes before one that names more. A model that no entry names has no
/// price: what its tokens cost is then not known, and is not taken to be nothing.
/// </param>
public sealed record AiProviderSettings(
    Uri BaseUrl,
    Secret? CatalogKey,
    int TimeoutSeconds,
    IReadOnlyList<string> Allow,
    IReadOnlyList<string> Deny,
    IReadOnlyList<string> LiveSearch,
    IReadOnlyList<ModelPriceSettings> Prices)
{
    /// <summary>
    /// The most that a million tokens may be said to cost. No provider charges near it: a price
    /// above it is most likely one that was written for some other number of tokens.
    /// </summary>
    public const decimal MostPerMillionTokens = 1_000_000m;

    internal static AiProviderSettings Read(SettingsReader file, string key) =>
        new(
            file.Url($"{key}:BaseUrl"),
            file.OptionalSecret($"{key}:CatalogKey"),
            file.WholeNumber($"{key}:TimeoutSeconds", minimum: 1, maximum: 600),
            Patterns(file, $"{key}:Allow"),
            Patterns(file, $"{key}:Deny"),
            Patterns(file, $"{key}:LiveSearch"),
            PricesOf(file, $"{key}:Prices"));

    private static List<string> Patterns(SettingsReader file, string key)
    {
        var patterns = file.List(key).Select(item => Pattern(file, item)).ToList();
        file.Distinct(key, patterns);

        return patterns;
    }

    /// <summary>
    /// The prices of a provider, in the order of the file, which is the order they are tried in.
    /// A pattern is listed once: the second entry of one would never be the price of a model.
    /// </summary>
    private static List<ModelPriceSettings> PricesOf(SettingsReader file, string key)
    {
        var prices = file.List(key)
            .Select(item => new ModelPriceSettings(
                Pattern(file, $"{item}:Model"),
                file.Number($"{item}:InputPerMillionTokens", 0m, MostPerMillionTokens),
                file.Number($"{item}:OutputPerMillionTokens", 0m, MostPerMillionTokens)))
            .ToList();
        file.Distinct(key, prices.Select(price => price.Model));

        return prices;
    }

    /// <summary>
    /// One pattern. An identifier of a model is in one piece, so a pattern with white space in
    /// it would name no model, and is most likely two patterns that were written as one.
    /// </summary>
    private static string Pattern(SettingsReader file, string key)
    {
        var pattern = file.Text(key);
        if (pattern.Any(char.IsWhiteSpace))
        {
            file.Problem(key, "must be the identifier of a model, with * for any characters and no white space");
            return string.Empty;
        }

        return pattern;
    }
}

/// <summary>
/// What the tokens of some models of a provider cost (FR-CAT-11): an entry of the provider's
/// <c>Prices</c> list. The providers do not report their prices in one way, so the file says them.
/// </summary>
/// <param name="Model">
/// A pattern of the models that the price is of, as the patterns of <c>Allow</c> are written:
/// the identifier of a model, in which <c>*</c> stands for any run of characters, and letter
/// case is ignored.
/// </param>
/// <param name="InputPerMillionTokens">
/// What a million tokens of a prompt cost, in US dollars, as the providers publish their prices.
/// Zero for a model that is not charged for.
/// </param>
/// <param name="OutputPerMillionTokens">What a million tokens of an answer cost, in US dollars.</param>
public sealed record ModelPriceSettings(string Model, decimal InputPerMillionTokens, decimal OutputPerMillionTokens);
