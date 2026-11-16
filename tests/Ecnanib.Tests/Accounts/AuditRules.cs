using System.Reflection;
using Ecnanib.Accounts;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// How one of the actions that the requirement names is recorded: by members of
/// <see cref="AuditAction"/>, or not yet, because what does the action is not built.
/// </summary>
/// <param name="RecordedAs">The members that record it. Empty while it waits.</param>
/// <param name="WaitsFor">
/// What it waits for: the namespace of the component that will do the action,
/// <see cref="AuditRules.LiveSwitch"/>, or <see cref="AuditRules.OperatorFunctions"/>. Null
/// when it is recorded.
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
    /// <summary>
    /// What the switches of an agent's mode wait for: the first type of Agents that asks for a
    /// fresh proof of who its user is, which switching an agent to Live takes (FR-AGT-7). Agents
    /// starts and stops agents, and switches none: every agent is in Demo. Nothing else of
    /// Agents takes that proof.
    /// </summary>
    public const string LiveSwitch = "the switch of an agent to Live";

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

        // Of an AI provider or of Binance. The event names the key by its id, and never holds it.
        // The last one is the service's own doing: a stored key that turned out to be able to
        // withdraw, which it deletes (FR-KEY-4).
        ["key changes"] = AuditCoverage.By(
            AuditAction.KeyAdded,
            AuditAction.KeyReplaced,
            AuditAction.KeyDeleted,
            AuditAction.KeyRejected),

        // What a user does to the state of an agent: starting, pausing, resuming, which is a
        // start, and stopping it. The event names the agent by its id. What the service does to
        // the state of an agent is no action of a user: the changes of the state record it.
        ["agent start, pause, and stop"] = AuditCoverage.By(
            AuditAction.AgentStarted,
            AuditAction.AgentResumed,
            AuditAction.AgentPaused,
            AuditAction.AgentStopped),

        ["mode switches"] = AuditCoverage.Waiting(
            LiveSwitch,
            "Switching an agent between Demo and Live (FR-AGT-7)."),

        // Closing a position by hand: with "Close position now", or as the agent is stopped
        // (FR-AGT-11, FR-AGT-12). Agents takes the request and records it, before the order.
        // What places the order may not use Accounts, and records nothing.
        ["manual closes"] = AuditCoverage.By(AuditAction.AgentPositionCloseRequested),

        // Of an agent, when a form that changes one of them is saved. The event names the agent
        // by its id, and holds no number. Setting them for the first time, as an agent is
        // created or copied, changes none.
        ["risk setting changes"] = AuditCoverage.By(AuditAction.AgentRiskSettingsChanged),

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
    /// The types of a namespace of an assembly that take a service: each that has a constructor
    /// with a parameter of the service's type, public or not.
    /// </summary>
    public static IReadOnlyList<string> FindTakersOf(Type service, Assembly assembly, string @namespace) =>
    [
        .. from type in assembly.GetTypes()
           where type.Namespace is { } name
               && (name == @namespace || name.StartsWith(@namespace + ".", StringComparison.Ordinal))
           where type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
               .SelectMany(constructor => constructor.GetParameters())
               .Any(parameter => parameter.ParameterType == service)
           orderby type.FullName
           select type.FullName!,
    ];

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
