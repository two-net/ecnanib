using Ecnanib.Tests.Database;
using System.Text.Json;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Logging;
using Ecnanib.Logging.Abstractions;
using Ecnanib.Logging.Services;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging.Live;

/// <summary>FR-LOG-01, FR-LOG-02 and FR-LOG-08 against PostgreSQL: events logged through ILogger land as rows with their ids, and the sink never writes the logs of its own inserts.</summary>
[Trait("Category", "Live")]
public class DatabaseLogSinkLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public DatabaseLogSinkLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RowsLandWithIdsAndContextAndTheSinkNeverLogsItsOwnInserts()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "sink@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = _fixture.ConnectionString,
 ["Database:EncryptionKey"] = DatabaseTestHost.EncryptionKey,
            ["Logging:Console:MinimumLevel"] = "Warning",
            ["Logging:Database:MinimumLevel"] = "Debug",
            // EF at Debug: the sink's own inserts would feed the sink forever if they were not suppressed.
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Debug",
            ["Logging:LogLevel:Binance"] = "Warning",
            ["Logging:LogLevel:CryptoExchange"] = "Warning",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogSinks(configuration);
        services.AddDatabase(configuration);
        services.AddLogReader();
        await using var provider = services.BuildServiceProvider();
        var writer = Assert.Single(provider.GetServices<IHostedService>().OfType<DatabaseLogWriter>());
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Ecnanib.Tests.Logging.Sink");
        var evaluation = Guid.NewGuid();
        var order = Guid.NewGuid();

        await writer.StartAsync(Ct);
        using (logger.BeginScope(agentId: agent.Id, userId: user.Id))
        using (logger.BeginScope(evaluationId: evaluation))
        {
            logger.LogInformation("Agent evaluated: {Outcome} after {Count} candles", "NoAction", 3);
            logger.LogWarning(new InvalidOperationException("boom"), "Order {OrderId} was rejected", order);
            logger.LogDebug("A detail");
        }

        logger.LogInformation("Outside any scope");
        await writer.StopAsync(Ct);

        var page = await provider.GetRequiredService<ILogReader>().QueryAsync(user.Id, new LogQuery(agent.Id), Ct);
        Assert.NotNull(page);
        Assert.Equal(["A detail", $"Order {order} was rejected", "Agent evaluated: NoAction after 3 candles"], page.Entries.Select(e => e.Message));
        Assert.All(page.Entries, e => Assert.Equal(agent.Id, e.AgentId));
        // jsonb comes back re-serialized, so the context is compared as JSON, not as text.
        Assert.All(page.Entries, e => Assert.Equal(evaluation.ToString(), Context(e).GetProperty("EvaluationId").GetString()));
        Assert.All(page.Entries, e => Assert.Equal("Ecnanib.Tests.Logging.Sink", e.Component));
        var rejected = page.Entries[1];
        Assert.Equal(order, rejected.OrderId);
        Assert.Null(rejected.DecisionId);
        Assert.Contains("boom", rejected.Exception);
        Assert.Equal("NoAction", Context(page.Entries[2]).GetProperty("Outcome").GetString());
        Assert.Equal(3, Context(page.Entries[2]).GetProperty("Count").GetInt32());
        Assert.Equal([LogLevel.Debug, LogLevel.Warning, LogLevel.Information], page.Entries.Select(e => e.Level));

        await using (var db = _fixture.CreateContext())
        {
            var rows = await db.LogEntries.AsNoTracking().OrderBy(l => l.Id).ToListAsync(Ct);
            var outside = Assert.Single(rows, r => r.Message == "Outside any scope");
            Assert.Null(outside.AgentId);
            Assert.Null(outside.UserId);
            Assert.Null(outside.Context);
            Assert.Equal(user.Id, rows.Single(r => r.Message == "A detail").UserId);
            Assert.DoesNotContain(rows, r => r.Component.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
            Assert.DoesNotContain(rows, r => r.Message.Contains("log_entries", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(4, rows.Count);
            await db.LogEntries.ExecuteDeleteAsync(Ct);
        }

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    private static JsonElement Context(LogEntryView entry) => JsonDocument.Parse(entry.ContextJson!).RootElement;
}
