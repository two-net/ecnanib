using System.Text.Json;
using Ecnanib.Logging.Abstractions;
using Ecnanib.Logging.Services;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Ecnanib.Tests.Logging.Services;

/// <summary>FR-LOG-02: how a Serilog event, as the ILogger bridge builds it, becomes a log_entries row.</summary>
public class LogEventMapperTests
{
    private const string Category = "Ecnanib.Tests.Logging.Mapper";

    [Fact]
    public void PromotesIdsFromArgumentsAndScopesToColumnsAndLeavesNoContext()
    {
        var (logger, sink, factory) = Bridge();
        using var bridge = factory;
        var agent = Guid.NewGuid();
        var user = Guid.NewGuid();
        var decision = Guid.NewGuid();
        var order = Guid.NewGuid();

        using (logger.BeginScope(agentId: agent, userId: user))
            logger.LogWarning("Decision {DecisionId} placed order {OrderId}", decision, order);
        var entry = LogEventMapper.Map(Assert.Single(sink.Events));

        Assert.Equal(Category, entry.Component);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal($"Decision {decision} placed order {order}", entry.Message);
        Assert.Equal((user, agent, decision, order), (entry.UserId, entry.AgentId, entry.DecisionId, entry.OrderId));
        Assert.Null(entry.Exception);
        Assert.Null(entry.Context);
        Assert.Equal(sink.Events[0].Timestamp, entry.Timestamp);
    }

    [Fact]
    public void TheArgumentWinsOverTheScopeAndTheInnerScopeOverTheOuter()
    {
        var (logger, sink, factory) = Bridge();
        using var bridge = factory;
        var outer = Guid.NewGuid();
        var inner = Guid.NewGuid();
        var argument = Guid.NewGuid();

        using (logger.BeginScope(agentId: outer))
        {
            logger.LogInformation("outer");
            using (logger.BeginScope(agentId: inner))
            {
                logger.LogInformation("inner");
                logger.LogInformation("Agent {AgentId} as argument", argument);
            }
        }

        Assert.Equal([outer, inner, argument], sink.Events.Select(e => LogEventMapper.Map(e).AgentId));
    }

    [Fact]
    public void KeepsTheRemainingPropertiesAsFlatJsonContext()
    {
        var (logger, sink, factory) = Bridge();
        using var bridge = factory;
        var evaluation = Guid.NewGuid();
        var trade = Guid.NewGuid();
        var when = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);

        using (logger.BeginScope(evaluationId: evaluation, tradeId: trade))
            logger.LogInformation(new EventId(7, "Seven"), "Candle {CandleOpen:O} closed at {Close} after {Count} ticks ({Level}, {Flag}, {Nothing})", when, 1.5m, 3, LogLevel.Critical, true, null);
        var context = LogEventMapper.Map(Assert.Single(sink.Events)).Context;

        Assert.NotNull(context);
        Assert.Equal(evaluation.ToString(), context.Value.GetProperty("EvaluationId").GetString());
        Assert.Equal(trade.ToString(), context.Value.GetProperty("TradeId").GetString());
        Assert.Equal("2026-01-05T10:00:00.0000000+00:00", context.Value.GetProperty("CandleOpen").GetString());
        Assert.Equal(1.5m, context.Value.GetProperty("Close").GetDecimal());
        Assert.Equal(3, context.Value.GetProperty("Count").GetInt32());
        Assert.Equal("Critical", context.Value.GetProperty("Level").GetString());
        Assert.True(context.Value.GetProperty("Flag").GetBoolean());
        Assert.Equal(JsonValueKind.Null, context.Value.GetProperty("Nothing").ValueKind);
        Assert.Equal(7, context.Value.GetProperty("EventId").GetProperty("Id").GetInt32());
        Assert.False(context.Value.TryGetProperty("SourceContext", out _));
    }

    [Fact]
    public void ParsesIdsGivenAsTextAndKeepsOtherTextInTheContext()
    {
        var (logger, sink, factory) = Bridge();
        using var bridge = factory;
        var agent = Guid.NewGuid();

        logger.LogInformation("Agent {AgentId} and order {OrderId}", agent.ToString(), "not-an-id");
        var entry = LogEventMapper.Map(Assert.Single(sink.Events));

        Assert.Equal(agent, entry.AgentId);
        Assert.Null(entry.OrderId);
        Assert.Equal("not-an-id", entry.Context!.Value.GetProperty("OrderId").GetString());
    }

    [Fact]
    public void NonStructuredScopesAppearUnderScope()
    {
        var (logger, sink, factory) = Bridge();
        using var bridge = factory;

        using (logger.BeginScope("request {RequestId}", 42))
        using (logger.BeginScope("plain text"))
            logger.LogInformation("inside");
        var context = LogEventMapper.Map(Assert.Single(sink.Events)).Context!.Value;

        Assert.Equal(42, context.GetProperty("RequestId").GetInt32());
        // Outermost first, as the bridge reports them.
        Assert.Equal(["request 42", "plain text"], context.GetProperty("Scope").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Critical)]
    public void MapsEveryLevelBackToTheOneThatWasLogged(LogLevel level)
    {
        var (logger, sink, factory) = Bridge();
        using var bridge = factory;

        logger.Log(level, "at {Level}", level);

        Assert.Equal(level, LogEventMapper.Map(Assert.Single(sink.Events)).Level);
    }

    [Fact]
    public void TruncatesComponentMessageExceptionAndValues()
    {
        var sink = new CollectingLogEventSink();
        var serilog = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        using var factory = new SerilogLoggerFactory(serilog, dispose: true);
        var logger = factory.CreateLogger(new string('c', 300));
        var exception = new InvalidOperationException(new string('e', 20_000));

        logger.LogError(exception, "{Text} {Payload}", new string('m', 5_000), new string('p', 600));
        var entry = LogEventMapper.Map(Assert.Single(sink.Events));

        Assert.Equal(LogEventMapper.MaxComponentLength, entry.Component.Length);
        Assert.EndsWith("…", entry.Component, StringComparison.Ordinal);
        Assert.Equal(LogEventMapper.MaxMessageLength, entry.Message.Length);
        Assert.Equal(LogEventMapper.MaxExceptionLength, entry.Exception!.Length);
        Assert.StartsWith("System.InvalidOperationException", entry.Exception, StringComparison.Ordinal);
        Assert.Equal(LogEventMapper.MaxValueLength, entry.Context!.Value.GetProperty("Payload").GetString()!.Length);
        Assert.Equal(LogEventMapper.MaxValueLength, entry.Context.Value.GetProperty("Text").GetString()!.Length);
    }

    [Fact]
    public void RemovesNulCharactersEverywhere()
    {
        var (logger, sink, factory) = Bridge();
        using var bridge = factory;

        logger.LogError(new InvalidOperationException("bad\0byte"), "a\0b {Value} {List}", "x\0y", new[] { "p\0q" });
        var entry = LogEventMapper.Map(Assert.Single(sink.Events));

        Assert.Equal("ab xy [\"pq\"]", entry.Message);
        Assert.DoesNotContain('\0', entry.Exception!);
        Assert.Contains("badbyte", entry.Exception, StringComparison.Ordinal);
        var json = entry.Context!.Value.GetRawText();
        Assert.DoesNotContain("u0000", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("xy", entry.Context.Value.GetProperty("Value").GetString());
        Assert.Equal("pq", entry.Context.Value.GetProperty("List")[0].GetString());
    }

    [Fact]
    public void RendersTheMessageAsTheConsoleDoes()
    {
        var (logger, sink, factory) = Bridge();
        using var bridge = factory;

        var agent = Guid.NewGuid();
        var when = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);

        logger.LogInformation("Agent {AgentId} of {Owner} connected to {Database} at {When:O} after {Count} tries ({Settings})", agent, "trader", "ecnanib", when, 3, new { Pool = 3 });

        Assert.Equal($"Agent {agent} of trader connected to ecnanib at 2026-01-05T10:00:00.0000000+00:00 after 3 tries ({{ Pool = 3 }})", LogEventMapper.Map(Assert.Single(sink.Events)).Message);
    }

    [Fact]
    public void AnEventWithoutACategoryGetsTheFallbackComponent()
    {
        var entry = LogEventMapper.Map(SinkLevelFilterTests.Event(LogEventLevel.Fatal, null, "no context"));

        Assert.Equal(LogEventMapper.FallbackComponent, entry.Component);
        Assert.Equal(LogLevel.Critical, entry.Level);
        Assert.Equal("no context", entry.Message);
        Assert.Null(entry.Context);
    }

    [Fact]
    public void TheFallbackRowNamesTheTemplateAndTheFailure()
    {
        var logEvent = SinkLevelFilterTests.Event(LogEventLevel.Warning, "Ecnanib.X", "Agent {AgentId} did {Thing}");

        var entry = LogEventMapper.Fallback(logEvent, new InvalidOperationException("boom"));

        Assert.Equal("Ecnanib.X", entry.Component);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Agent {AgentId} did {Thing} (the event could not be mapped: boom)", entry.Message);
        Assert.Null(entry.Context);
        Assert.Null(entry.AgentId);
    }

    /// <summary>An ILogger over Serilog with a collecting sink: events exactly as the application's bridge produces them.</summary>
    internal static (ILogger Logger, CollectingLogEventSink Sink, IDisposable Factory) Bridge(string category = Category)
    {
        var sink = new CollectingLogEventSink();
        var serilog = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var factory = new SerilogLoggerFactory(serilog, dispose: true);
        return (factory.CreateLogger(category), sink, factory);
    }
}
