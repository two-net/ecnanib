using System.Collections;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Logging.Abstractions;

/// <summary>The names of the identifiers a log event may carry (FR-LOG-02), as message-template arguments or through a <see cref="LogScope"/>.</summary>
public static class LogProperties
{
    public const string UserId = "UserId";

    public const string AgentId = "AgentId";

    public const string DecisionId = "DecisionId";

    public const string OrderId = "OrderId";

    public const string EvaluationId = "EvaluationId";

    public const string TradeId = "TradeId";
}

/// <summary>
/// The ids a stretch of work is about, as a logging scope (FR-LOG-02): every event logged inside it carries the set
/// ids as structured properties, which the database sink stores in their columns (user, agent, decision, order) or
/// in the entry's context (evaluation, trade). A message's own argument of the same name wins over the scope, and an
/// inner scope over an outer one.
/// </summary>
public sealed class LogScope : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly KeyValuePair<string, object?>[] _items;

    public LogScope(Guid? agentId = null, Guid? userId = null, Guid? evaluationId = null, Guid? decisionId = null, Guid? orderId = null, Guid? tradeId = null)
    {
        var items = new List<KeyValuePair<string, object?>>(6);
        Add(items, LogProperties.AgentId, agentId);
        Add(items, LogProperties.UserId, userId);
        Add(items, LogProperties.EvaluationId, evaluationId);
        Add(items, LogProperties.DecisionId, decisionId);
        Add(items, LogProperties.OrderId, orderId);
        Add(items, LogProperties.TradeId, tradeId);
        _items = items.ToArray();
    }

    public int Count => _items.Length;

    public KeyValuePair<string, object?> this[int index] => _items[index];

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>What a text-only sink prints for the scope: <c>AgentId=…, UserId=…</c>.</summary>
    public override string ToString() => string.Join(", ", _items.Select(item => $"{item.Key}={item.Value}"));

    private static void Add(List<KeyValuePair<string, object?>> items, string name, Guid? value)
    {
        if (value is { } id)
            items.Add(new KeyValuePair<string, object?>(name, id));
    }
}

public static class LoggerScopeExtensions
{
    /// <summary>Opens a <see cref="LogScope"/> with the given ids; dispose the result to close it.</summary>
    public static IDisposable? BeginScope(this ILogger logger, Guid? agentId = null, Guid? userId = null, Guid? evaluationId = null, Guid? decisionId = null, Guid? orderId = null, Guid? tradeId = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return logger.BeginScope(new LogScope(agentId, userId, evaluationId, decisionId, orderId, tradeId));
    }
}
