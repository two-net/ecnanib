using System.Collections.Concurrent;
using Binance.Net;
using CryptoExchange.Net.RateLimiting;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Exchange.Binance;

/// <summary>
/// Observes Binance.Net's client-side rate limiter (EX-05): logs every time a limit is hit and keeps the latest usage
/// per limit for the health status (NFR-OBS-01). Adds no requests of its own.
/// </summary>
internal sealed class BinanceRateLimitMonitor : IHostedService
{
    private readonly ILogger<BinanceRateLimitMonitor> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, RateLimitUsage> _usages = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastTriggeredAt;
    private DateTimeOffset? _retryAfterUntil;

    public BinanceRateLimitMonitor(ILogger<BinanceRateLimitMonitor> logger, TimeProvider time)
    {
        _logger = logger;
        _time = time;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        BinanceExchange.RateLimiter.RateLimitTriggered += OnTriggered;
        BinanceExchange.RateLimiter.RateLimitUpdated += OnUpdated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        BinanceExchange.RateLimiter.RateLimitTriggered -= OnTriggered;
        BinanceExchange.RateLimiter.RateLimitUpdated -= OnUpdated;
        return Task.CompletedTask;
    }

    public RateLimitSnapshot Snapshot() => new(
        _usages.Values.OrderBy(u => u.Limit, StringComparer.Ordinal).ToArray(),
        _lastTriggeredAt,
        _retryAfterUntil);

    internal void OnTriggered(RateLimitEvent e)
    {
        var now = _time.GetUtcNow();
        _lastTriggeredAt = now;
        var delay = e.DelayTime;
        if (delay > TimeSpan.Zero)
            _retryAfterUntil = now + delay;

        _logger.LogWarning(
            "Binance rate limit {Limit} reached ({Current}/{Max} per {Window}); {Behaviour} for {Delay}",
            e.LimitDescription, e.Current, e.Limit, e.TimePeriod, e.Behaviour, delay);
    }

    internal void OnUpdated(RateLimitUpdateEvent e)
    {
        var name = string.IsNullOrEmpty(e.LimitDescription) ? $"limit-{e.ItemId}" : e.LimitDescription;
        _usages[name] = new RateLimitUsage(name, e.Current, e.Limit ?? 0, e.TimePeriod ?? TimeSpan.Zero);
    }
}
