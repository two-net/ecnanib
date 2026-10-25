using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Execution.Abstractions;

namespace Ecnanib.Components.Live;

/// <summary>
/// Trade markers for the chart (FR-UI-03 c) from the trade timelines: an arrow where an entry filled, a circle where an
/// exit filled, a square where the stop moved. The library attaches a marker to a bar, so each time is snapped to the
/// open of the candle it happened in, and markers before the oldest bar shown are left out.
/// </summary>
internal static class ChartMarkers
{
    public static IReadOnlyList<ChartMarker> From(IEnumerable<TradeTimelineView> trades, CandleInterval interval, DateTimeOffset? oldestBarOpen)
    {
        ArgumentNullException.ThrowIfNull(trades);
        var markers = new List<ChartMarker>();
        foreach (var trade in trades)
        {
            foreach (var step in trade.Events)
            {
                var marker = Describe(trade, step);
                if (marker is not { } described)
                    continue;
                var time = interval.OpenTimeOf(step.At);
                if (oldestBarOpen is { } oldest && time < oldest)
                    continue;
                markers.Add(new ChartMarker(time.ToUnixTimeSeconds(), described.Position, described.Shape, described.Tone, described.Text, $"{trade.TradeId:N}:{(int)step.Kind}:{step.At.UtcTicks}"));
            }
        }

        return markers.OrderBy(m => m.Time).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
    }

    private static (string Position, string Shape, string Tone, string Text)? Describe(TradeTimelineView trade, TimelineEvent step)
    {
        var price = LiveMessages.Price(step.Price);
        return step.Kind switch
        {
            TimelineKind.EntryFilled when trade.Side == PositionSide.Long => ("belowBar", "arrowUp", "long", $"Long {price}"),
            TimelineKind.EntryFilled => ("aboveBar", "arrowDown", "short", $"Short {price}"),
            TimelineKind.Exit => (trade.Side == PositionSide.Long ? "aboveBar" : "belowBar", "circle", "exit", $"Exit {price}"),
            TimelineKind.PartialExit => (trade.Side == PositionSide.Long ? "aboveBar" : "belowBar", "circle", "exit", $"Partial {price}"),
            TimelineKind.StopMoved => ("inBar", "square", "stop", $"Stop {price}"),
            _ => null,
        };
    }
}
