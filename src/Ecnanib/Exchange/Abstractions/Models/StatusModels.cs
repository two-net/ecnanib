namespace Ecnanib.Exchange.Abstractions;

public sealed record StreamStatus(
    string Name,
    StreamHealth Health,
    DateTimeOffset? LastMessageAt,
    DateTimeOffset Since,
    string? LastError);

public sealed record RateLimitUsage(string Limit, int Current, int Max, TimeSpan Window);

public sealed record RateLimitSnapshot(
    IReadOnlyList<RateLimitUsage> Usages,
    DateTimeOffset? LastTriggeredAt,
    DateTimeOffset? RetryAfterUntil)
{
    public static RateLimitSnapshot Empty { get; } = new([], null, null);
}
