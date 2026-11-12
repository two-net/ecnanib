namespace Ecnanib.Tests.Architecture;

/// <summary>
/// Packages the application must never ship, because each one means a second thing to deploy or
/// a proprietary cloud service: the application is one process plus PostgreSQL and runs anywhere
/// those two run (NFR-ARC-1, NFR-ARC-2, NFR-ARC-5). PostgreSQL is its only data store (NFR-DAT-1).
/// And the service is free, so it takes no payment and needs nothing that takes one (FR-ACC-6).
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

        ("Microsoft.EntityFrameworkCore.SqlServer", "another database"),
        ("Microsoft.EntityFrameworkCore.Sqlite", "another database"),
        ("Microsoft.EntityFrameworkCore.InMemory", "another database"),
        ("Microsoft.EntityFrameworkCore.Cosmos", "another database"),
        ("Microsoft.Data.SqlClient", "another database"),
        ("System.Data.SqlClient", "another database"),
        ("Microsoft.Data.Sqlite", "another database"),
        ("System.Data.SQLite", "another database"),
        ("SQLitePCLRaw", "another database"),
        ("MySql", "another database"),
        ("MySqlConnector", "another database"),
        ("Pomelo.EntityFrameworkCore.MySql", "another database"),
        ("Oracle", "another database"),
        ("MongoDB", "another database"),
        ("LiteDB", "another database"),
        ("RavenDB", "another database"),
        ("CassandraCSharpDriver", "another database"),
        ("CouchbaseNetClient", "another database"),
        ("InfluxDB", "another database"),
        ("ClickHouse", "another database"),
        ("Neo4j", "another database"),

        ("Azure", "proprietary cloud service"),
        ("Microsoft.Azure", "proprietary cloud service"),
        ("AWSSDK", "proprietary cloud service"),
        ("Amazon", "proprietary cloud service"),
        ("Google.Cloud", "proprietary cloud service"),

        ("Stripe", "payment service"),
        ("PayPal", "payment service"),
        ("PayPalCheckoutSdk", "payment service"),
        ("PayPalServerSDK", "payment service"),
        ("Braintree", "payment service"),
        ("Paddle", "payment service"),
        ("Adyen", "payment service"),
        ("Mollie", "payment service"),
        ("ChargeBee", "payment service"),
        ("Recurly", "payment service"),
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
