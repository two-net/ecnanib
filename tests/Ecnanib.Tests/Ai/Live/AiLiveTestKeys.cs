using System.Text.Json;
using Ecnanib.Ai;
using Ecnanib.Ai.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Ai.Live;

/// <summary>
/// Opt-in live tests read <c>tests/.ai-keys.json</c> (git-ignored), one section per provider:
/// <code>{ "claude": { "apiKey": "...", "model": "..." }, "gemini": {...}, "gpt": {...}, "grok": {...} }</code>
/// The model is taken from the file so no identifier is hard-coded (FR-SYNC-05). Every test skips when its section
/// is absent, so the default <c>dotnet test</c> never touches the network.
/// </summary>
public sealed class AiLiveTestKeys
{
    public const string FileName = ".ai-keys.json";

    public LiveProviderKey? Claude { get; init; }

    public LiveProviderKey? Gemini { get; init; }

    public LiveProviderKey? Gpt { get; init; }

    public LiveProviderKey? Grok { get; init; }

    public sealed record LiveProviderKey(string? ApiKey, string? Model);

    public static AiLiveTestKeys? Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", FileName);
            if (File.Exists(candidate))
                return JsonSerializer.Deserialize<AiLiveTestKeys>(File.ReadAllText(candidate), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            directory = directory.Parent;
        }

        return null;
    }

    public static (AiKeyHandle Handle, string Model) Require(AiProvider provider)
    {
        var keys = Load();
        var entry = keys is null ? null : provider switch
        {
            AiProvider.Claude => keys.Claude,
            AiProvider.Gemini => keys.Gemini,
            AiProvider.Gpt => keys.Gpt,
            AiProvider.Grok => keys.Grok,
            _ => null,
        };
        var section = provider.ToString().ToLowerInvariant();
        if (entry is null || string.IsNullOrWhiteSpace(entry.ApiKey) || string.IsNullOrWhiteSpace(entry.Model))
            Assert.Skip($"Live test: add \"{section}\" with \"apiKey\" and \"model\" to tests/{FileName} to run.");
        return (new AiKeyHandle($"{section}-live", provider, new AiCredentials(entry!.ApiKey!)), entry.Model!);
    }

    /// <summary>The module as the application registers it, with default configuration and console logging.</summary>
    public static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddAiProviders(new ConfigurationBuilder().Build());
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        return services.BuildServiceProvider();
    }
}
