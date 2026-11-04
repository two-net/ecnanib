namespace Ecnanib.Modules.ExchangeAdapter.Contracts;

/// <summary>
/// How much of each server's limits this process is using, for the system health page (FR-ADM-04, #49).
/// It's held in memory, so it starts empty after a restart.
/// </summary>
public interface IExchangeUsage
{
    IReadOnlyList<ExchangeServerUsage> Current();
}

/// <param name="UsedWeight">In the current minute, as Binance last reported it, plus requests in flight.</param>
/// <param name="WeightLimit">Per minute, from exchange information, or Binance's published default until that loads.</param>
/// <param name="HeadroomPercent">How much of the limit only protective requests may use (Binance:WeightHeadroomPercent).</param>
/// <param name="BackoffUntil">Set after HTTP 429, when it's still in the future.</param>
/// <param name="BannedUntil">Set after HTTP 418, when it's still in the future.</param>
/// <param name="ClockOffset">Binance's time minus this server's, or null before the first sync.</param>
public sealed record ExchangeServerUsage(
    ExchangeServer Server,
    int UsedWeight,
    int WeightLimit,
    int HeadroomPercent,
    DateTimeOffset? BackoffUntil,
    DateTimeOffset? BannedUntil,
    TimeSpan? ClockOffset,
    DateTimeOffset? ClockSyncedAt);
