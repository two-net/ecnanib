using System.Text;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.AiCouncil;

/// <summary>
/// Writes a prompt's message: FR-PRM-01's layers 5 and 6. Custom instructions come first, in their own block, then
/// the runtime context. What Ecnanib computed is written as data; what a person or a model wrote is escaped, and
/// what came from outside is labelled as untrusted (FR-PRM-05, FR-PRM-06). The system template explains each tag.
/// </summary>
internal static class ContextRenderer
{
    public static string Render(PromptRequest request)
    {
        var message = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(request.CustomInstructions))
        {
            message.Append("<custom_instructions>\n")
                .Append(PromptText.Escape(request.CustomInstructions.Trim()))
                .Append("\n</custom_instructions>\n\n");
        }

        var context = request.Context;
        var sections = new List<string>();

        if (context.Snapshot is { } snapshot)
        {
            sections.Add(MarketData(snapshot, PromptWindows.For(request.Role)));
        }

        if (context.Account is { } account)
        {
            sections.Add(Account(account, request.Market));
            sections.Add(Position(context.Position, request.Market));
            sections.Add(PendingEntry(context.PendingEntry));
        }
        else
        {
            if (context.Position is { } position)
            {
                sections.Add(Position(position, request.Market));
            }

            if (context.PendingEntry is { } pending)
            {
                sections.Add(PendingEntry(pending));
            }
        }

        if (context.Record is { } record)
        {
            sections.Add(Record(record));
        }

        if (context.Risk is { } risk)
        {
            sections.Add(Risk(risk, request.Market, request.Timeframe));
        }

        sections.AddRange(context.Reflections.Select(Reflection));
        sections.AddRange(context.External.Select(External));

        message.Append("<runtime_context>\n");
        message.AppendJoin("\n\n", sections);
        if (sections.Count > 0)
        {
            message.Append('\n');
        }

        message.Append("</runtime_context>");

        return message.ToString();
    }

    private static string MarketData(MarketSnapshot snapshot, (int Candles, int HigherTimeframe) window)
    {
        var timeframe = PromptText.TimeframeName(snapshot.Timeframe);
        var higher = PromptText.IntervalName(snapshot.HigherTimeframe.Interval);
        var close = snapshot.Candles.LastOpenTime + snapshot.Candles.Interval;
        var quote = snapshot.Quote;

        var block = new StringBuilder()
            .Append($"<market_data symbol=\"{PromptText.Attribute(snapshot.Symbol)}\" market=\"{snapshot.Market}\" ")
            .Append($"timeframe=\"{timeframe}\" higher_timeframe=\"{higher}\" candle_close=\"{PromptText.Time(close)}\">\n")
            .Append($"quote_at: {PromptText.Time(quote.At)}\n")
            .Append($"last_price: {PromptText.Number(quote.LastPrice)}\n")
            .Append($"bid: {PromptText.Number(quote.BidPrice)} (quantity {PromptText.Number(quote.BidQuantity)})\n")
            .Append($"ask: {PromptText.Number(quote.AskPrice)} (quantity {PromptText.Number(quote.AskQuantity)})\n");

        if (snapshot.Futures is { } futures)
        {
            block.Append($"mark_price: {PromptText.Number(futures.MarkPrice)}\n")
                .Append($"index_price: {PromptText.Number(futures.IndexPrice)}\n")
                .Append($"funding_rate: {(futures.FundingRate is { } rate ? PromptText.Percent(rate) + " (last)" : "none")}\n")
                .Append($"next_funding: {(futures.NextFundingTime is { } next ? PromptText.Time(next) : "none")}\n");
        }

        block.Append('\n');
        Indicators(block, timeframe, snapshot.Candles.Indicators);
        block.Append('\n');
        Indicators(block, higher, snapshot.HigherTimeframe.Indicators);
        Candles(block, timeframe, snapshot.Candles, window.Candles);
        Candles(block, higher, snapshot.HigherTimeframe, window.HigherTimeframe);

        return block.Append("</market_data>").ToString();
    }

    private static void Indicators(StringBuilder block, string timeframe, IndicatorSet set)
    {
        block.Append($"indicators_{timeframe}: ema20={PromptText.Computed(set.Ema20)}, ema50={PromptText.Computed(set.Ema50)}, ")
            .Append($"ema200={PromptText.Computed(set.Ema200)}, rsi14={PromptText.Computed(set.Rsi14)}, atr14={PromptText.Computed(set.Atr14)}\n");

        if (set.Volume is { } volume)
        {
            block.Append($"volume_{timeframe}: latest={PromptText.Computed(volume.Latest)}, ")
                .Append($"average{PromptText.Number(Ecnanib.Modules.MarketData.Contracts.Indicators.VolumePeriod)}={PromptText.Computed(volume.Average)}, ")
                .Append($"relative={PromptText.Computed(volume.Relative)}\n");
        }

        block.Append($"swing_highs_{timeframe} (newest first): {Swings(set.SwingHighs)}\n")
            .Append($"swing_lows_{timeframe} (newest first): {Swings(set.SwingLows)}\n");
    }

    private static string Swings(IReadOnlyList<SwingPoint> swings) =>
        swings.Count == 0
            ? "none"
            : string.Join("; ", swings.Select(swing => $"{PromptText.Number(swing.Price)} at {PromptText.Time(swing.OpenTime)}"));

    private static void Candles(StringBuilder block, string timeframe, CandleSeries series, int window)
    {
        if (window <= 0 || series.Candles.Count == 0)
        {
            return;
        }

        var candles = series.Candles.Skip(Math.Max(0, series.Candles.Count - window)).ToList();

        block.Append($"\ncandles_{timeframe} (last {PromptText.Number(candles.Count)}, oldest first):\n")
            .Append("open_time,open,high,low,close,volume\n");

        foreach (var candle in candles)
        {
            block.Append(PromptText.Time(candle.OpenTime)).Append(',')
                .Append(PromptText.Number(candle.Open)).Append(',')
                .Append(PromptText.Number(candle.High)).Append(',')
                .Append(PromptText.Number(candle.Low)).Append(',')
                .Append(PromptText.Number(candle.Close)).Append(',')
                .Append(PromptText.Number(candle.Volume)).Append('\n');
        }
    }

    private static string Account(PromptAccount account, Market market)
    {
        var block = new StringBuilder()
            .Append($"<account base_asset=\"{PromptText.Attribute(account.BaseAsset)}\" quote_asset=\"{PromptText.Attribute(account.QuoteAsset)}\">\n")
            .Append($"capital_allocation: {PromptText.Number(account.CapitalAllocation)}\n")
            .Append($"available_{(market == Market.Futures ? "margin" : "quote")}: {PromptText.Number(account.AvailableQuote)}\n");

        if (account.BaseHeld is { } held)
        {
            block.Append($"base_held_by_agent: {PromptText.Number(held)}\n");
        }

        if (account.Leverage is { } leverage)
        {
            block.Append($"leverage: {PromptText.Number(leverage)}x, isolated margin\n");
        }

        return block.Append("</account>").ToString();
    }

    private static string Position(PromptPosition? position, Market market)
    {
        if (position is null)
        {
            return "<position>\nnone\n</position>";
        }

        var block = new StringBuilder()
            .Append($"<position side=\"{position.Side}\">\n")
            .Append($"quantity: {PromptText.Number(position.Quantity)}\n")
            .Append($"average_entry: {PromptText.Number(position.AverageEntry)}\n")
            .Append($"opened_at: {PromptText.Time(position.OpenedAt)}\n")
            .Append($"stop: {PromptText.Number(position.Stop)}\n")
            .Append($"targets: {(position.Targets.Count == 0 ? "none" : string.Join(", ", position.Targets.Select(PromptText.Number)))}\n")
            .Append($"unrealized_pnl: {PromptText.Number(position.UnrealizedPnl)}\n");

        if (market == Market.Futures)
        {
            block.Append($"liquidation_price: {PromptText.Number(position.LiquidationPrice)}\n");
        }

        return block.Append("</position>").ToString();
    }

    private static string PendingEntry(PromptPendingEntry? pending) =>
        pending is null
            ? "<pending_entry>\nnone\n</pending_entry>"
            : $"<pending_entry side=\"{pending.Side}\" type=\"{pending.Type}\">\n"
                + $"price: {(pending.Price is { } price ? PromptText.Number(price) : "market")}\n"
                + $"quantity: {PromptText.Number(pending.Quantity)}\n"
                + $"placed_at: {PromptText.Time(pending.PlacedAt)}\n"
                + "</pending_entry>";

    private static string Record(TradingRecord record) =>
        "<trading_record>\n"
        + $"realized_pnl_today: {PromptText.Number(record.RealizedToday)}\n"
        + $"realized_pnl_this_week: {PromptText.Number(record.RealizedThisWeek)}\n"
        + $"consecutive_losses: {PromptText.Number(record.ConsecutiveLosses)}\n"
        + $"entries_today: {PromptText.Number(record.EntriesToday)}\n"
        + $"cycles_since_last_loss: {(record.CyclesSinceLastLoss is { } cycles ? PromptText.Number(cycles) : "none")}\n"
        + "</trading_record>";

    private static string Risk(RiskSettings risk, Market market, Timeframe timeframe)
    {
        var block = new StringBuilder()
            .Append("<risk_settings>\n")
            .Append($"max_risk_per_trade: {PromptText.Number(risk.MaxRiskPerTradePercent)}% of the capital allocation\n")
            .Append($"max_position_notional: {(risk.MaxPositionNotional is { } notional ? PromptText.Number(notional) : "the capital allocation")}\n");

        if (market == Market.Futures)
        {
            block.Append($"max_leverage: {PromptText.Number(risk.MaxLeverage)}x\n")
                .Append("margin_mode: isolated\n");
        }

        block.Append($"daily_loss_limit: {PromptText.Number(risk.DailyLossLimitPercent)}% of the capital allocation\n")
            .Append($"weekly_loss_limit: {PromptText.Number(risk.WeeklyLossLimitPercent)}% of the capital allocation\n")
            .Append($"consecutive_losses_before_pause: {(risk.ConsecutiveLossesBeforePause == 0 ? "off" : PromptText.Number(risk.ConsecutiveLossesBeforePause))}\n")
            .Append($"min_reward_to_risk: {PromptText.Number(risk.MinRewardToRisk)}\n")
            .Append($"max_new_entries_per_day: {PromptText.Number(risk.MaxNewEntriesPerDay)}\n")
            .Append($"stop_distance: {PromptText.Number(risk.MinStopDistanceAtr)} to {PromptText.Number(risk.MaxStopDistanceAtr)} x ATR(14) of the {PromptText.TimeframeName(timeframe)} candles\n");

        if (market == Market.Futures)
        {
            block.Append($"liquidation_buffer: liquidation at least {PromptText.Number(risk.LiquidationBufferMultiple)} x the entry-to-stop distance from the entry\n")
                .Append($"funding_filter: no entry when funding against the position is above {PromptText.Number(risk.FundingFilterPercent)}% per 8 hours\n");
        }

        return block.Append($"cooldown_cycles_after_loss: {PromptText.Number(risk.CooldownCyclesAfterLoss)}\n")
            .Append("protective_stop: always required\n")
            .Append("</risk_settings>")
            .ToString();
    }

    private static string Reflection(PromptReflection reflection)
    {
        var attributes = new StringBuilder()
            .Append($"kind=\"reflection\" source=\"{AiRole.Reviewer}\" trade_closed=\"{PromptText.Time(reflection.TradeClosedAt)}\"")
            .Append($" r_multiple=\"{PromptText.Number(reflection.RMultiple)}\"");

        if (reflection.Tags.Count > 0)
        {
            attributes.Append($" tags=\"{PromptText.Attribute(string.Join(", ", reflection.Tags))}\"");
        }

        return Untrusted(attributes.ToString(), reflection.Lesson);
    }

    private static string External(UntrustedContent content)
    {
        var attributes = new StringBuilder()
            .Append($"kind=\"{PromptText.Snake(content.Kind)}\" source=\"{PromptText.Attribute(content.Source)}\"");

        if (!string.IsNullOrWhiteSpace(content.Title))
        {
            attributes.Append($" title=\"{PromptText.Attribute(content.Title)}\"");
        }

        if (!string.IsNullOrWhiteSpace(content.Url))
        {
            attributes.Append($" url=\"{PromptText.Attribute(content.Url)}\"");
        }

        if (content.PublishedAt is { } published)
        {
            attributes.Append($" published=\"{PromptText.Time(published)}\"");
        }

        return Untrusted(attributes.ToString(), content.Text);
    }

    private static string Untrusted(string attributes, string text) =>
        $"<untrusted_content {attributes}>\n{PromptText.Body(text, PromptLimits.UntrustedItemMaxLength)}\n</untrusted_content>";
}
