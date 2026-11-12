using Ecnanib.Accounts;
using Microsoft.Extensions.DependencyModel;

namespace Ecnanib.Tests.Architecture;

/// <summary>
/// Guards the deployment shape as packages are added: no message broker, separate cache server,
/// or search server (NFR-ARC-2), no proprietary cloud service (NFR-ARC-5), no database other than
/// PostgreSQL (NFR-DAT-1), no log output into a database (NFR-DAT-7), and no payment service,
/// because the service is free (FR-ACC-6).
/// </summary>
public class DeploymentRuleTests
{
    [Fact]
    public void The_application_ships_no_broker_cache_server_search_server_cloud_sdk_other_database_or_payment_service()
    {
        var forbidden = ForbiddenInfrastructure.FindIn(Packages());

        Assert.True(
            forbidden.Count == 0,
            "The application must stay one process plus PostgreSQL, with no proprietary cloud service, " +
            $"and it takes no payment. Remove: {string.Join(", ", forbidden)}");
    }

    [Fact]
    public void The_application_ships_no_log_output_but_the_console_and_the_file()
    {
        // The two outputs the configuration file can turn on. A package that writes log events
        // anywhere else, a database included, is a third.
        var outputs = Packages().Where(package => package.StartsWith("Serilog.Sinks.", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(["Serilog.Sinks.Console", "Serilog.Sinks.File"], outputs.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("StackExchange.Redis")]
    [InlineData("RabbitMQ.Client")]
    [InlineData("MassTransit.RabbitMQ")]
    [InlineData("Elastic.Clients.Elasticsearch")]
    [InlineData("Azure.Storage.Blobs")]
    [InlineData("AWSSDK.S3")]
    [InlineData("Google.Cloud.Storage.V1")]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite")]
    [InlineData("Microsoft.EntityFrameworkCore.InMemory")]
    [InlineData("Microsoft.Data.SqlClient")]
    [InlineData("MySql.Data")]
    [InlineData("MongoDB.Driver")]
    [InlineData("Stripe.net")]
    [InlineData("PayPalCheckoutSdk")]
    [InlineData("Mollie.Api")]
    public void Forbidden_packages_are_recognised(string package)
    {
        Assert.NotEmpty(ForbiddenInfrastructure.FindIn([package]));
    }

    [Theory]
    [InlineData("Npgsql")]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.EntityFrameworkCore.Relational")]
    [InlineData("Microsoft.EntityFrameworkCore.Design")]
    [InlineData("EFCore.NamingConventions")]
    [InlineData("Microsoft.Extensions.Caching.Memory")]
    [InlineData("Serilog.AspNetCore")]
    [InlineData("Elasticity")]
    public void Permitted_packages_are_not_flagged(string package)
    {
        Assert.Empty(ForbiddenInfrastructure.FindIn([package]));
    }

    /// <summary>The names of the packages in the application's package graph.</summary>
    private static List<string> Packages()
    {
        var dependencies = DependencyContext.Load(typeof(AccountsModule).Assembly);
        Assert.True(dependencies is not null, "The application's .deps.json was not found next to its assembly.");

        var packages = dependencies.RuntimeLibraries.Select(library => library.Name)
            .Concat(dependencies.CompileLibraries.Select(library => library.Name))
            .Distinct()
            .ToList();

        // The application itself must be in the list, or the check is looking at the wrong file.
        Assert.Contains("Ecnanib", packages);

        return packages;
    }
}
