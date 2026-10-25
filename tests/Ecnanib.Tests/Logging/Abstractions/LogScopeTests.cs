using Ecnanib.Logging.Abstractions;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging.Abstractions;

public class LogScopeTests
{
    [Fact]
    public void HoldsOnlyTheIdsThatWereSetAndReadsAsPairs()
    {
        var agent = Guid.NewGuid();
        var order = Guid.NewGuid();

        var scope = new LogScope(agentId: agent, orderId: order);

        Assert.Equal([new KeyValuePair<string, object?>("AgentId", agent), new KeyValuePair<string, object?>("OrderId", order)], scope);
        Assert.Equal(2, scope.Count);
        Assert.Equal(order, scope[1].Value);
        Assert.Equal($"AgentId={agent}, OrderId={order}", scope.ToString());
        Assert.Empty(new LogScope());
    }

    [Fact]
    public void BeginScopeAttachesTheIdsToEveryEntryInsideAndTheInnerScopeWins()
    {
        var logs = new TestLoggerProvider();
        using var factory = TestLogging.CreateFactory(logs);
        var logger = factory.CreateLogger("Ecnanib.Tests");
        var agent = Guid.NewGuid();
        var user = Guid.NewGuid();
        var evaluation = Guid.NewGuid();
        var inner = Guid.NewGuid();

        using (logger.BeginScope(agentId: agent, userId: user))
        {
            logger.LogInformation("outer");
            using (logger.BeginScope(agentId: inner, evaluationId: evaluation))
                logger.LogInformation("inner");
        }

        logger.LogInformation("outside");

        var outer = Assert.Single(logs.Entries, e => e.Message == "outer");
        Assert.Equal(new Dictionary<string, object?> { ["AgentId"] = agent, ["UserId"] = user }, outer.Scope);
        var nested = Assert.Single(logs.Entries, e => e.Message == "inner");
        Assert.Equal(new Dictionary<string, object?> { ["AgentId"] = inner, ["UserId"] = user, ["EvaluationId"] = evaluation }, nested.Scope);
        Assert.Empty(Assert.Single(logs.Entries, e => e.Message == "outside").Scope);
    }
}
