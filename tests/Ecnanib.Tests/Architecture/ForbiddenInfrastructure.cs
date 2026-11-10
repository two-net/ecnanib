namespace Ecnanib.Tests.Architecture;

/// <summary>
/// Packages the application must never ship, because each one means a second thing to deploy or
/// a proprietary cloud service: the application is one process plus PostgreSQL and runs anywhere
/// those two run (NFR-ARC-1, NFR-ARC-2, NFR-ARC-5).
/// </summary>
internal static class ForbiddenInfrastructure
{
    /// <summary>
    /// Package names, each with the reason it is forbidden. A name also covers every package
    /// below it, so <c>Azure</c> covers <c>Azure.Storage.Blobs</c>.
    /// </summary>
    private static readonly (string Package, string Kind)[] Forbidden =
    [
        ("RabbitMQ", "message broker"),
        ("EasyNetQ", "message broker"),
        ("Confluent.Kafka", "message broker"),
        ("MassTransit", "message broker"),
        ("NServiceBus", "message broker"),
        ("Rebus", "message broker"),
        ("NATS", "message broker"),
        ("Apache.NMS", "message broker"),

        ("StackExchange.Redis", "cache server"),
        ("Microsoft.Extensions.Caching.StackExchangeRedis", "cache server"),
        ("ServiceStack.Redis", "cache server"),
        ("EnyimMemcachedCore", "cache server"),

        ("Elastic", "search server"),
        ("Elasticsearch.Net", "search server"),
        ("NEST", "search server"),
        ("OpenSearch", "search server"),
        ("Meilisearch", "search server"),
        ("SolrNet", "search server"),
        ("Typesense", "search server"),
        ("Algolia", "search server"),

        ("Azure", "proprietary cloud service"),
        ("Microsoft.Azure", "proprietary cloud service"),
        ("AWSSDK", "proprietary cloud service"),
        ("Amazon", "proprietary cloud service"),
        ("Google.Cloud", "proprietary cloud service"),
    ];

    /// <summary>Returns the forbidden packages among <paramref name="packages"/>, with the reason.</summary>
    public static IReadOnlyList<string> FindIn(IEnumerable<string> packages) =>
    [
        .. from package in packages
           from forbidden in Forbidden
           where Covers(forbidden.Package, package)
           orderby package
           select $"{package} ({forbidden.Kind})",
    ];

    private static bool Covers(string forbidden, string package) =>
        package.Equals(forbidden, StringComparison.OrdinalIgnoreCase)
        || package.StartsWith(forbidden + ".", StringComparison.OrdinalIgnoreCase);
}
