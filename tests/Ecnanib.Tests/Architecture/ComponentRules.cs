namespace Ecnanib.Tests.Architecture;

/// <summary>
/// The architecture rules of the application, in one place. This file is the source of truth:
/// <c>docs/architecture.md</c> explains the rules, and <see cref="DependencyRuleTests"/> enforces them.
///
/// To change a rule, edit this file. The dependency graph must stay free of cycles.
/// </summary>
internal static class ComponentRules
{
    // Foundation. Not SRS components: shared vocabulary, and the cross-cutting plumbing
    // (configuration, persistence, logging, background work) that every component builds on.
    public const string Shared = "Shared";
    public const string Platform = "Platform";

    // The eleven logical components of SRS section 3.1. Each is the namespace Ecnanib.<Name>.
    public const string WebUi = "WebUi";
    public const string Accounts = "Accounts";
    public const string Credentials = "Credentials";
    public const string Catalog = "Catalog";
    public const string Agents = "Agents";
    public const string DecisionEngine = "DecisionEngine";
    public const string RiskGuard = "RiskGuard";
    public const string AiProviderGateway = "AiProviderGateway";
    public const string ExchangeGateway = "ExchangeGateway";
    public const string Experience = "Experience";
    public const string Notifications = "Notifications";

    // The HTTP API. Like the Web UI, it is an entry point on top of the components.
    public const string Api = "Api";

    public static readonly IReadOnlyList<string> SrsComponents =
    [
        WebUi, Accounts, Credentials, Catalog, Agents, DecisionEngine, RiskGuard,
        AiProviderGateway, ExchangeGateway, Experience, Notifications,
    ];

    public static readonly RuleSet Rules = new(
        RootNamespace: "Ecnanib",

        // For each area, the other areas its code may reference. Anything not listed is forbidden.
        AllowedDependencies: new Dictionary<string, IReadOnlySet<string>>
        {
            [Shared] = Only(),
            [Platform] = Only(Shared),

            // Leaves. The gateways receive keys with each call and never fetch them, and the
            // Risk Guard is rules only, so none of them needs another component.
            [Accounts] = Component(),
            [AiProviderGateway] = Component(),
            [ExchangeGateway] = Component(),
            [RiskGuard] = Component(),

            [Notifications] = Component(Accounts),
            [Catalog] = Component(AiProviderGateway),
            [Experience] = Component(Catalog, ExchangeGateway),
            [Credentials] = Component(Accounts, Notifications, Catalog, AiProviderGateway, ExchangeGateway),
            [Agents] = Component(
                Accounts, Notifications, Credentials, Catalog, Experience, RiskGuard, ExchangeGateway),

            // No Accounts here: the engine composes prompts and must not see personal data (FR-PRM-9).
            [DecisionEngine] = Component(
                Agents, Credentials, Catalog, Experience, RiskGuard, Notifications,
                AiProviderGateway, ExchangeGateway),

            // Entry points. They reach AI providers only through other components.
            [WebUi] = Component(
                Accounts, Notifications, Credentials, Catalog, Experience, Agents, RiskGuard,
                DecisionEngine, ExchangeGateway),
            [Api] = Component(
                Accounts, Notifications, Credentials, Catalog, Experience, Agents, RiskGuard,
                DecisionEngine, ExchangeGateway),
        },

        // Namespaces with a narrower audience than the component they belong to. Besides these,
        // every <Component>.Adapters namespace is private to its component.
        RestrictedNamespaces: new Dictionary<string, IReadOnlySet<string>>
        {
            // Order placement. Only the Decision Engine may place orders, after schema
            // validation and the Risk Guard (NFR-SEC-6).
            ["ExchangeGateway.Trading"] = Only(ExchangeGateway, DecisionEngine),
        },

        // Types that talk to another system over the network. They may be used only inside an
        // Adapters namespace, so every external system is reached through its adapter.
        OutboundNetworkNamespaces:
        [
            "System.Net.Http",
            "System.Net.Mail",
            "System.Net.WebSockets",
            "System.Net.Sockets",

            // The SMTP client library of the email adapter.
            "MailKit",
        ],

        // Namespaces outside the application that only Platform may use.
        ReservedNamespaces: new Dictionary<string, IReadOnlySet<string>>
        {
            // The host's configuration API. Every other area takes typed settings, which come
            // from the one configuration file, so a setting cannot reach it from an environment
            // variable or a command-line argument (NFR-CFG-2).
            ["Microsoft.Extensions.Configuration"] = Only(Platform),

            // The logging library. Every other area writes through the host's ILogger, so no
            // event can reach an output without passing the one place that masks secrets
            // (NFR-LOG-4).
            ["Serilog"] = Only(Platform),

            // The database driver. Every other area reaches the database through the context
            // the platform registers, so there is one connection to one database, with one
            // model and one set of migrations (NFR-DAT-1, NFR-DAT-2).
            ["Npgsql"] = Only(Platform),
        },

        // Methods that no code may call.
        ForbiddenCalls:
        [
            // Direct access to the process environment, so the application adds no environment
            // variable or command-line argument of its own (NFR-CFG-3).
            "System.Environment::GetEnvironmentVariable",
            "System.Environment::SetEnvironmentVariable",
            "System.Environment::ExpandEnvironmentVariables",
            "System.Environment::GetCommandLineArgs",
            "System.Environment::get_CommandLine",

            // Creating an HTTP client. An adapter takes one from the host's factory, which logs
            // every call it makes (NFR-LOG-5).
            "System.Net.Http.HttpClient::.ctor",
        ]);

    private static IReadOnlySet<string> Only(params string[] areas) => new HashSet<string>(areas);

    private static IReadOnlySet<string> Component(params string[] components) =>
        new HashSet<string>([Shared, Platform, .. components]);
}
