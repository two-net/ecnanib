using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway.Adapters.Binance;

/// <summary>
/// Reads Binance's clocks on a schedule, every <c>Binance:ClockCheckSeconds</c> of the
/// configuration file and as the background work starts, and tells <see cref="BinanceClock"/>
/// what each said (FR-BIN-5). See docs/exchange.md.
/// </summary>
/// <remarks>
/// <para>
/// A run reads the clock of each market in each environment, all four at once: each is counted
/// against a limit of its own, so one that waits or does not answer holds no other up. A read is
/// the service's own call, which needs no credential and costs the least that a call can.
/// </para>
/// <para>
/// A clock that cannot be read does not fail the run: the watch is told, and the clock is read
/// again with the next run. A run keeps nothing but what the watch has in memory, so it can end
/// anywhere.
/// </para>
/// </remarks>
internal sealed class BinanceClockJob(BinanceCall call, BinanceClock watch, BinanceSettings settings)
    : IBackgroundJob, IDueAtStart
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "clock-watch";

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } =
        BackgroundJobSchedule.Every(TimeSpan.FromSeconds(settings.ClockCheckSeconds));

    public Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken) =>
        Task.WhenAll(BinanceClock.Clocks.Select(clock => ReadAsync(clock.Market, clock.Mode, cancellationToken)));

    /// <summary>
    /// The clocks are read as the background work starts, and not when the interval since a run
    /// before the start has passed: the readings are kept in memory, so a start has none, and
    /// new entries are paused until there is one.
    /// </summary>
    public Task<bool> IsDueAtStartAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    private async Task ReadAsync(Market market, TradingMode mode, CancellationToken cancellationToken)
    {
        var answer = await call.AskAsync(BinanceEndpoint.TimeOf(market), mode, BinanceAdapter.ServerTime, cancellationToken);

        if (answer.Value is { } serverTime)
        {
            watch.Read(market, mode, serverTime, answer.SentAt, answer.Took);
        }
        else
        {
            watch.Unread(market, mode);
        }
    }
}
