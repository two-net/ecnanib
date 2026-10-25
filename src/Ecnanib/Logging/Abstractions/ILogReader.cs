using Microsoft.Extensions.Logging;

namespace Ecnanib.Logging.Abstractions;

/// <summary>
/// A page of one agent's log (FR-LOG-07). <see cref="AgentId"/> is required: every query is narrowed by the indexed
/// agent first. <see cref="Search"/> matches the message case-insensitively; <see cref="From"/> is inclusive,
/// <see cref="To"/> exclusive; pages count from 1 and are capped at <see cref="MaxPageSize"/> rows.
/// </summary>
public sealed record LogQuery(
    Guid AgentId,
    LogLevel? MinLevel = null,
    string? Component = null,
    string? Search = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = LogQuery.DefaultPageSize)
{
    public const int DefaultPageSize = 50;

    public const int MaxPageSize = 200;

    public const int MaxSearchLength = 200;

    public const int MaxComponents = 200;
}

/// <summary>One row as the viewer shows it; <see cref="EvaluationId"/> is resolved from the decision so the row can link to its deliberation.</summary>
public sealed record LogEntryView(
    long Id,
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Component,
    string Message,
    string? Exception,
    Guid? AgentId,
    Guid? DecisionId,
    Guid? EvaluationId,
    Guid? OrderId,
    string? ContextJson);

/// <summary>Newest first; <see cref="HasMore"/> says whether an older page exists. <see cref="AgentName"/> saves the page a second read.</summary>
public sealed record LogPage(Guid AgentId, string AgentName, IReadOnlyList<LogEntryView> Entries, int Page, int PageSize, bool HasMore);

/// <summary>Reads the log of the user's own agents (FR-LOG-07, NFR-SEC-03). Another user's agent and a missing one look the same: null or empty.</summary>
public interface ILogReader
{
    Task<LogPage?> QueryAsync(Guid userId, LogQuery query, CancellationToken cancellationToken);

    /// <summary>The distinct components of the agent's rows, sorted, for the filter.</summary>
    Task<IReadOnlyList<string>> ComponentsAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);
}
