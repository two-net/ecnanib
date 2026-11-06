using Mono.Cecil;
using ReflectionAssembly = System.Reflection.Assembly;

namespace Ecnanib.Tests.Security;

/// <summary>A reference in a method body that a rule forbids there.</summary>
internal sealed record BannedUse(string Source, string Member, string Risk)
{
    public override string ToString() => $"{Source} -> {Member}: {Risk}";
}

/// <summary>
/// NFR-SEC-03. Most of what these protect doesn't exist yet, so each is a rule the feature has to
/// meet when it lands. It reads method bodies, so it sees Razor markup, lambdas and async methods. It
/// takes the assembly and root namespace as parameters so that BannedApiGuardTests can prove it
/// catches real uses. See docs/architecture.md.
/// </summary>
internal sealed class BannedApis(ReflectionAssembly assembly, string rootNamespace)
{
    private const string Xss = "renders text as HTML, and AI output and user input reach the UI";
    private const string Injection = "builds SQL from a string. Use the interpolated form, which parameterises";
    private const string Csrf = "turns off cross-site request forgery protection";
    private const string Ssrf = "reaches the network, which only the adapters may do, using URLs from the configuration file";
    private const string SecretAccess = "stores or reveals a user's secret, which only Identity and Credentials may do (FR-KEY-09, FR-ADM-01)";
    private const string Smtp = "reaches an SMTP server, which only Platform/Email may do, requiring TLS, using the server from the configuration file";
    private const string Addresses = "hands out other users' addresses, which only Notifications may ask for (FR-NTF-02)";
    private const string LegacySmtp = "is the SMTP client the framework no longer recommends, which can't require STARTTLS. Use IEmailSender";
    private const string Orders = "places or cancels orders, which only Execution may do, after a passing Risk Engine evaluation (AC-01)";
    private const string Spending = "calls a model on a user's key, which spends their money, and only AiCouncil may do that, inside the agent's budget (FR-CYC-07)";
    private const string KeyBuilt =
        "builds a key for an adapter, which only Credentials may do, from a user's own credential that passed its checks (CON-11, FR-KEY-05)";
    private const string KeyOpened =
        "decrypts a user's key for use, which only the modules that call providers with it may do: Catalog, AiCouncil and Execution (FR-KEY-09)";

    private const string Unchecked =
        "makes an order the Risk Engine didn't size, which only the engine may do, so that nothing reaches the exchange "
        + "without a passing evaluation (FR-RISK-01, AC-01)";

    private const string CatalogWide =
        "syncs every user's keys, overrides models' metadata or changes the roles' defaults, which only an administrator may do, "
        + "and Administration checks that before it calls (FR-ADM-03)";

    private const string EveryAgent =
        "lists every user's agents, which only the scheduler may do, to run their cycles (NFR-SEC-04)";

    private const string CycleStarted =
        "starts a decision cycle, which only the scheduler may do, so that each goes through its limit, its queue and "
        + "one cycle per agent (FR-CYC-08)";

    private const string CouncilAsked =
        "asks the AI council, which spends the user's money, and only the decision cycle may, inside the agent's budget (FR-CYC-07)";

    private const string AccountChanged =
        "changes a Futures symbol's margin type and leverage on a user's account, which only order execution may do, before "
        + "an entry that passed the Risk Engine (FR-RISK-09, AC-01)";

    private const string OrderSent =
        "sends a sized order to Binance, which only the decision cycle may, for an Autonomous agent or an approved trade, "
        + "after a passing Risk Engine evaluation (AC-01, FR-APR-01)";

    private static readonly string[] NoExceptions = [];
    private static readonly string[] Adapters = ["Modules.ExchangeAdapter.", "Modules.AiAdapters."];
    private static readonly string[] Email = ["Platform.Email."];
    private static readonly string[] Execution = ["Modules.Execution."];
    private static readonly string[] Council = ["Modules.AiCouncil."];
    private static readonly string[] RiskEngine = ["Modules.RiskEngine."];

    /// <summary>The one module that turns a user's credential into a key.</summary>
    private static readonly string[] Credentials = ["Modules.Credentials."];

    /// <summary>Credentials, which implements it, and the modules that call providers with a user's key.</summary>
    private static readonly string[] KeyUsers = ["Modules.Credentials.", "Modules.Catalog.", "Modules.AiCouncil.", "Modules.Execution."];

    /// <summary>Catalog, which implements it, and Administration, which checks the caller is an administrator first.</summary>
    private static readonly string[] CatalogAdministrators = ["Modules.Catalog.", "Modules.Administration."];

    /// <summary>The secrets' owners, and Platform, which implements the store. Administration may use Platform, but not this.</summary>
    private static readonly string[] SecretOwners = ["Modules.Identity.", "Modules.Credentials.", "Platform."];

    /// <summary>Identity, which implements it, and Notifications, which emails them. Not the Web UI or Administration.</summary>
    private static readonly string[] AddressReaders = ["Modules.Identity.", "Modules.Notifications."];

    /// <summary>Agents, which implements it, and the scheduler, which runs every Active agent.</summary>
    private static readonly string[] AgentListers = ["Modules.Agents.", "Modules.Scheduler."];

    /// <summary>DecisionCycle, which implements it, and the scheduler, which starts every cycle.</summary>
    private static readonly string[] CycleStarters = ["Modules.DecisionCycle.", "Modules.Scheduler."];

    /// <summary>AiCouncil, which implements it, and the decision cycle, which asks each role.</summary>
    private static readonly string[] CouncilAskers = ["Modules.AiCouncil.", "Modules.DecisionCycle."];

    /// <summary>Execution, which implements it and sends entries, and the decision cycle, which approvals (#38) belong to.</summary>
    private static readonly string[] EntrySenders = ["Modules.Execution.", "Modules.DecisionCycle."];

    private static readonly (string Reference, string Risk, string[] AllowedIn)[] Rules =
    [
        ("Microsoft.AspNetCore.Components.MarkupString", Xss, NoExceptions),
        ("::FromSqlRaw", Injection, NoExceptions),
        ("::ExecuteSqlRaw", Injection, NoExceptions),
        ("::SqlQueryRaw", Injection, NoExceptions),
        ("::DisableAntiforgery", Csrf, NoExceptions),
        ("System.Net.Http.HttpClient", Ssrf, Adapters),
        ("System.Net.Http.IHttpClientFactory", Ssrf, Adapters),
        (".Platform.Contracts.ISecretStore::", SecretAccess, SecretOwners),
        (".Modules.Identity.Contracts.IEmailRecipients::", Addresses, AddressReaders),
        ("MailKit.Net.Smtp.SmtpClient", Smtp, Email),
        ("System.Net.Mail.SmtpClient", LegacySmtp, NoExceptions),
        (".Modules.ExchangeAdapter.Contracts.IExchangeTrading::", Orders, Execution),
        (".Modules.AiAdapters.Contracts.IAiCompletions::", Spending, Council),
        (".Modules.AiAdapters.Contracts.AiKey::.ctor", KeyBuilt, Credentials),
        (".Modules.ExchangeAdapter.Contracts.ExchangeKey::.ctor", KeyBuilt, Credentials),
        (".Modules.Credentials.Contracts.ICredentialKeys::", KeyOpened, KeyUsers),
        (".Modules.Catalog.Contracts.ICatalogAdministration::", CatalogWide, CatalogAdministrators),
        (".Modules.RiskEngine.Contracts.SizedOrder::.ctor", Unchecked, RiskEngine),
        (".Modules.Agents.Contracts.IActiveAgents::", EveryAgent, AgentListers),
        (".Modules.DecisionCycle.Contracts.IDecisionCycles::", CycleStarted, CycleStarters),
        (".Modules.AiCouncil.Contracts.ICouncil::", CouncilAsked, CouncilAskers),
        (".Modules.Execution.Contracts.IFuturesSetup::", AccountChanged, EntrySenders),
        (".Modules.Execution.Contracts.IOrderExecution::", OrderSent, EntrySenders),
    ];

    private readonly string _prefix = rootNamespace + ".";

    public List<BannedUse> FindUses()
    {
        using var module = ModuleDefinition.ReadModule(assembly.Location);

        return
        [
            .. (from type in module.GetTypes()
                where type.FullName.StartsWith(_prefix, StringComparison.Ordinal)
                from method in type.Methods
                where method.HasBody
                from instruction in method.Body.Instructions
                where instruction.Operand is MemberReference
                let member = ((MemberReference)instruction.Operand).FullName
                from rule in Rules
                where member.Contains(rule.Reference, StringComparison.Ordinal)
                    && !rule.AllowedIn.Any(allowed => type.FullName.StartsWith(_prefix + allowed, StringComparison.Ordinal))
                select new BannedUse(type.FullName, member, rule.Risk)).Distinct(),
        ];
    }
}
