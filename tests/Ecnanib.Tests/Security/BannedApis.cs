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

    private static readonly string[] NoExceptions = [];
    private static readonly string[] Adapters = ["Modules.ExchangeAdapter.", "Modules.AiAdapters."];
    private static readonly string[] Email = ["Platform.Email."];
    private static readonly string[] Execution = ["Modules.Execution."];
    private static readonly string[] Council = ["Modules.AiCouncil."];

    /// <summary>The secrets' owners, and Platform, which implements the store. Administration may use Platform, but not this.</summary>
    private static readonly string[] SecretOwners = ["Modules.Identity.", "Modules.Credentials.", "Platform."];

    /// <summary>Identity, which implements it, and Notifications, which emails them. Not the Web UI or Administration.</summary>
    private static readonly string[] AddressReaders = ["Modules.Identity.", "Modules.Notifications."];

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
