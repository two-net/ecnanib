using System.Reflection;
using Ecnanib.Accounts;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// How one of the actions that the requirement names is recorded: by members of
/// <see cref="AuditAction"/>, or not yet, because what does the action is not built.
/// </summary>
/// <param name="RecordedAs">The members that record it. Empty while it waits.</param>
/// <param name="WaitsFor">
/// What it waits for: the namespace of the component that will do the action, or
/// <see cref="AuditRules.OperatorFunctions"/>. Null when it is recorded.
/// </param>
/// <param name="What">For an action that waits: what is to be recorded, and by whom.</param>
internal sealed record AuditCoverage(IReadOnlyList<AuditAction> RecordedAs, string? WaitsFor, string? What)
{
    public static AuditCoverage By(params AuditAction[] members) => new(members, WaitsFor: null, What: null);

    public static AuditCoverage Waiting(string waitsFor, string what) => new([], waitsFor, what);
}

/// <summary>
/// What the audit trail records, in one place (NFR-LOG-7). This file is the source of truth:
/// <c>docs/accounts.md</c> explains the rules, and <see cref="AuditRulesTests"/> enforces them.
/// </summary>
/// <remarks>
/// The requirement names actions of components that are not built yet. Each of them is listed
/// here with what it waits for, and fails a test as soon as that exists. To record it then, add
/// its members to <see cref="AuditAction"/>, record them where the action is done, mark the test
/// that shows it with <see cref="ShowsAuditOfAttribute"/>, and list the members here.
/// </remarks>
internal static class AuditRules
{
    /// <summary>The namespace of the component that keeps the users' keys.</summary>
    public const string Credentials = "Ecnanib.Credentials";

    /// <summary>The namespace of the component that keeps the agents.</summary>
    public const string Agents = "Ecnanib.Agents";

    /// <summary>
    /// What the actions of operators wait for: the first function that asks whether its user is
    /// an operator. They have no component of their own.
    /// </summary>
    public const string OperatorFunctions = "the operator functions";

    /// <summary>
    /// The actions that NFR-LOG-7 names, each with how it is recorded. An entry stands on its
    /// own: one of them can be recorded while another still waits.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, AuditCoverage> Requirement = new Dictionary<string, AuditCoverage>
    {
        ["sign-ins"] = AuditCoverage.By(AuditAction.SignedIn),

        ["two-factor changes"] = AuditCoverage.By(
            AuditAction.TwoFactorTurnedOn,
            AuditAction.TwoFactorTurnedOff,
            AuditAction.TwoFactorRecoveryCodesReplaced,
            AuditAction.TwoFactorRecoveryCodeUsed),

        ["key changes"] = AuditCoverage.Waiting(
            Credentials,
            "Adding, replacing, and deleting a key of an AI provider or of Binance. The event names the key by its id, and never holds it."),

        ["agent start, pause, and stop"] = AuditCoverage.Waiting(
            Agents,
            "What a user does to the state of an agent: starting, pausing, resuming, which is a start, and stopping it."),

        ["mode switches"] = AuditCoverage.Waiting(
            Agents,
            "Switching an agent between Demo and Live (FR-AGT-7)."),

        ["manual closes"] = AuditCoverage.Waiting(
            Agents,
            "Closing a position by hand (FR-AGT-12). Agents takes the request and records it. The Decision Engine places the order, and may not use Accounts."),

        ["risk setting changes"] = AuditCoverage.Waiting(
            Agents,
            "Changing the risk settings of an agent."),

        ["operator actions"] = AuditCoverage.Waiting(
            OperatorFunctions,
            "Switching the global halt, disabling and enabling an account, and triggering a catalog sync (FR-OPS-3 to FR-OPS-5)."),
    };

    /// <summary>
    /// The actions that are recorded though the requirement does not name them, each with why.
    /// </summary>
    public static readonly IReadOnlyDictionary<AuditAction, string> BeyondTheRequirement = new Dictionary<AuditAction, string>
    {
        [AuditAction.Reauthenticated] =
            "The proof before a sensitive action is what signing in takes, given again: it says who was at the browser when keys or money were at stake.",
        [AuditAction.PasswordChanged] =
            "A new password by a link from the mail is how an account is taken over by whoever reads that mail.",
    };

    /// <summary>
    /// The members of some types that take text, or anything at all, from whoever uses them:
    /// each constructor and method that is not private and has a parameter of <c>string</c> or
    /// <c>object</c>. What makes an audit event has none, so an event cannot hold a secret.
    /// </summary>
    public static IReadOnlyList<string> FindTextTakers(params Type[] types)
    {
        const BindingFlags declared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        return
        [
            .. from type in types
               from member in type.GetConstructors(declared).Cast<MethodBase>().Concat(type.GetMethods(declared))
               where !member.IsPrivate
               from parameter in member.GetParameters()
               where parameter.ParameterType == typeof(string) || parameter.ParameterType == typeof(object)
               orderby type.Name, member.Name, parameter.Position
               select $"{type.Name}.{member.Name}: takes {parameter.ParameterType.Name} {parameter.Name}",
        ];
    }
}

/// <summary>
/// Marks a test which shows that an action is recorded in the audit trail when it is done
/// (NFR-LOG-7). Every member of <see cref="AuditAction"/> needs at least one, which
/// <see cref="AuditRulesTests"/> checks.
/// </summary>
/// <param name="actions">The actions the test is about.</param>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ShowsAuditOfAttribute(params AuditAction[] actions) : Attribute
{
    public IReadOnlyList<AuditAction> Actions { get; } = actions;
}
