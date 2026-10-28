using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ecnanib.Configuration;
using Ecnanib.Database;
using Ecnanib.Logging;
using Ecnanib.Tests.Database;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Configuration;

/// <summary>
/// NFR-ARCH-01/02 and C-10 as rules the tree must keep: one deployable process over one PostgreSQL database (DB-01)
/// with every background job a hosted service of a known module; the modules the SRS names, each a folder with its
/// registration method and README, reaching one another through their contracts only. The lists below are the
/// architecture as it stands: adding a module, a job or a dependency is a conscious change here.
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly Application = typeof(EcnanibHost).Assembly;

    /// <summary>Every module folder under <c>src/Ecnanib</c>: the SRS's eleven (NFR-ARCH-02) and the five that support them (Agents, Ai, Database, Exchange, Health).</summary>
    private static readonly string[] Modules =
    [
        "Accounts", "Agents", "Ai", "Configuration", "Council", "Database", "Exchange", "Execution", "Experience", "Health",
        "Keys", "Logging", "MarketData", "ModelSync", "Strategies", "Ui",
    ];

    /// <summary>NFR-ARCH-02 by folder: accounts, keys, configuration, market data, strategy engine, council, model sync, experience, execution, logging, UI.</summary>
    private static readonly string[] SrsModules =
    [
        "Accounts", "Keys", "Configuration", "MarketData", "Strategies", "Council", "ModelSync", "Experience", "Execution", "Logging", "Ui",
    ];

    /// <summary>
    /// Every background job, in the order <c>AddLogSinks</c> and <c>AddEcnanibModules</c> register them (NFR-ARCH-01):
    /// the database log writer first, so it stops last and still writes the others' shutdown logs (FR-LOG-04).
    /// </summary>
    private static readonly string[] HostedServiceOrder =
    [
        "Ecnanib.Logging.Services.DatabaseLogWriter",
        "Ecnanib.Exchange.Streaming.MarketStreamHub",
        "Ecnanib.Exchange.Streaming.UserDataStreamHub",
        "Ecnanib.Exchange.Binance.BinanceRateLimitMonitor",
        "Ecnanib.Database.InstanceLock",
        "Ecnanib.Database.Retention.RetentionSweeper",
        "Ecnanib.ModelSync.Services.ModelSyncScheduler",
        "Ecnanib.Experience.Services.ExperienceRefresher",
        "Ecnanib.MarketData.Services.SymbolCatalogRefresher",
        "Ecnanib.Execution.Services.Live.ExchangeReconciler",
        "Ecnanib.Execution.Services.Live.LiveFillTracker",
        "Ecnanib.Agents.Services.Paper.PaperFillSimulator",
        "Ecnanib.Agents.Services.AgentScheduler",
        "Ecnanib.Keys.Services.KeyRevalidator",
    ];

    /// <summary>A module's pages live under <c>Components</c> next to the module; they are part of it and may use its internals.</summary>
    private static readonly Dictionary<string, string> ComponentOwners = new(StringComparer.Ordinal)
    {
        ["Account"] = "Accounts",
        ["Agents"] = "Agents",
        ["Council"] = "Council",
        ["Experience"] = "Experience",
        ["Health"] = "Health",
        ["Keys"] = "Keys",
        ["Logging"] = "Logging",
        ["Providers"] = "ModelSync",
    };

    private static readonly string[] NonModuleFolders = ["Components", "Properties", "wwwroot", "obj", "bin"];

    /// <summary>A fully qualified <c>Ecnanib.&lt;Module&gt;.&lt;Segment&gt;…</c> mention outside a string literal; a two-segment module root never matches.</summary>
    private static readonly Regex QualifiedReference = new(@"(?<![\w.""])Ecnanib\.(?<module>[A-Z]\w*)\.(?<segment>[A-Z]\w*)(?<rest>(?:\.[A-Z]\w*)*)", RegexOptions.Compiled);

    private static readonly Regex NamespaceDeclaration = new(@"^\s*namespace\s+([\w.]+)\s*[;{]", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>NFR-ARCH-01: one deployable application; the test project is an executable only because the test runner needs one.</summary>
    [Fact]
    public void TheSolutionHasExactlyOneDeployableProjectAndItIsTheWebApplication()
    {
        var solution = XDocument.Load(Path.Combine(SourceTree.RepoRoot, "Ecnanib.slnx"));
        var projects = solution.Descendants("Project").Select(p => p.Attribute("Path")!.Value).ToList();
        Assert.Equal(2, projects.Count);

        var application = Assert.Single(projects, p => !IsTestProject(XDocument.Load(Path.Combine(SourceTree.RepoRoot, p))));
        Assert.Equal("src/Ecnanib/Ecnanib.csproj", application);
        var project = XDocument.Load(Path.Combine(SourceTree.RepoRoot, application));
        Assert.Equal("Microsoft.NET.Sdk.Web", project.Root!.Attribute("Sdk")!.Value);
        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Empty(project.Descendants("OutputType"));

        var tests = XDocument.Load(Path.Combine(SourceTree.RepoRoot, projects.Single(p => p != application)));
        Assert.Equal("../../src/Ecnanib/Ecnanib.csproj", Assert.Single(tests.Descendants("ProjectReference")).Attribute("Include")!.Value);

        Assert.Equal("Ecnanib", Application.GetName().Name);
        Assert.NotNull(Application.EntryPoint);
    }

    /// <summary>DB-01, NFR-DATA-01: one context over Npgsql, registered once; no other provider or store.</summary>
    [Fact]
    public void TheOnlyDataStoreIsOnePostgreSqlDatabaseReachedThroughTheOrm()
    {
        var contexts = ApplicationTypes().Where(t => typeof(DbContext).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.Equal([typeof(EcnanibDbContext)], contexts);

        using var provider = Register().BuildServiceProvider();
        using var db = provider.GetRequiredService<IDbContextFactory<EcnanibDbContext>>().CreateDbContext();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);

        var text = string.Concat(SourceTree.ApplicationSources(".cs").Select(File.ReadAllText));
        Assert.Single(Regex.Matches(text, @"\.UseNpgsql\("));
        Assert.Single(Regex.Matches(text, @"\.AddDbContext(Factory|Pool)?<"));
    }

    /// <summary>C-10: no message broker, queue, cache or separate service, and no state outside PostgreSQL and process memory.</summary>
    [Fact]
    public void TheSourceUsesNoBrokerQueueCacheSecondStoreOrFileState()
    {
        string[] forbidden =
        [
            // other stores, or PostgreSQL beside the ORM (DB-01)
            "UseSqlite(", "UseSqlServer(", "UseInMemoryDatabase(", "UseCosmos(", "UseMySql(", "MongoClient", "Dapper", "new NpgsqlConnection(", "NpgsqlCommand", "NpgsqlDataSource",
            // caches
            "AddMemoryCache(", "IMemoryCache", "IDistributedCache", "AddStackExchangeRedisCache", "AddOutputCache(", "AddResponseCaching(", "HybridCache", "Redis",
            // brokers, queues, job schedulers and separate services
            "Hangfire", "Quartz", "RabbitMQ", "Kafka", "MassTransit", "Azure.Messaging", "Amazon.SQS", "Grpc", "AddSignalR(", "MapHub<",
            // state on the file system
            "File.Write", "File.Create(", "File.Open(", "File.AppendAllText", "new FileStream(", "Directory.CreateDirectory(", "PersistKeysToFileSystem", "WriteTo.File(",
        ];

        foreach (var file in SourceTree.ApplicationSources(".cs", ".razor"))
        {
            var text = File.ReadAllText(file);
            foreach (var pattern in forbidden)
                Assert.False(text.Contains(pattern, StringComparison.Ordinal), $"{SourceTree.RelativeToApplication(file)} mentions '{pattern}'.");
        }
    }

    /// <summary>NFR-ARCH-01: scheduling, streaming and background jobs are hosted services of the one process, and constructing them connects to nothing.</summary>
    [Fact]
    public void EveryBackgroundJobIsAnInProcessHostedServiceOfAKnownModule()
    {
        var services = Register();
        Assert.Equal(HostedServiceOrder, HostedServices.OfApplication(services).Select(t => t.FullName!));

        var factory = new ThrowingDbContextFactory();
        services.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory);
        using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().Where(h => h.GetType().Assembly == Application).ToList();

        Assert.Equal(HostedServiceOrder, hosted.Select(h => h.GetType().FullName!));
        foreach (var service in hosted)
        {
            Assert.Contains(service.GetType().Namespace!.Split('.')[1], Modules);
            Assert.Same(service, provider.GetRequiredService(service.GetType()));
        }

        Assert.Equal(0, factory.Calls);
    }

    /// <summary>NFR-ARCH-02: the modules, each a folder with its <c>Add*</c> registration and its README; the SRS's eleven among them.</summary>
    [Fact]
    public void TheModulesAreTheSixteenFoldersEachWithARegistrationMethodAndAReadme()
    {
        var namespaces = ApplicationTypes()
            .Select(t => t.Namespace?.Split('.'))
            .Where(parts => parts is { Length: >= 2 } && parts[0] == "Ecnanib" && parts[1] != "Components")
            .Select(parts => parts![1])
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(Modules, namespaces);

        var folders = Directory.GetDirectories(SourceTree.ApplicationRoot).Select(Path.GetFileName).Except(NonModuleFolders).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(Modules, folders);

        foreach (var module in Modules)
        {
            var registration = Application.GetType($"Ecnanib.{module}.{module}ServiceCollectionExtensions");
            Assert.NotNull(registration);
            Assert.True(registration.IsPublic && registration.IsAbstract && registration.IsSealed, $"{registration.FullName} is not a public static class.");
            Assert.Contains(registration.GetMethods(BindingFlags.Public | BindingFlags.Static), m => m.Name.StartsWith("Add", StringComparison.Ordinal) && m.GetParameters().FirstOrDefault()?.ParameterType == typeof(IServiceCollection));
            Assert.True(File.Exists(Path.Combine(SourceTree.ApplicationRoot, module, "README.md")), $"{module}/README.md is missing.");
        }

        Assert.All(SrsModules, module => Assert.Contains(module, Modules));
    }

    /// <summary>
    /// NFR-ARCH-02: a module's contract is its root namespace (the registration), <c>Abstractions</c>, <c>Configuration</c> (its
    /// options) and, for the database module, <c>Entities</c>; everything else is internal to it. Pages are excluded (Razor
    /// components are public by construction), as are the generated EF migrations.
    /// </summary>
    [Fact]
    public void OnlyContractsAreVisibleOutsideAModule()
    {
        var offenders = ApplicationTypes()
            .Where(t => !t.IsNested && t.Namespace is { } ns && ns.StartsWith("Ecnanib.", StringComparison.Ordinal) && !ns.StartsWith("Ecnanib.Components", StringComparison.Ordinal))
            .Where(t => t.GetCustomAttribute<MigrationAttribute>() is null && t.GetCustomAttribute<DbContextAttribute>() is null && t.GetCustomAttribute<CompilerGeneratedAttribute>() is null)
            .Where(t => t.IsPublic && !IsContract(t.Namespace!))
            .Select(t => t.FullName!)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// NFR-ARCH-02: a module reaches another through that module's contracts only. A file belongs to the module of its
    /// folder; a page under <c>Components/&lt;Module&gt;</c> belongs to that module; the shared components (the layout,
    /// the live islands, the landing pages) and <c>Program.cs</c> belong to none, and <c>Program.cs</c> sees module roots only.
    /// </summary>
    [Fact]
    public void AModuleReachesAnotherOnlyThroughItsContracts()
    {
        var violations = new List<string>();
        foreach (var file in SourceTree.ApplicationSources(".cs", ".razor"))
        {
            var relative = SourceTree.RelativeToApplication(file);
            var own = ModuleOf(relative);
            var text = File.ReadAllText(file);
            foreach (Match match in QualifiedReference.Matches(text))
            {
                var module = match.Groups["module"].Value;
                var segment = match.Groups["segment"].Value;
                var rest = match.Groups["rest"].Value;
                if (relative == "Program.cs")
                {
                    violations.Add($"{relative}: {match.Value} (the composition root sees module roots only)");
                    continue;
                }

                if (module == own || module == "Components")
                    continue;
                if (segment is "Abstractions" or "Configuration")
                    continue;
                if (module == "Database" && segment == "Entities" && !rest.StartsWith(".Configurations", StringComparison.Ordinal))
                    continue;
                if (Application.GetType($"Ecnanib.{module}.{segment}") is { IsPublic: true })
                    continue;
                violations.Add($"{relative}: Ecnanib.{module}.{segment}{rest}");
            }
        }

        Assert.Empty(violations.Distinct());
    }

    /// <summary>
    /// The attribution above relies on folders: every <c>.cs</c> file declares the namespace of its folder (a sub-folder of
    /// <c>Abstractions</c> keeps the contract namespace), and no <c>.razor</c> file overrides the folder-derived one.
    /// </summary>
    [Fact]
    public void EveryNamespaceMatchesItsFolder()
    {
        var mismatches = new List<string>();
        foreach (var file in SourceTree.ApplicationSources(".cs"))
        {
            var relative = SourceTree.RelativeToApplication(file);
            if (relative == "Program.cs")
                continue;

            var slash = relative.LastIndexOf('/');
            var expected = slash < 0 ? "Ecnanib" : "Ecnanib." + relative[..slash].Replace('/', '.');
            var match = NamespaceDeclaration.Match(File.ReadAllText(file));
            var actual = match.Success ? match.Groups[1].Value : "(none)";
            var allowed = actual == expected || (expected.StartsWith(actual + ".", StringComparison.Ordinal) && actual.EndsWith(".Abstractions", StringComparison.Ordinal));
            if (!allowed)
                mismatches.Add($"{relative}: {actual}");
        }

        Assert.Empty(mismatches);

        foreach (var file in SourceTree.ApplicationSources(".razor"))
            Assert.DoesNotMatch(@"(?m)^@namespace\b", File.ReadAllText(file));
    }

    /// <summary>The root README is the architecture overview; it names every module and every background job, so it cannot drift from the code.</summary>
    [Fact]
    public void TheRootReadmeNamesEveryModuleAndEveryBackgroundJob()
    {
        var readme = File.ReadAllText(Path.Combine(SourceTree.RepoRoot, "README.md"));

        foreach (var module in Modules)
            Assert.Contains($"src/Ecnanib/{module}/README.md", readme);
        foreach (var job in HostedServiceOrder)
            Assert.Contains($"`{job.Split('.')[^1]}`", readme);
    }

    private static bool IsTestProject(XDocument project) =>
        project.Descendants("IsTestProject").Any(e => string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));

    private static bool IsContract(string ns)
    {
        var parts = ns.Split('.');
        return parts.Length == 2 || parts[2] is "Abstractions" or "Configuration" || ns == "Ecnanib.Database.Entities";
    }

    /// <summary>The module a source file belongs to by its folder, or null for the composition root and the shared components.</summary>
    private static string? ModuleOf(string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length < 2)
            return null;
        if (parts[0] != "Components")
            return parts[0];
        return parts.Length > 2 && ComponentOwners.TryGetValue(parts[1], out var owner) ? owner : null;
    }

    private static IEnumerable<Type> ApplicationTypes()
    {
        try
        {
            return Application.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    /// <summary>Both log sinks and every module over the reference file, as <c>Program.cs</c> registers them (see <see cref="ReferenceFileTests"/>).</summary>
    private static ServiceCollection Register()
    {
        var configuration = ReferenceConfiguration.Load();
        configuration.AddInMemoryCollection(DatabaseTestHost.OfflineConfiguration());
        var services = new ServiceCollection();
        services.AddLogSinks(configuration);
        services.AddEcnanibModules(configuration);
        return services;
    }
}
