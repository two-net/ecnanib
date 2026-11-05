using System.Globalization;
using System.Text.Json;
using Ecnanib.Modules.MarketData.Contracts;

namespace Ecnanib.Tests.MarketData;

/// <summary>FR-CYC-02 step 4, and the issue's acceptance criterion: indicator values match fixed reference data.</summary>
public sealed class IndicatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 300 BTCUSDT 1h candles from Binance's public market data, ending with the one that opened at
    /// 2026-11-04T19:00Z, and TA-Lib 0.8.3's values for them, from the whole series and from its first 230
    /// candles. They were made with:
    /// <code>
    /// curl "https://data-api.binance.vision/api/v3/klines?symbol=BTCUSDT&amp;interval=1h&amp;endTime=1793800800000&amp;limit=300"
    /// talib.EMA(close, 20)[-1], talib.EMA(close, 50)[-1], talib.EMA(close, 200)[-1], talib.RSI(close, 14)[-1], talib.ATR(high, low, close, 14)[-1]
    /// </code>
    /// TA-Lib computes in double, and these in decimal, so they agree to about 15 significant digits.
    /// </summary>
    [Theory]
    [InlineData(300, "78022.87239187252", "77730.61204918148", "78550.52773589769", "69.90761551267794", "524.7703523142098")]
    [InlineData(230, "77919.4313420599", "77943.34794901618", "79480.16033982768", "60.109262096884976", "207.65769699591445")]
    public void Moving_averages_RSI_and_ATR_match_TA_Lib(int count, string ema20, string ema50, string ema200, string rsi14, string atr14)
    {
        var indicators = Indicators.Compute([.. Reference().Take(count)]);

        Close(ema20, indicators.Ema20);
        Close(ema50, indicators.Ema50);
        Close(ema200, indicators.Ema200);
        Close(rsi14, indicators.Rsi14);
        Close(atr14, indicators.Atr14);
    }

    [Fact]
    public void The_same_candles_always_give_the_same_values()
    {
        var candles = Reference();

        Assert.Equal(Indicators.Compute(candles), Indicators.Compute([.. candles]), IndicatorSetComparer.Instance);
    }

    [Fact]
    public void An_indicator_without_enough_candles_is_null()
    {
        var candles = Reference();

        var short199 = Indicators.Compute([.. candles.Take(199)]);
        Assert.Null(short199.Ema200);
        Assert.NotNull(short199.Ema50);

        Assert.NotNull(Indicators.Compute([.. candles.Take(200)]).Ema200);

        var fourteen = Indicators.Compute([.. candles.Take(14)]);
        Assert.Equal((null, null, null, (decimal?)null), (fourteen.Ema20, fourteen.Rsi14, fourteen.Atr14, fourteen.Volume?.Average));

        var fifteen = Indicators.Compute([.. candles.Take(15)]);
        Assert.NotNull(fifteen.Rsi14);
        Assert.NotNull(fifteen.Atr14);
        Assert.Null(fifteen.Volume);

        var empty = Indicators.Compute([]);
        Assert.Equal((0, 0), (empty.SwingHighs.Count, empty.SwingLows.Count));
    }

    [Fact]
    public void A_flat_series_has_an_RSI_of_50_and_a_rising_one_100()
    {
        var flat = Series([.. Enumerable.Repeat(10m, 30)]);
        var rising = Series([.. Enumerable.Range(1, 30).Select(value => (decimal)value)]);

        Assert.Equal(50m, Indicators.Compute(flat).Rsi14);
        Assert.Equal(100m, Indicators.Compute(rising).Rsi14);
        Assert.Equal(10m, Indicators.Compute(flat).Ema20);
    }

    [Fact]
    public void ATR_uses_the_gap_from_the_previous_close()
    {
        // Each candle's range is 1, but it opens 5 above the last close, so its true range is 6 (5 + 1).
        var candles = Enumerable.Range(0, 15)
            .Select(i => new Candle(Start.AddHours(i), 5m * i, (5m * i) + 1, 5m * i, 5m * i, 1m, 1m, 1))
            .ToList();

        Assert.Equal(6m, Indicators.Compute(candles).Atr14);
    }

    [Fact]
    public void Swings_stand_out_from_three_candles_on_each_side_and_a_flat_top_counts_once()
    {
        decimal[] highs = [1, 2, 3, 9, 9, 4, 3, 2, 5, 6, 7, 12, 6, 5, 4, 11, 1, 1];
        var candles = highs.Select((high, i) => new Candle(Start.AddHours(i), high, high, high - 1, high, 1m, 1m, 1)).ToList();

        var swings = Indicators.Compute(candles).SwingHighs;

        // 11 at index 15 has only two candles after it, so it isn't confirmed. 9 at index 4 doesn't beat the 9 before it.
        Assert.Equal([new SwingPoint(Start.AddHours(11), 12m), new SwingPoint(Start.AddHours(3), 9m)], swings);
    }

    [Fact]
    public void Swing_lows_mirror_swing_highs_and_only_the_latest_five_are_kept()
    {
        // A low of 0 every 6 candles: 3 2 1 0 1 2 3 2 1 0 ...
        var candles = Enumerable.Range(0, 60)
            .Select(i => (decimal)Math.Abs((i % 6) - 3))
            .Select((low, i) => new Candle(Start.AddHours(i), low + 1, low + 2, low, low + 1, 1m, 1m, 1))
            .ToList();

        var lows = Indicators.Compute(candles).SwingLows;

        Assert.Equal(Indicators.SwingCount, lows.Count);
        Assert.All(lows, low => Assert.Equal(0m, low.Price));
        Assert.Equal([.. lows.OrderByDescending(low => low.OpenTime)], lows);
        // The low at 57 has only two candles after it, so the newest confirmed one is at 51.
        Assert.Equal(Start.AddHours(51), lows[0].OpenTime);
    }

    [Fact]
    public void Volume_is_the_last_candle_against_the_average_of_the_last_twenty()
    {
        var candles = Enumerable.Range(1, 25)
            .Select(i => new Candle(Start.AddHours(i), 1m, 1m, 1m, 1m, i, i, 1))
            .ToList();

        // The last twenty are 6 to 25, which average 15.5.
        Assert.Equal(new VolumeStatistics(25m, 15.5m, 25m / 15.5m), Indicators.Compute(candles).Volume);

        var silent = Enumerable.Range(1, 20).Select(i => new Candle(Start.AddHours(i), 1m, 1m, 1m, 1m, 0m, 0m, 0)).ToList();
        Assert.Equal(new VolumeStatistics(0m, 0m, null), Indicators.Compute(silent).Volume);
    }

    [Fact]
    public void Candles_out_of_order_are_refused()
    {
        var candles = Reference().Take(3).Reverse().ToList();

        Assert.Throws<ArgumentException>(() => Indicators.Compute(candles));
    }

    /// <summary>The reference candles, as Binance sent them.</summary>
    internal static IReadOnlyList<Candle> Reference()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "MarketData", "Samples", "btcusdt-1h.json")));

        return
        [
            .. document.RootElement.EnumerateArray().Select(row => new Candle(
                DateTimeOffset.FromUnixTimeMilliseconds(row[0].GetInt64()),
                Decimal(row[1]),
                Decimal(row[2]),
                Decimal(row[3]),
                Decimal(row[4]),
                Decimal(row[5]),
                Decimal(row[7]),
                row[8].GetInt64())),
        ];
    }

    private static decimal Decimal(JsonElement value) => decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture);

    private static List<Candle> Series(IReadOnlyList<decimal> closes) =>
        [.. closes.Select((close, i) => new Candle(Start.AddHours(i), close, close, close, close, 1m, close, 1))];

    private static void Close(string expected, decimal? actual)
    {
        var reference = decimal.Parse(expected, CultureInfo.InvariantCulture);

        Assert.NotNull(actual);
        Assert.True(
            Math.Abs(actual.Value - reference) <= Math.Abs(reference) * 1e-12m,
            $"Expected {expected}, but got {actual.Value.ToString(CultureInfo.InvariantCulture)}.");
    }

    /// <summary>Records compare their lists by reference, so the swings are compared by value here.</summary>
    private sealed class IndicatorSetComparer : IEqualityComparer<IndicatorSet>
    {
        public static readonly IndicatorSetComparer Instance = new();

        public bool Equals(IndicatorSet? x, IndicatorSet? y) =>
            x is not null && y is not null
            && (x.Ema20, x.Ema50, x.Ema200, x.Rsi14, x.Atr14, x.Volume) == (y.Ema20, y.Ema50, y.Ema200, y.Rsi14, y.Atr14, y.Volume)
            && x.SwingHighs.SequenceEqual(y.SwingHighs)
            && x.SwingLows.SequenceEqual(y.SwingLows);

        public int GetHashCode(IndicatorSet obj) => obj.Ema20.GetHashCode();
    }
}
