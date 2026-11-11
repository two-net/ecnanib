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
/// <param name="Allow">Patterns of the models that may be selected (FR-CAT-6).</param>
/// <param name="Deny">Patterns of the models that may not be selected.</param>
/// <param name="LiveSearch">Patterns of the models that can search live.</param>
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

    private static List<string> Patterns(SettingsReader file, string key) =>
        file.List(key).Select(file.Text).ToList();
}
