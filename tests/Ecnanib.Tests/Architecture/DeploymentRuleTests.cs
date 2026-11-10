using Ecnanib.Accounts;
using Microsoft.Extensions.DependencyModel;

namespace Ecnanib.Tests.Architecture;

/// <summary>
/// Guards the deployment shape as packages are added: no message broker, separate cache server,
/// or search server (NFR-ARC-2), and no proprietary cloud service (NFR-ARC-5).
/// </summary>
public class DeploymentRuleTests
{
    [Fact]
    public void The_application_ships_no_broker_cache_server_search_server_or_cloud_sdk()
    {
        var dependencies = DependencyContext.Load(typeof(AccountsModule).Assembly);
        Assert.True(dependencies is not null, "The application's .deps.json was not found next to its assembly.");

        var packages = dependencies.RuntimeLibraries.Select(library => library.Name)
            .Concat(dependencies.CompileLibraries.Select(library => library.Name))
            .Distinct();

        // The application itself must be in the list, or the check is looking at the wrong file.
        Assert.Contains("Ecnanib", packages);

        var forbidden = ForbiddenInfrastructure.FindIn(packages);

        Assert.True(
            forbidden.Count == 0,
            "The application must stay one process plus PostgreSQL, with no proprietary cloud service. " +
            $"Remove: {string.Join(", ", forbidden)}");
    }

    [Theory]
    [InlineData("StackExchange.Redis")]
    [InlineData("RabbitMQ.Client")]
    [InlineData("MassTransit.RabbitMQ")]
    [InlineData("Elastic.Clients.Elasticsearch")]
    [InlineData("Azure.Storage.Blobs")]
    [InlineData("AWSSDK.S3")]
    [InlineData("Google.Cloud.Storage.V1")]
    public void Forbidden_packages_are_recognised(string package)
    {
        Assert.NotEmpty(ForbiddenInfrastructure.FindIn([package]));
    }

    [Theory]
    [InlineData("Npgsql")]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.Extensions.Caching.Memory")]
    [InlineData("Serilog.AspNetCore")]
    [InlineData("Elasticity")]
    public void Permitted_packages_are_not_flagged(string package)
    {
        Assert.Empty(ForbiddenInfrastructure.FindIn([package]));
    }
}
