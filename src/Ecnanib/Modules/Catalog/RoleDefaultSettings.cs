using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Modules.Catalog.Contracts;

namespace Ecnanib.Modules.Catalog;

/// <summary>
/// The RoleDefaults section (FR-ROLE-03, SRS 7.3): a default provider per role, and optionally a model. Each
/// role is either a provider's name, such as <c>"Strategist": "OpenAI"</c>, or
/// <c>{ "Provider": "OpenAI", "Model": "…" }</c>. A role that's missing or blank has no default.
/// <para>
/// These seed <c>role_defaults</c> once per role (NFR-DEP-02), so a change here reaches only a role that has
/// no row yet. After that the table is what counts, and administrators edit it on /admin/catalog. Read() is the only way
/// in. An invalid value stops startup with a message that names it (CFG-03).
/// </para>
/// </summary>
internal sealed class RoleDefaultSettings
{
    public const string Section = "RoleDefaults";

    private static readonly string[] Fields = ["Provider", "Model"];

    private RoleDefaultSettings(IReadOnlyList<RoleDefault> defaults) => Defaults = defaults;

    /// <summary>In role order.</summary>
    public IReadOnlyList<RoleDefault> Defaults { get; }

    /// <exception cref="InvalidOperationException">A setting is invalid. The message names it.</exception>
    public static RoleDefaultSettings Read(IConfiguration configuration, IAiProviders providers)
    {
        var names = providers.All.Select(provider => provider.Name).ToList();
        var roles = Enum.GetNames<AiRole>();
        var defaults = new List<RoleDefault>();

        foreach (var entry in configuration.GetSection(Section).GetChildren())
        {
            var key = $"{Section}:{entry.Key}";

            // Ordinal, so a misspelt role is refused rather than matched, and a number never reads as a role.
            Require(roles.Contains(entry.Key, StringComparer.Ordinal), $"{key} isn't a role. Use {string.Join(", ", roles)}.");

            if (ReadDefault(entry, key, names) is { } value)
            {
                defaults.Add(value);
            }
        }

        return new RoleDefaultSettings([.. defaults.OrderBy(value => value.Role)]);
    }

    private static RoleDefault? ReadDefault(IConfigurationSection entry, string key, IReadOnlyList<string> providers)
    {
        var role = Enum.Parse<AiRole>(entry.Key);
        var fields = entry.GetChildren().ToList();

        if (fields.Count == 0)
        {
            var name = entry.Value?.Trim();

            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            Require(providers.Contains(name, StringComparer.Ordinal), $"{key} must be one of {string.Join(", ", providers)}.");
            return new RoleDefault(role, name, null);
        }

        // A misspelt field would otherwise set nothing without a word, as in Catalog:Overrides.
        foreach (var field in fields)
        {
            Require(Fields.Contains(field.Key, StringComparer.Ordinal), $"{key}:{field.Key} isn't a setting. Use {string.Join(", ", Fields)}.");
        }

        var provider = entry["Provider"]?.Trim();
        Require(
            provider is not null && providers.Contains(provider, StringComparer.Ordinal),
            $"{key}:Provider must be one of {string.Join(", ", providers)}.");

        var model = entry["Model"];

        if (string.IsNullOrEmpty(model))
        {
            return new RoleDefault(role, provider!, null);
        }

        Require(
            CatalogRules.IsModelId(model),
            $"{key}:Model must be one model's exact ID, of at most {CatalogRules.ModelMaxLength} characters, with no spaces.");

        return new RoleDefault(role, provider!, model);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
