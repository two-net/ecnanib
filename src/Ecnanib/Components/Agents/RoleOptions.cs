using System.Globalization;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Catalog.Contracts;

namespace Ecnanib.Components.Agents;

/// <summary>
/// The models a user's valid AI keys can give each role, as a select's options (FR-ROLE-02), with the catalog's names
/// for a chosen model that's no longer offered, and which models administrators hid. The settings page and the wizard
/// share it, so both offer the same.
/// </summary>
public sealed class RoleOptions
{
    private readonly Dictionary<AiRole, List<(string Value, string Text)>> _options;
    private readonly Dictionary<string, string> _modelNames;
    private readonly HashSet<string> _hiddenModels;

    private RoleOptions(
        Dictionary<AiRole, List<(string Value, string Text)>> options, Dictionary<string, string> modelNames, HashSet<string> hiddenModels)
    {
        _options = options;
        _modelNames = modelNames;
        _hiddenModels = hiddenModels;
    }

    public static RoleOptions Empty { get; } = new([], [], []);

    /// <summary>Whether any role has a model to offer. When none has, the user's AI keys can't fill any role yet.</summary>
    public bool Any => _options.Values.Any(options => options.Count > 0);

    public static async Task<RoleOptions> LoadAsync(ICatalog catalog, RoleChoices choices, CancellationToken cancellationToken)
    {
        var models = await catalog.ListAsync(cancellationToken);

        return new RoleOptions(
            choices.Options.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Select(choice => (AgentForm.ValueOf(choice.Model), AgentText.ModelOption(choice, pair.Key))).ToList()),
            models.ToDictionary(model => AgentForm.ValueOf(new ModelRef(model.Provider, model.ModelId)), model => model.DisplayName),
            [.. models.Where(model => model.Hidden).Select(model => AgentForm.ValueOf(new ModelRef(model.Provider, model.ModelId)))]);
    }

    /// <summary>
    /// The role's options, plus the chosen model when it isn't one of them, such as when its key stopped
    /// working, or an administrator hid it, which leaves it to the agents that have it. Otherwise saving would
    /// quietly drop it.
    /// </summary>
    public IEnumerable<(string Value, string Text)> For(AiRole role, string? chosen)
    {
        var options = _options.GetValueOrDefault(role) ?? [];

        if (string.IsNullOrEmpty(chosen) || options.Any(option => option.Value == chosen))
        {
            return options;
        }

        return [(chosen, TextOf(chosen)), .. options];
    }

    /// <summary>A chosen model's text, as its option says, for a summary.</summary>
    public string TextOf(AiRole role, string chosen) =>
        (_options.GetValueOrDefault(role) ?? []).FirstOrDefault(option => option.Value == chosen) is { Text: { } text } ? text : TextOf(chosen);

    /// <summary>The provider of a model's option value, or null for none.</summary>
    public static string? ProviderOf(string? value) =>
        value?.IndexOf(AgentForm.ModelSeparator, StringComparison.Ordinal) is int separator and > 0 ? value[..separator] : null;

    /// <summary>FR-ROLE-04 allows it, with a warning. Activation warns too.</summary>
    public static bool RiskCriticSharesProvider(AgentForm form) =>
        ProviderOf(form.RoleValue(AiRole.Strategist, false)) is { } strategist
        && ProviderOf(form.RoleValue(AiRole.RiskCritic, false)) == strategist;

    private string TextOf(string chosen)
    {
        var separator = chosen.IndexOf(AgentForm.ModelSeparator, StringComparison.Ordinal);
        var name = _modelNames.GetValueOrDefault(chosen) ?? (separator > 0 ? chosen[(separator + 1)..] : chosen);

        return string.Format(
            CultureInfo.CurrentCulture,
            _hiddenModels.Contains(chosen) ? Strings.AgentSettingsModelHidden : Strings.AgentSettingsModelUnavailable,
            name);
    }
}
