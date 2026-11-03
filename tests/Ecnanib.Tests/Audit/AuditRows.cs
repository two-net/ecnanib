using Npgsql;

namespace Ecnanib.Tests.Audit;

/// <summary>An audit record as the table holds it.</summary>
internal sealed record AuditRow(
    long Id,
    DateTimeOffset Timestamp,
    Guid? UserId,
    string ActorKind,
    Guid? ActorId,
    string Action,
    string? TargetType,
    string? TargetId,
    string? Summary,
    string? IpAddress,
    string? UserAgent,
    string? CorrelationId);

/// <summary>Reads audit_events directly, so assertions don't rely on the application, oldest first.</summary>
internal static class AuditRows
{
    public static Task<List<AuditRow>> OfUserAsync(string connectionString, Guid userId, CancellationToken cancellationToken) =>
        WhereAsync(connectionString, "user_id = @value", userId, cancellationToken);

    public static Task<List<AuditRow>> WithCorrelationAsync(string connectionString, string correlationId, CancellationToken cancellationToken) =>
        WhereAsync(connectionString, "correlation_id = @value", correlationId, cancellationToken);

    /// <summary>How many records hold <paramref name="text"/> in any column, which is how a test proves something wasn't kept.</summary>
    public static async Task<long> CountHoldingAsync(string connectionString, string text, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand("SELECT count(*) FROM audit_events AS record WHERE strpos(record::text, @value) > 0");
        command.Parameters.AddWithValue("value", text);

        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<List<AuditRow>> WhereAsync(
        string connectionString,
        string condition,
        object value,
        CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand(
            $"""
            SELECT id, timestamp, user_id, actor_kind, actor_id, action, target_type, target_id, summary::text,
                   ip_address, user_agent, correlation_id
            FROM audit_events
            WHERE {condition}
            ORDER BY id
            """);
        command.Parameters.AddWithValue("value", value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<AuditRow>();

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AuditRow(
                reader.GetInt64(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return rows;
    }
}
