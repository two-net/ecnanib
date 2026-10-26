using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ecnanib.Ai.Configuration;
using Ecnanib.Ai.Providers;
using Ecnanib.Configuration;
using Ecnanib.Exchange.Binance;
using Ecnanib.Exchange.Configuration;
using Ecnanib.Tests.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Configuration;

/// <summary>
/// NFR-SEC-04: outbound traffic goes to Binance and the four providers and nowhere else, and the system sends no
/// telemetry. The defaults and the reference file name only allowed hosts, the application references no package that
/// could phone home, and the source opens no network client beside the two named HttpClients and Binance.Net's sockets.
/// </summary>
public class OutboundTrafficTests
{
    /// <summary>Every package the application may reference; a telemetry exporter, crash reporter or network sink is a conscious change here.</summary>
    private static readonly string[] AllowedPackages =
    [
        "Binance.Net",
        "EFCore.NamingConventions",
        "Konscious.Security.Cryptography.Argon2",
        "Microsoft.AspNetCore.DataProtection.EntityFrameworkCore",
        "Microsoft.EntityFrameworkCore.Design",
        "Microsoft.EntityFrameworkCore.Relational",
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        "Serilog.Extensions.Hosting",
        "Serilog.Sinks.Console",
    ];

    private static readonly string RepoRoot = FindRepoRoot();

    private static readonly Regex Url = new("^(https?|wss?)://", RegexOptions.IgnoreCase);

    [Fact]
    public void TheDefaultsNameOnlyBinanceAndTheFourProviders()
    {
        var ai = new AiOptions();
        foreach (var (provider, endpoint) in new[] { (Ecnanib.Ai.Abstractions.AiProvider.Claude, ai.Claude), (Ecnanib.Ai.Abstractions.AiProvider.Gemini, ai.Gemini), (Ecnanib.Ai.Abstractions.AiProvider.Gpt, ai.Gpt), (Ecnanib.Ai.Abstractions.AiProvider.Grok, ai.Grok) })
            Assert.True(HostNames.IsUnder(new Uri(endpoint.BaseUrl).Host, AiOptionsValidator.AllowedDomains[provider]), endpoint.BaseUrl);

        foreach (var endpoints in new[] { ExchangeEndpointOptions.ProductionDefaults(), ExchangeEndpointOptions.TestnetDefaults() })
        {
            foreach (var url in new[] { endpoints.SpotRest, endpoints.SpotStreams, endpoints.SpotWsApi, endpoints.FuturesRest, endpoints.FuturesStreams, endpoints.FuturesWsApi }.Where(u => !string.IsNullOrEmpty(u)))
                Assert.True(ExchangeOptionsValidator.IsAllowedHost(new Uri(url!).Host), url);
        }
    }

    [Fact]
    public void TheReferenceFileNamesOnlyAllowedHosts()
    {
        var configuration = new ConfigurationManager();
        ConfigurationFile.Load(configuration, Path.Combine(AppContext.BaseDirectory, ConfigurationFile.DefaultFileName));
        var urls = configuration.AsEnumerable().Where(pair => pair.Value is not null && Url.IsMatch(pair.Value)).ToList();

        Assert.NotEmpty(urls);
        foreach (var (key, value) in urls)
        {
            var host = new Uri(value!).Host;
            if (key.StartsWith("Server:", StringComparison.Ordinal))
            {
                Assert.True(new Uri(value!).IsLoopback, $"{key} is the public origin and must stay local in the reference file.");
                continue;
            }

            var allowed = ExchangeOptionsValidator.IsAllowedHost(host) || AiOptionsValidator.AllowedDomains.Values.Any(domain => HostNames.IsUnder(host, domain));
            Assert.True(allowed, $"{key} points at {host}, which is neither Binance nor a provider.");
        }
    }

    [Fact]
    public void TheApplicationReferencesOnlyTheAllowedPackages()
    {
        var project = XDocument.Load(Path.Combine(RepoRoot, "src", "Ecnanib", "Ecnanib.csproj"));
        var packages = project.Descendants("PackageReference").Select(p => p.Attribute("Include")!.Value).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(AllowedPackages, packages);
    }

    /// <summary>The two named HttpClients and Binance.Net's own sockets are the only ways out; nothing else opens a connection, measures or traces.</summary>
    [Fact]
    public void TheSourceOpensNoOtherNetworkClientAndNoTelemetry()
    {
        var forbidden = new[] { "new HttpClient(", "ClientWebSocket", "TcpClient", "UdpClient", "SmtpClient", "WebRequest.", "Dns.Get", "Process.Start(", "ActivitySource", "new Meter(", "HttpClient.DefaultProxy", "OpenTelemetry", "ApplicationInsights", "Sentry" };
        var sources = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "Ecnanib"), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".js", StringComparison.Ordinal))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}wwwroot{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(sources);

        var httpClients = 0;
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            foreach (var pattern in forbidden)
                Assert.DoesNotContain(pattern, text, StringComparison.Ordinal);
            httpClients += Regex.Matches(text, @"\.AddHttpClient\(").Count;
        }

        Assert.Equal(2, httpClients);
    }

    /// <summary>A 3xx from a provider or the exchange must not carry the user's key anywhere else: neither client follows a redirect (FR-KEY-05).</summary>
    [Fact]
    public void TheNamedClientsFollowNoRedirect()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = DatabaseTestHost.OfflineConnectionString }).Build();
        using var provider = new ServiceCollection().AddEcnanibModules(configuration).BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        foreach (var name in new[] { AiProviderClientBase.HttpClientName, BinanceClientRegistry.HttpClientName })
        {
            var handler = factory.CreateHandler(name);
            while (handler is DelegatingHandler { InnerHandler: { } inner })
                handler = inner;
            var primary = Assert.IsType<SocketsHttpHandler>(handler);
            Assert.False(primary.AllowAutoRedirect, name);
        }
    }

    /// <summary>Browsers load nothing from another origin: no CDN, no font service, no beacon (NFR-SEC-04).</summary>
    [Fact]
    public void TheFrontEndReferencesNoOtherOrigin()
    {
        var files = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "Ecnanib", "Components"), "*.*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "Ecnanib", "wwwroot", "css"), "*.css", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".js", StringComparison.Ordinal) || f.EndsWith(".css", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotMatch(@"(?i)(src|href|url\(|@import|fetch\(|sendBeacon|new WebSocket|XMLHttpRequest)[^\n]*https?://", text);
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ecnanib.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Ecnanib.slnx not found above the test directory.");
    }
}
