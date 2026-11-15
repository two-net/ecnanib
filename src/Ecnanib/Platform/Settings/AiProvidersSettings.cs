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
public sealed record AiProviderSettings(
    Uri BaseUrl,
    Secret? CatalogKey,
    int TimeoutSeconds,
    IReadOnlyList<string> Allow,
    IReadOnlyList<string> Deny,
    IReadOnlyList<string> LiveSearch)
{
    internal static AiProviderSettings Read(SettingsReader file, string key) =>
        new(
            file.Url($"{key}:BaseUrl"),
            file.OptionalSecret($"{key}:CatalogKey"),
            file.WholeNumber($"{key}:TimeoutSeconds", minimum: 1, maximum: 600),
            Patterns(file, $"{key}:Allow"),
            Patterns(file, $"{key}:Deny"),
            Patterns(file, $"{key}:LiveSearch"));

    private static List<string> Patterns(SettingsReader file, string key)
    {
        var patterns = file.List(key).Select(item => Pattern(file, item)).ToList();
        file.Distinct(key, patterns);

        return patterns;
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
