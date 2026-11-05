using Ecnanib.Kernel;
using Ecnanib.Modules.Catalog.Contracts;

namespace Ecnanib.Components.Catalog;

/// <summary>
/// The model page's form (FR-CAT-05). Every value arrives as text, because anyone can post anything, and is read
/// in <see cref="ToEdit"/>. A blank field inherits.
/// </summary>
public sealed class ModelEditForm
{
    /// <summary>What a capability's select posts to inherit. Otherwise <c>true</c> or <c>false</c>.</summary>
    public const string Inherit = "";

    public string? DisplayName { get; set; }

    /// <summary>A ticked box posts <c>true</c>, and one that isn't ticked posts nothing.</summary>
    public bool Hidden { get; set; }

    public string? ImageInput { get; set; }

    public string? StructuredOutput { get; set; }

    public string? ToolUse { get; set; }

    public string? LiveSearch { get; set; }

    public string? ReasoningEffort { get; set; }

    public string? InputPrice { get; set; }

    public string? OutputPrice { get; set; }

    public bool TechnicalAnalyst { get; set; }

    public bool SentimentAnalyst { get; set; }

    public bool Strategist { get; set; }

    public bool RiskCritic { get; set; }

    public bool Reviewer { get; set; }

    public static ModelEditForm Of(ModelEdit edit)
    {
        var form = new ModelEditForm
        {
            DisplayName = edit.DisplayName,
            Hidden = edit.Hidden,
            InputPrice = edit.Price?.InputPerMillion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            OutputPrice = edit.Price?.OutputPerMillion.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        foreach (var capability in Enum.GetValues<ModelCapability>())
        {
            form.SetCapability(capability, edit.Capabilities.Of(capability) is { } value ? (value ? "true" : "false") : Inherit);
        }

        foreach (var role in edit.RecommendedRoles)
        {
            form.SetRecommended(role, true);
        }

        return form;
    }

    public string? CapabilityValue(ModelCapability capability) => capability switch
    {
        ModelCapability.ImageInput => ImageInput,
        ModelCapability.StructuredOutput => StructuredOutput,
        ModelCapability.ToolUse => ToolUse,
        ModelCapability.LiveSearch => LiveSearch,
        ModelCapability.ReasoningEffort => ReasoningEffort,
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null),
    };

    public bool IsRecommended(AiRole role) => role switch
    {
        AiRole.TechnicalAnalyst => TechnicalAnalyst,
        AiRole.SentimentAnalyst => SentimentAnalyst,
        AiRole.Strategist => Strategist,
        AiRole.RiskCritic => RiskCritic,
        AiRole.Reviewer => Reviewer,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    /// <summary>
    /// The edit, or null with the fields that couldn't be read. A price that isn't one, or one without the
    /// other, is refused here, and the catalog checks the rest.
    /// </summary>
    public (ModelEdit? Edit, IReadOnlySet<ModelEditField> Fields) ToEdit()
    {
        var fields = new HashSet<ModelEditField>();
        var input = Price(InputPrice, ModelEditField.InputPrice, fields);
        var output = Price(OutputPrice, ModelEditField.OutputPrice, fields);

        // A cost needs both, so the blank one is the one that's wrong.
        if (input.HasValue != output.HasValue && fields.Count == 0)
        {
            fields.Add(input.HasValue ? ModelEditField.OutputPrice : ModelEditField.InputPrice);
        }

        if (fields.Count > 0)
        {
            return (null, fields);
        }

        var edit = new ModelEdit(
            string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim(),
            Hidden,
            new ModelCapabilities(
                Flag(ImageInput), Flag(StructuredOutput), Flag(ToolUse), Flag(LiveSearch), Flag(ReasoningEffort)),
            input is { } inputPrice && output is { } outputPrice ? new ModelPrice(inputPrice, outputPrice) : null,
            [.. Enum.GetValues<AiRole>().Where(IsRecommended)]);

        return (edit, fields);
    }

    private void SetCapability(ModelCapability capability, string value)
    {
        switch (capability)
        {
            case ModelCapability.ImageInput: ImageInput = value; break;
            case ModelCapability.StructuredOutput: StructuredOutput = value; break;
            case ModelCapability.ToolUse: ToolUse = value; break;
            case ModelCapability.LiveSearch: LiveSearch = value; break;
            case ModelCapability.ReasoningEffort: ReasoningEffort = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(capability), capability, null);
        }
    }

    private void SetRecommended(AiRole role, bool value)
    {
        switch (role)
        {
            case AiRole.TechnicalAnalyst: TechnicalAnalyst = value; break;
            case AiRole.SentimentAnalyst: SentimentAnalyst = value; break;
            case AiRole.Strategist: Strategist = value; break;
            case AiRole.RiskCritic: RiskCritic = value; break;
            case AiRole.Reviewer: Reviewer = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(role), role, null);
        }
    }

    /// <summary>Anything but <c>true</c> or <c>false</c> inherits, as the select's first option does.</summary>
    private static bool? Flag(string? value) => value switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    /// <summary>As the configuration writes it: digits and a point, such as <c>2.5</c>, from 0.</summary>
    private static decimal? Price(string? value, ModelEditField field, HashSet<ModelEditField> fields)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (decimal.TryParse(value.Trim(), System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var price))
        {
            return price;
        }

        fields.Add(field);
        return null;
    }
}

/// <summary>
/// The roles' defaults form (FR-ROLE-03). A provider of <see cref="None"/> leaves the role with no default, and a
/// blank model means the provider's best that the user can use.
/// </summary>
public sealed class RoleDefaultsForm
{
    public const string None = "";

    public string? TechnicalAnalystProvider { get; set; }

    public string? TechnicalAnalystModel { get; set; }

    public string? SentimentAnalystProvider { get; set; }

    public string? SentimentAnalystModel { get; set; }

    public string? StrategistProvider { get; set; }

    public string? StrategistModel { get; set; }

    public string? RiskCriticProvider { get; set; }

    public string? RiskCriticModel { get; set; }

    public string? ReviewerProvider { get; set; }

    public string? ReviewerModel { get; set; }

    public static RoleDefaultsForm Of(IReadOnlyList<RoleDefault> defaults)
    {
        var form = new RoleDefaultsForm();

        foreach (var value in defaults)
        {
            form.Set(value.Role, value.Provider, value.ModelId);
        }

        return form;
    }

    public string? ProviderOf(AiRole role) => Values(role).Provider;

    public string? ModelOf(AiRole role) => Values(role).Model;

    /// <summary>Every role, as posted. Blank text is no value.</summary>
    public IReadOnlyList<RoleDefaultEdit> ToEdits() =>
    [
        .. Enum.GetValues<AiRole>().Select(role => new RoleDefaultEdit(
            role,
            string.IsNullOrWhiteSpace(ProviderOf(role)) ? null : ProviderOf(role),
            string.IsNullOrWhiteSpace(ModelOf(role)) ? null : ModelOf(role))),
    ];

    private (string? Provider, string? Model) Values(AiRole role) => role switch
    {
        AiRole.TechnicalAnalyst => (TechnicalAnalystProvider, TechnicalAnalystModel),
        AiRole.SentimentAnalyst => (SentimentAnalystProvider, SentimentAnalystModel),
        AiRole.Strategist => (StrategistProvider, StrategistModel),
        AiRole.RiskCritic => (RiskCriticProvider, RiskCriticModel),
        AiRole.Reviewer => (ReviewerProvider, ReviewerModel),
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private void Set(AiRole role, string? provider, string? model)
    {
        switch (role)
        {
            case AiRole.TechnicalAnalyst: (TechnicalAnalystProvider, TechnicalAnalystModel) = (provider, model); break;
            case AiRole.SentimentAnalyst: (SentimentAnalystProvider, SentimentAnalystModel) = (provider, model); break;
            case AiRole.Strategist: (StrategistProvider, StrategistModel) = (provider, model); break;
            case AiRole.RiskCritic: (RiskCriticProvider, RiskCriticModel) = (provider, model); break;
            case AiRole.Reviewer: (ReviewerProvider, ReviewerModel) = (provider, model); break;
            default: throw new ArgumentOutOfRangeException(nameof(role), role, null);
        }
    }
}
