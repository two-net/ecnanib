using Ecnanib.Ai.Abstractions;
using Ecnanib.Keys.Abstractions;

namespace Ecnanib.Components.Keys;

/// <summary>User-facing texts shared by the keys pages. None of them ever carries a key value (FR-KEY-04).</summary>
internal static class KeyMessages
{
    public const string PaperAgentsNeedNoKey = "Paper-mode agents need no Binance key. A live agent needs a key that allows its market.";

    public const string BinanceKeyHint =
        "Create the key on Binance with reading and trading permissions and \"Enable Withdrawals\" off: a key that can withdraw is refused. If the key is restricted to IP addresses, allow this server's address.";

    public const string TestnetPermissions = "The testnet does not report key permissions; the key was proven by authenticated requests only.";

    public static string ProviderUnavailable(AiProvider provider) =>
        $"No key. Council roles on {ProviderNames.Display(provider)} are unavailable until you add one.";

    public static string AgentsInUse(int agents) =>
        $"{Plural(agents, "running or paused agent uses", "running or paused agents use")} this key. Stop them first, or tick the box to stop them now.";

    public static string AgentsStopped(int agents) => agents == 0 ? string.Empty : $" {Plural(agents, "agent was", "agents were")} stopped.";

    public static string ForSave(KeySaveResult result, string service) => result.Status switch
    {
        KeySaveStatus.InvalidInput => result.Detail ?? "Check the values you entered.",
        KeySaveStatus.NoMarketSelected => "Select at least one market.",
        KeySaveStatus.LabelTaken => "You already have a key with this label.",
        KeySaveStatus.WithdrawalEnabled => result.Detail ?? "Binance reports withdrawal permission on this key; it was not saved.",
        KeySaveStatus.Rejected => $"{service} rejected the key{Detail(result.Detail)} It was not saved.",
        KeySaveStatus.Failed => $"The key could not be validated{Detail(result.Detail)} It was not saved; try again.",
        KeySaveStatus.NotFound => "The key no longer exists.",
        _ => string.Empty,
    };

    public static string ForValidation(KeyValidationResult result, string service) => result.Outcome switch
    {
        KeyValidationOutcome.Valid => $"{service} accepted the key.{(result.ModelCount > 0 ? $" {Plural(result.ModelCount, "model", "models")} listed." : string.Empty)}",
        KeyValidationOutcome.Invalid => $"{service} rejected the key{Detail(result.Detail)}{(result.AgentsFailed > 0 ? $" {Plural(result.AgentsFailed, "agent", "agents")} entered Error." : string.Empty)}",
        KeyValidationOutcome.Failed => $"The key could not be checked{Detail(result.Detail)} Its status is unchanged; try again.",
        _ => "The key no longer exists.",
    };

    public static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    private static string Detail(string? detail) => string.IsNullOrWhiteSpace(detail) ? "." : $": {detail.TrimEnd('.')}.";
}
