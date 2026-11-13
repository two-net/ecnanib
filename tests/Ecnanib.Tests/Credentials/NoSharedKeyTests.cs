using System.Reflection;
using Ecnanib.Platform.Settings;
using Ecnanib.Tests.Architecture;

namespace Ecnanib.Tests.Credentials;

/// <summary>
/// Users supply their own keys, and Ecnanib provides no shared key for trading decisions or
/// orders (FR-KEY-1). The service has nowhere to keep such a key: the configuration file holds
/// none for Binance, and the one it may hold for an AI provider is for syncing the model catalog
/// (FR-CAT-3), which nothing but the catalog reads.
/// </summary>
public class NoSharedKeyTests
{
    [Fact]
    public void The_configuration_file_has_no_place_for_a_key_that_a_decision_or_an_order_could_use()
    {
        // Pinned, so that a secret which is added to the file shows in a review as what it is.
        // None is under Binance or Trading, and the one of an AI provider is its catalog key.
        Assert.Equal(
            [
                "AiProviders.ByCode[].CatalogKey",
                "ConnectionStrings.EcnanibDbContext",
                "Email.Password",
                "Security.MasterKeys[]",
            ],
            SecretsOf(typeof(EcnanibSettings)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Nothing_but_the_catalog_reads_the_key_that_the_service_holds_for_an_AI_provider()
    {
        // The rule that finds a call no code may make, asked for the one property. Platform
        // reads it too: to read the file, and to keep the key out of the log.
        var checker = new DependencyChecker(
            ComponentRules.Rules with { ForbiddenCalls = [$"{typeof(AiProviderSettings).FullName}::get_{nameof(AiProviderSettings.CatalogKey)}"] });

        var readers = checker.FindForbiddenCalls(typeof(Program).Assembly).Select(call => call.Origin).ToList();
        var others = readers
            .Where(reader => checker.AreaOf(NamespaceOf(reader)) is not (ComponentRules.Platform or ComponentRules.Catalog))
            .ToList();

        Assert.True(
            others.Count == 0,
            "The key that the configuration file may hold for an AI provider is for syncing the model catalog (FR-CAT-3). A " +
            "decision is made with the key of the user whose agent makes it (FR-KEY-1), so nothing but the catalog reads the " +
            $"service's own: {string.Join(", ", others)}");

        // The rule sees a read where there is one: the log's mask makes it.
        Assert.Contains(readers, reader => reader.StartsWith("Ecnanib.Platform.Logging.SecretMask", StringComparison.Ordinal));
    }

    /// <summary>The namespace of a type, by its full name. A type that is nested in another is named after it.</summary>
    private static string NamespaceOf(string type)
    {
        var outer = type.Split('+')[0];

        return outer[..outer.LastIndexOf('.')];
    }

    /// <summary>Where the settings hold a secret, each as the path of its property.</summary>
    private static IEnumerable<string> SecretsOf(Type settings, string path = "") =>
        settings.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(property => SecretsIn(property.PropertyType, path.Length == 0 ? property.Name : $"{path}.{property.Name}"));

    private static IEnumerable<string> SecretsIn(Type type, string path)
    {
        if (type == typeof(Secret))
        {
            return [path];
        }

        // A list or a map: what it holds is what counts.
        if (type.IsGenericType)
        {
            return SecretsIn(type.GetGenericArguments()[^1], $"{path}[]");
        }

        return type.Namespace == typeof(EcnanibSettings).Namespace && type.IsClass ? SecretsOf(type, path) : [];
    }
}
