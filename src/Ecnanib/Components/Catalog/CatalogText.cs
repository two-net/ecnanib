using System.Globalization;
using Ecnanib.Components.Agents;
using Ecnanib.Modules.Catalog.Contracts;

namespace Ecnanib.Components.Catalog;

/// <summary>
/// The model catalog's text: statuses, capabilities, triggers and outcomes. Public so that a test can prove
/// every value has text of its own.
/// </summary>
public static class CatalogText
{
    /// <summary>A provider's product name, for the four the product launched with. Any other shows as its name.</summary>
    public static string ProviderOf(string provider) => provider switch
    {
        "Google" => Strings.ProviderGoogle,
        "xAI" => Strings.ProviderXai,
        "OpenAI" => Strings.ProviderOpenAi,
        "Anthropic" => Strings.ProviderAnthropic,
        _ => provider,
    };

    public static string StatusOf(ModelStatus status) => status switch
    {
        ModelStatus.Active => Strings.CatalogStatusActive,
        ModelStatus.Deprecated => Strings.CatalogStatusDeprecated,
        ModelStatus.Retired => Strings.CatalogStatusRetired,
        ModelStatus.Hidden => Strings.CatalogStatusHidden,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Give the status its text in Strings.resx."),
    };

    public static string CapabilityOf(ModelCapability capability) => capability switch
    {
        ModelCapability.ImageInput => Strings.CatalogCapabilityImageInput,
        ModelCapability.StructuredOutput => Strings.CatalogCapabilityStructuredOutput,
        ModelCapability.ToolUse => Strings.CatalogCapabilityToolUse,
        ModelCapability.LiveSearch => Strings.CatalogCapabilityLiveSearch,
        ModelCapability.ReasoningEffort => Strings.CatalogCapabilityReasoningEffort,
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, "Give the capability its text in Strings.resx."),
    };

    public static string TriggerOf(CatalogSyncTrigger trigger) => trigger switch
    {
        CatalogSyncTrigger.Schedule => Strings.CatalogTriggerSchedule,
        CatalogSyncTrigger.Administrator => Strings.CatalogTriggerAdministrator,
        CatalogSyncTrigger.Credential => Strings.CatalogTriggerCredential,
        CatalogSyncTrigger.User => Strings.CatalogTriggerUser,
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Give the trigger its text in Strings.resx."),
    };

    public static string SyncAllOf(SyncAllOutcome outcome) => outcome switch
    {
        SyncAllOutcome.Started => Strings.AdminCatalogSyncStarted,
        SyncAllOutcome.AlreadyRunning => Strings.AdminCatalogSyncRunning,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Give the outcome its text in Strings.resx."),
    };

    public static string FieldErrorOf(ModelEditField field) => field switch
    {
        ModelEditField.DisplayName => Strings.AdminModelDisplayNameError,
        ModelEditField.InputPrice => Strings.AdminModelInputPriceError,
        ModelEditField.OutputPrice => Strings.AdminModelOutputPriceError,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Give the field its text in Strings.resx."),
    };

    public static string RoleDefaultProblemOf(RoleDefaultProblem problem) => problem switch
    {
        RoleDefaultProblem.UnknownProvider => Strings.AdminCatalogDefaultUnknownProvider,
        RoleDefaultProblem.InvalidModel => Strings.AdminCatalogDefaultInvalidModel,
        RoleDefaultProblem.ModelWithoutProvider => Strings.AdminCatalogDefaultModelWithoutProvider,
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, "Give the problem its text in Strings.resx."),
    };

    /// <summary>What a capability's value says: yes, no, or that nobody said.</summary>
    public static string FlagOf(bool? value) => value switch
    {
        true => Strings.CatalogYes,
        false => Strings.CatalogNo,
        null => Strings.CatalogNotStated,
    };

    /// <summary>Input and output, in US dollars per million tokens, or that it isn't known.</summary>
    public static string PriceOf(ModelPrice? price) =>
        price is null
            ? Strings.CatalogPriceUnknown
            : string.Format(CultureInfo.CurrentCulture, Strings.CatalogPrice, price.InputPerMillion, price.OutputPerMillion);

    /// <summary>The context window and maximum output that are known, or that nobody said either.</summary>
    public static string TokensOf(int? contextWindow, int? maxOutputTokens)
    {
        string[] known =
        [
            .. contextWindow is { } context ? [string.Format(CultureInfo.CurrentCulture, Strings.CatalogTokensContext, context)] : Array.Empty<string>(),
            .. maxOutputTokens is { } output ? [string.Format(CultureInfo.CurrentCulture, Strings.CatalogTokensOutput, output)] : Array.Empty<string>(),
        ];

        return known.Length == 0 ? Strings.CatalogNotStated : string.Join(", ", known);
    }

    /// <summary>The roles, by name, or that there are none.</summary>
    public static string RolesOf(IReadOnlyCollection<Kernel.AiRole> roles) =>
        roles.Count == 0 ? Strings.CatalogNoRoles : string.Join(", ", roles.Select(AgentText.RoleOf));
}
