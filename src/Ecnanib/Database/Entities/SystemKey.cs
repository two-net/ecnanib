namespace Ecnanib.Database.Entities;

/// <summary>A key the system generates for its own operation on first use (FR-CFG-05, A-01), by purpose.</summary>
public sealed class SystemKey
{
    public required string Purpose { get; set; }

    public required string Value { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
