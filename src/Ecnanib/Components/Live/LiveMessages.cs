using System.Globalization;

namespace Ecnanib.Components.Live;

/// <summary>User-facing texts of the live islands (FR-UI-03, FR-UI-04). Everything an animation shows is also said here in words.</summary>
internal static class LiveMessages
{
    public const string Evaluating = "Evaluating…";

    public const string Loading = "Loading…";

    public const string TodayUtc = "Today (UTC)";

    public const string LiveEquityNote = "A live agent's equity is the exchange account's, shared by every agent on the key; see your Binance balance.";

    public const string PriceWaiting = "waiting for a price";

    /// <summary>"in 12 min 05 s", "in 1 h 02 min", "in 3 d 04 h" or "now" once the close is due.</summary>
    public static string NextEvaluation(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return "now";
        if (remaining.TotalDays >= 1)
            return $"in {(int)remaining.TotalDays} d {remaining.Hours:00} h";
        if (remaining.TotalHours >= 1)
            return $"in {(int)remaining.TotalHours} h {remaining.Minutes:00} min";
        return $"in {(int)remaining.TotalMinutes} min {remaining.Seconds:00} s";
    }

    /// <summary>A price with up to eight decimals and no trailing zeros, or the waiting text.</summary>
    public static string Price(decimal? price) =>
        price is { } p ? p.ToString("0.########", CultureInfo.InvariantCulture) : PriceWaiting;
}
