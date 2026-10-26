using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Ecnanib.Configuration;
using Ecnanib.Tests.Database;
using Ecnanib.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Configuration;

/// <summary>
/// The application's request pipeline (<see cref="EcnanibPipeline"/>) on an in-memory server, with every module
/// registered through <see cref="EcnanibModules.AddEcnanibModules"/> as <c>Program.cs</c> does, over the reference
/// configuration file plus overrides. The Data
/// Protection key ring lives in memory, no module's hosted service runs (nothing may connect on start), and the
/// connection's remote address, which TestServer leaves null, is set per request from <see cref="RemoteAddressHeader"/>.
/// The client's host is <c>ecnanib.test</c>, not loopback, so HSTS is not excluded by its default host list.
/// </summary>
internal sealed class PipelineTestHost : IAsyncDisposable
{
    public const string RemoteAddressHeader = "X-Test-Remote-Address";

    public const string Host = "ecnanib.test";

    private readonly WebApplication _app;

    private PipelineTestHost(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<PipelineTestHost> StartAsync(Dictionary<string, string?>? config = null, string? connectionString = DatabaseTestHost.OfflineConnectionString, TestLoggerProvider? logs = null, Action<WebApplication>? map = null)
    {
        var values = new Dictionary<string, string?>();
        if (connectionString is not null)
            values["Database:ConnectionString"] = connectionString;
        foreach (var (key, value) in config ?? [])
            values[key] = value;

        // Under the application's own name, MapStaticAssets and UseStaticWebAssets find the Ecnanib manifests copied beside the test assembly.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ApplicationName = "Ecnanib" });
        builder.Configuration.AddJsonFile(ReferenceConfiguration.Path, optional: false, reloadOnChange: false);
        builder.Configuration.AddInMemoryCollection(values);
        builder.WebHost.UseTestServer();
        builder.WebHost.UseStaticWebAssets();
        builder.Services.AddRouting();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddLogging(b =>
        {
            if (logs is not null)
                b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace);
        });

        var services = builder.Services;
        services.AddEcnanibModules(builder.Configuration);

        services.PostConfigure<KeyManagementOptions>(o => o.XmlRepository = new InMemoryXmlRepository());
        RemoveModuleHostedServices(services);

        var app = builder.Build();
        var remaining = app.Services.GetServices<IHostedService>().Where(h => h.GetType().Assembly == typeof(EcnanibHost).Assembly).ToList();
        if (remaining.Count > 0)
            throw new InvalidOperationException("A module hosted service would run: " + string.Join(", ", remaining.Select(h => h.GetType().Name)));

        app.Use((context, next) =>
        {
            if (context.Request.Headers.TryGetValue(RemoteAddressHeader, out var value) && IPAddress.TryParse(value, out var address))
                context.Connection.RemoteIpAddress = address;
            context.Request.Headers.Remove(RemoteAddressHeader);
            return next(context);
        });
        app.UseEcnanibPipeline();
        map?.Invoke(app);
        await app.StartAsync(Ct);

        var client = app.GetTestClient();
        client.BaseAddress = new Uri($"http://{Host}/");
        return new PipelineTestHost(app, client);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
    }

    /// <summary>A request with the connection's remote address and, optionally, the proxy's forwarded headers and cookies.</summary>
    public static HttpRequestMessage Request(HttpMethod method, string path, string remoteAddress = "127.0.0.1", string? forwardedProto = null, string? forwardedFor = null, IEnumerable<string>? cookies = null, HttpContent? content = null, string? hostHeader = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add(RemoteAddressHeader, remoteAddress);
        if (hostHeader is not null)
            request.Headers.Host = hostHeader;
        if (forwardedProto is not null)
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        var jar = (cookies ?? []).Where(c => c.Length > 0).ToList();
        if (jar.Count > 0)
            request.Headers.Add("Cookie", string.Join("; ", jar));
        return request;
    }

    public static FormUrlEncodedContent Form(params (string Name, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));

    /// <summary>The <c>Set-Cookie</c> header for the named cookie, or null when the response sets none.</summary>
    public static string? SetCookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault(v => v.StartsWith(name + "=", StringComparison.Ordinal)) : null;

    /// <summary>The <c>name=value</c> pair of a set cookie, fit for a Cookie request header.</summary>
    public static string? CookiePair(HttpResponseMessage response, string name) =>
        SetCookie(response, name) is { } header ? header.Split(';')[0] : null;

    /// <summary>Every <c>&lt;input&gt;</c> in the page by name, with its attributes.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Inputs(string html)
    {
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (Match input in Regex.Matches(html, "<input\\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var attributes = Regex.Matches(input.Value, "([a-zA-Z_:-]+)=\"([^\"]*)\"")
                .ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value), StringComparer.OrdinalIgnoreCase);
            if (attributes.TryGetValue("name", out var name))
                inputs[name] = attributes;
        }

        return inputs;
    }

    /// <summary>The antiforgery token a page rendered for its forms (one per page; every form shares it).</summary>
    public static string AntiforgeryToken(string html) =>
        Inputs(html).TryGetValue("__RequestVerificationToken", out var field) ? field["value"] : throw new InvalidOperationException("The page rendered no antiforgery token.");

    public static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

    private static void RemoveModuleHostedServices(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && !d.IsKeyedService && HostedServices.ImplementationType(d)?.Assembly == typeof(EcnanibHost).Assembly).ToList())
            services.Remove(descriptor);
    }
}
