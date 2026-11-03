using System.Text.Json;
using Ecnanib.Platform.Contracts;
using Ecnanib.Platform.Logging;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Platform.Audit;

/// <summary>
/// Writes into the caller's context, so a record commits with its change or not at all. Changes are
/// masked with the logging rules, because this table has no SanitizingSink and FR-AUD-02 rules out
/// secrets. See Audit in docs/architecture.md.
/// </summary>
internal sealed class AuditTrail(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    ICorrelationContext correlation,
    ClientOriginContext origin,
    IConfiguration configuration) : IAuditTrail
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SecretRules _rules = SecretRules.From(configuration);

    public void Add(EcnanibDbContext context, AuditEntry entry)
    {
        var client = origin.Current;

        context.Set<AuditEvent>().Add(new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            UserId = entry.UserId,
            ActorKind = entry.ActorKind,
            ActorId = entry.ActorId,
            Action = entry.Action,
            TargetType = entry.TargetType,
            TargetId = entry.TargetId,
            Summary = entry.Changes.Count == 0 ? null : JsonSerializer.Serialize(entry.Changes.Select(Mask), Json),
            IpAddress = client?.IpAddress,
            UserAgent = client?.UserAgent,
            CorrelationId = correlation.CurrentId,
        });
    }

    public async Task<IReadOnlyList<AuditRecord>> ListForUserAsync(
        Guid userId,
        long? before,
        int count,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var records = context.Set<AuditEvent>().AsNoTracking().Where(record => record.UserId == userId);

        if (before is { } cursor)
        {
            records = records.Where(record => record.Id < cursor);
        }

        var page = await records.OrderByDescending(record => record.Id).Take(count).ToListAsync(cancellationToken);

        return [.. page.Select(ToRecord)];
    }

    private static AuditRecord ToRecord(AuditEvent record) => new(
        record.Id,
        record.Timestamp,
        record.Action,
        record.ActorKind,
        record.ActorId,
        record.UserId,
        record.TargetType,
        record.TargetId,
        record.Summary is null ? [] : JsonSerializer.Deserialize<List<AuditChange>>(record.Summary, Json) ?? [],
        record.IpAddress,
        record.UserAgent,
        record.CorrelationId);

    /// <summary>A null stays null, so the summary still shows that a secret was set or removed.</summary>
    private AuditChange Mask(AuditChange change) =>
        _rules.IsSensitiveName(change.Field)
            ? change with { Before = change.Before is null ? null : SecretRules.Mask, After = change.After is null ? null : SecretRules.Mask }
            : change with { Before = Scrub(change.Before), After = Scrub(change.After) };

    private string? Scrub(string? value) => value is null ? null : _rules.Scrub(value);
}
