using System.Globalization;
using Ecnanib.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Components.Logging;

/// <summary>The log filter in the query string (FR-LOG-07): lenient parsing, so a hand-edited value means "all" rather than an error.</summary>
internal static class LogFilters
{
    public const string LevelName = "level";

    public const string ComponentName = "component";

    public const string SearchName = "q";

    public const string FromName = "from";

    public const string ToName = "to";

    public const string PageName = "page";

    private static readonly string[] TimeFormats = ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd"];

    public static readonly IReadOnlyList<(string Value, string Label, LogLevel Level)> Levels =
    [
        ("trace", "Trace", LogLevel.Trace), ("debug", "Debug", LogLevel.Debug), ("info", "Info", LogLevel.Information),
        ("warning", "Warning", LogLevel.Warning), ("error", "Error", LogLevel.Error), ("critical", "Critical", LogLevel.Critical),
    ];

    public static LogQuery Parse(Guid agentId, string? level, string? component, string? search, string? from, string? to, string? page, int pageSize = LogQuery.DefaultPageSize) =>
        new(agentId, ParseLevel(level), Trimmed(component), Trimmed(search), ParseTime(from), ParseTime(to), ParsePage(page), pageSize);

    public static LogLevel? ParseLevel(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        foreach (var (slug, _, level) in Levels)
        {
            if (string.Equals(slug, text, StringComparison.OrdinalIgnoreCase))
                return level;
        }

        return Enum.TryParse<LogLevel>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && parsed != LogLevel.None && !char.IsDigit(text[0]) ? parsed : null;
    }

    /// <summary>A datetime-local value, a date, or any ISO time; without a zone it is read as UTC.</summary>
    public static DateTimeOffset? ParseTime(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (DateTime.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var local))
            return new DateTimeOffset(local, TimeSpan.Zero);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var any) ? any.ToUniversalTime() : null;
    }

    public static int ParsePage(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) && page >= 1 ? page : 1;

    public static string LevelValue(LogLevel level) => Levels.FirstOrDefault(l => l.Level == level).Value ?? level.ToString().ToLowerInvariant();

    public static string ForInput(DateTimeOffset? at) => at is { } when ? when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture) : "";

    public static bool IsFiltered(LogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.MinLevel is not null || query.Component is not null || query.Search is not null || query.From is not null || query.To is not null;
    }

    /// <summary>The filter and the page as query-string pairs, empty when nothing is set and the page is the first.</summary>
    public static string Query(LogQuery query, int page)
    {
        ArgumentNullException.ThrowIfNull(query);
        var parts = new List<string>();
        if (query.MinLevel is { } level)
            parts.Add($"{LevelName}={LevelValue(level)}");
        if (query.Component is { } component)
            parts.Add($"{ComponentName}={Uri.EscapeDataString(component)}");
        if (query.Search is { } search)
            parts.Add($"{SearchName}={Uri.EscapeDataString(search)}");
        if (query.From is { } from)
            parts.Add($"{FromName}={ForInput(from)}");
        if (query.To is { } to)
            parts.Add($"{ToName}={ForInput(to)}");
        if (page > 1)
            parts.Add($"{PageName}={page.ToString(CultureInfo.InvariantCulture)}");
        return string.Join("&", parts);
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
