using System.Text.RegularExpressions;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.MarketData.Contracts;

namespace Ecnanib.Tests.Prompts;

/// <summary>Templates, requests and market data for composing prompts without a database.</summary>
internal static class PromptHarness
{
    public static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// One generic template per layer's own dimension, as the seeds have, each saying which it is, such as
    /// "MARKET Futures".
    /// </summary>
    public static List<PromptTemplate> Generic()
    {
        var templates = new List<PromptTemplate> { Template(new PromptTemplateKey(PromptLayer.System), "SYSTEM") };

        foreach (var role in Enum.GetValues<AiRole>())
        {
            templates.Add(Template(new PromptTemplateKey(PromptLayer.OutputContract, Role: role), $"CONTRACT {role}"));
            templates.Add(Template(new PromptTemplateKey(PromptLayer.Role, Role: role), $"ROLE {role}"));
        }

        templates.AddRange(Enum.GetValues<Market>()
            .Select(market => Template(new PromptTemplateKey(PromptLayer.Market, Market: market), $"MARKET {market}")));
        templates.AddRange(Enum.GetValues<Timeframe>()
            .Select(timeframe => Template(new PromptTemplateKey(PromptLayer.Timeframe, Timeframe: timeframe), $"TIMEFRAME {timeframe}")));

        return templates;
    }

    public static PromptTemplate Template(PromptTemplateKey key, string content, int version = 1) =>
        new(Guid.NewGuid(), key, version, content);

    public static PromptRequest Request(
        AiRole role = AiRole.Strategist,
        Market market = Market.Futures,
        Timeframe timeframe = Timeframe.FifteenMinutes,
        PromptContext? context = null) =>
        new(role, market, timeframe, context ?? PromptContext.Empty);

    public static ComposedPrompt Compose(PromptRequest request) => PromptComposer.Compose(Generic(), request);

    /// <summary>
    /// A snapshot with <paramref name="count"/> candles in each series, a gentle uptrend whose prices carry
    /// Binance's eight decimal places, and its indicators computed as Market Data computes them.
    /// </summary>
    public static MarketSnapshot Snapshot(Market market = Market.Futures, Timeframe timeframe = Timeframe.FifteenMinutes, int count = 200)
    {
        var (interval, higher) = timeframe switch
        {
            Timeframe.FifteenMinutes => (TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)),
            Timeframe.OneHour => (TimeSpan.FromHours(1), TimeSpan.FromDays(1)),
            Timeframe.FourHours => (TimeSpan.FromHours(4), TimeSpan.FromDays(1)),
            _ => (TimeSpan.FromDays(1), TimeSpan.FromDays(7)),
        };

        return new MarketSnapshot(
            market,
            TradingEnvironment.Demo,
            "BTCUSDT",
            timeframe,
            Series(interval, count),
            Series(higher, count),
            new Quote(67050.10000000m, 67050.00000000m, 1.234m, 67050.20000000m, 0.5m, Start.AddDays(30)),
            market == Market.Futures ? new FuturesData(67049.8m, 67045.1m, 0.0001m, Start.AddDays(30).AddHours(6), Start.AddDays(30)) : null);
    }

    public static CandleSeries Series(TimeSpan interval, int count)
    {
        var candles = Enumerable.Range(0, count)
            .Select(i =>
            {
                var open = 60000.12345678m + (i * 10m) + ((i % 5) * 3.5m);
                return new Candle(Start + (interval * i), open, open + 25.5m, open - 20.25m, open + 5.10000000m, 12.34000000m + i, 800000m, 100);
            })
            .ToList();

        return new CandleSeries(interval, candles.Count == 0 ? Start : candles[^1].OpenTime, candles, Indicators.Compute(candles));
    }

    /// <summary>The CSV rows of a candles block, such as candles_15m, or none when there's no such block.</summary>
    public static int CandleRows(string message, string timeframe)
    {
        var match = Regex.Match(message, $@"candles_{timeframe} \(last \d+, oldest first\):\nopen_time,open,high,low,close,volume\n((?:\d{{4}}-[^\n]*\n)*)");

        return match.Success ? match.Groups[1].Value.Count(c => c == '\n') : 0;
    }

    public static int Occurrences(string text, string value) => Regex.Matches(text, Regex.Escape(value)).Count;

    /// <summary>An enum name as the SRS writes FR-CYC-03's actions, such as OPEN_LONG for OpenLong.</summary>
    public static string Screaming(string pascal) =>
        string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : char.ToUpperInvariant(c).ToString()));
}
