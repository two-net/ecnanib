using System.Text.Json.Nodes;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters;
using Ecnanib.Modules.AiAdapters.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace Ecnanib.Tests.Ai;

/// <summary>
/// The application's own provider adapters, from the registration the application uses, without starting
/// it. They're internal, so tests reach them through the public interface, as the pipeline does.
/// </summary>
internal static class Adapters
{
    public const string ApiKey = "Zkey7c1d-adapter";

    private static readonly IAiProviderAdapter[] Registered =
        [.. new ServiceCollection().AddAiAdapters().BuildServiceProvider().GetServices<IAiProviderAdapter>()];

    public static IReadOnlyList<IAiProviderAdapter> All => Registered;

    public static IAiProviderAdapter Get(string name) => Registered.Single(adapter => adapter.Name == name);

    public static SecretValue Key() => SecretValue.FromString(ApiKey);

    /// <summary>The AI adapters alone, with the AiProviders section appsettings.json ships, as Production.</summary>
    public static ServiceProvider Shipped()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<IHostEnvironment>(new HostingEnvironment
            {
                EnvironmentName = Environments.Production,
                ApplicationName = "Ecnanib",
                ContentRootPath = AppContext.BaseDirectory,
            });

        return services.AddAiAdapters().BuildServiceProvider();
    }

    public static string Header(HttpRequestMessage request, string name) => string.Join(", ", request.Headers.GetValues(name));

    public static JsonNode Body(HttpRequestMessage request) => JsonNode.Parse(request.Content!.ReadAsStream())!;

    /// <summary>The request's body is exactly <paramref name="expected"/>, whatever the order of its properties.</summary>
    public static void AssertBody(string expected, HttpRequestMessage request)
    {
        var actual = Body(request);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), $"The body was {actual.ToJsonString()}");
    }

    /// <summary>A key travels only in a header: a URI can reach an exception.</summary>
    public static void AssertKeyOnlyInHeader(HttpRequestMessage request)
    {
        Assert.False(request.RequestUri!.IsAbsoluteUri);
        Assert.DoesNotContain(ApiKey, request.RequestUri.OriginalString, StringComparison.Ordinal);
        Assert.DoesNotContain("key=", request.RequestUri.OriginalString, StringComparison.OrdinalIgnoreCase);
    }
}
