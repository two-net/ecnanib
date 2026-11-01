using System.Globalization;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace Ecnanib.Platform.Logging;

/// <summary>A row in the optional database log (FR-LOG-02). Purging is #50's job, which the time index serves.</summary>
internal sealed class LogRecord
{
    private static readonly JsonValueFormatter Json = new(typeTagName: null);

    public long Id { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string Level { get; init; }

    public string? Category { get; init; }

    public required string Message { get; init; }

    public string? Exception { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>Every property of the event, as JSON.</summary>
    public required string Properties { get; init; }

    public static LogRecord From(LogEvent logEvent) => new()
    {
        Timestamp = logEvent.Timestamp.ToUniversalTime(),
        Level = logEvent.Level.ToString(),
        Category = TextOf(logEvent, "SourceContext"),
        Message = logEvent.RenderMessage(CultureInfo.InvariantCulture),
        Exception = logEvent.Exception?.ToString(),
        CorrelationId = TextOf(logEvent, LogProperties.CorrelationId),
        Properties = ToJson(logEvent),
    };

    private static string? TextOf(LogEvent logEvent, string property) =>
        logEvent.Properties.GetValueOrDefault(property) is ScalarValue { Value: { } value }
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;

    private static string ToJson(LogEvent logEvent)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);

        Json.Format(
            new StructureValue(logEvent.Properties.Select(property => new LogEventProperty(property.Key, property.Value))),
            writer);

        return writer.ToString();
    }
}

internal sealed class LogRecordConfiguration : IEntityTypeConfiguration<LogRecord>
{
    public void Configure(EntityTypeBuilder<LogRecord> builder)
    {
        builder.ToTable("log_events");
        builder.HasKey(record => record.Id);
        builder.Property(record => record.Properties).HasColumnType("jsonb");
        builder.HasIndex(record => record.Timestamp);
        builder.HasIndex(record => record.CorrelationId);
    }
}
