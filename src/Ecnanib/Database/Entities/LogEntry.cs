using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Database.Entities;

/// <summary>
/// One log event in the database sink (FR-LOG-01, FR-LOG-02). Deliberately has no foreign keys: a sink must never
/// fail because a referenced row vanished (FR-LOG-08). Account deletion removes a user's rows explicitly
/// (FR-ACC-04); expired rows are swept after the configured retention (FR-LOG-06).
/// </summary>
public sealed class LogEntry
{
    public long Id { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public LogLevel Level { get; set; }

    public required string Component { get; set; }

    public required string Message { get; set; }

    public string? Exception { get; set; }

    public Guid? UserId { get; set; }

    public Guid? AgentId { get; set; }

    public Guid? DecisionId { get; set; }

    public Guid? OrderId { get; set; }

    public JsonElement? Context { get; set; }
}
