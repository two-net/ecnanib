using Ecnanib.Database;
using Ecnanib.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Logging.Services;

/// <summary>
/// Reads <c>log_entries</c> for one of the user's agents: the agent must be the user's, and a row counts when it names
/// the agent and either the user or no user at all (the scheduler and the fill loops log agent rows without one). The
/// <c>(agent_id, timestamp)</c> index serves every query; a text search is an escaped <c>ILIKE</c> on the message
/// after that narrowing, so it stays bounded.
/// </summary>
internal sealed class LogReader : ILogReader
{
    private readonly IDbContextFactory<EcnanibDbContext> _factory;

    public LogReader(IDbContextFactory<EcnanibDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<LogPage?> QueryAsync(Guid userId, LogQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agentName = await db.Agents.AsNoTracking()
            .Where(a => a.Id == query.AgentId && a.UserId == userId)
            .Select(a => a.Name)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (agentName is null)
            return null;

        var rows = db.LogEntries.AsNoTracking().Where(l => l.AgentId == query.AgentId && (l.UserId == null || l.UserId == userId));
        if (query.MinLevel is { } minimum)
            rows = rows.Where(l => l.Level >= minimum);
        if (!string.IsNullOrWhiteSpace(query.Component))
        {
            var component = query.Component.Trim();
            rows = rows.Where(l => l.Component == component);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            if (term.Length > LogQuery.MaxSearchLength)
                term = term[..LogQuery.MaxSearchLength];
            var pattern = "%" + EscapeLike(term) + "%";
            rows = rows.Where(l => EF.Functions.ILike(l.Message, pattern, "\\"));
        }

        if (query.From is { } from)
            rows = rows.Where(l => l.Timestamp >= from);
        if (query.To is { } to)
            rows = rows.Where(l => l.Timestamp < to);

        var size = Math.Clamp(query.PageSize, 1, LogQuery.MaxPageSize);
        var page = Math.Max(1, query.Page);
        var entries = await rows
            .OrderByDescending(l => l.Timestamp).ThenByDescending(l => l.Id)
            .Skip((page - 1) * size)
            .Take(size + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = entries.Count > size;
        if (hasMore)
            entries.RemoveAt(entries.Count - 1);

        // Decisions are kept for the life of the account, so the link to a deliberation is one lookup per page.
        var decisionIds = entries.Where(l => l.DecisionId != null).Select(l => l.DecisionId!.Value).Distinct().ToList();
        var evaluations = decisionIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await db.Decisions.AsNoTracking()
                .Where(d => decisionIds.Contains(d.Id))
                .Select(d => new { d.Id, d.EvaluationId })
                .ToDictionaryAsync(d => d.Id, d => d.EvaluationId, cancellationToken).ConfigureAwait(false);

        return new LogPage(query.AgentId, agentName, entries.Select(l => new LogEntryView(
            l.Id, l.Timestamp, l.Level, l.Component, l.Message, l.Exception, l.AgentId, l.DecisionId,
            l.DecisionId is { } decision && evaluations.TryGetValue(decision, out var evaluation) ? evaluation : null,
            l.OrderId, l.Context?.GetRawText())).ToList(), page, size, hasMore);
    }

    public async Task<IReadOnlyList<string>> ComponentsAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!await db.Agents.AnyAsync(a => a.Id == agentId && a.UserId == userId, cancellationToken).ConfigureAwait(false))
            return [];
        return await db.LogEntries.AsNoTracking()
            .Where(l => l.AgentId == agentId && (l.UserId == null || l.UserId == userId))
            .Select(l => l.Component)
            .Distinct()
            .OrderBy(c => c)
            .Take(LogQuery.MaxComponents)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Makes a search term literal inside a LIKE pattern: the escape character itself, <c>%</c> and <c>_</c>.</summary>
    internal static string EscapeLike(string term) => term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
