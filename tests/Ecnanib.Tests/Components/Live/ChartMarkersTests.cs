using Ecnanib.Components.Live;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Execution.Abstractions;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Tests.Components.Live;

public class ChartMarkersTests
{
    private static readonly DateTimeOffset Open = new(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid TradeId = Guid.Parse("00000000-0000-0000-0000-00000000000a");

    [Fact]
    public void EntriesExitsAndStopMovesBecomeMarkersSnappedToTheirBarAndSorted()
    {
        var trade = Trade(PositionSide.Long,
        [
            new TimelineEvent(Open.AddMinutes(-70), TimelineKind.Evaluated),
            new TimelineEvent(Open.AddMinutes(-65), TimelineKind.Decided, 100m, Action: DecisionAction.OpenLong),
            new TimelineEvent(Open.AddMinutes(12), TimelineKind.EntryFilled, 100.5m, 0.1m),
            new TimelineEvent(Open.AddMinutes(13), TimelineKind.Protected, 95m, Targets: [110m]),
            new TimelineEvent(Open.AddHours(2).AddMinutes(1), TimelineKind.StopMoved, 100m, Reason: "breakeven"),
            new TimelineEvent(Open.AddHours(3).AddMinutes(30), TimelineKind.PartialExit, 108m, 0.05m, RealizedPnl: 0.3m),
            new TimelineEvent(Open.AddHours(4), TimelineKind.Exit, 110m, 0.05m, RealizedPnl: 0.4m),
            new TimelineEvent(Open.AddHours(4), TimelineKind.Closed, 110m, ExitReason: TradeExitReason.Target, RealizedPnl: 0.7m),
        ]);

        var markers = ChartMarkers.From([trade], CandleInterval.OneHour, null);

        Assert.Equal(
        [
            (Open.ToUnixTimeSeconds(), "belowBar", "arrowUp", "long", "Long 100.5"),
            (Open.AddHours(2).ToUnixTimeSeconds(), "inBar", "square", "stop", "Stop 100"),
            (Open.AddHours(3).ToUnixTimeSeconds(), "aboveBar", "circle", "exit", "Partial 108"),
            (Open.AddHours(4).ToUnixTimeSeconds(), "aboveBar", "circle", "exit", "Exit 110"),
        ], markers.Select(m => (m.Time, m.Position, m.Shape, m.Tone, m.Text)));
        Assert.Equal(markers.Count, markers.Select(m => m.Id).Distinct().Count());
    }

    [Fact]
    public void AShortEntryPointsDownFromAboveAndItsExitSitsBelow()
    {
        var trade = Trade(PositionSide.Short,
        [
            new TimelineEvent(Open, TimelineKind.EntryFilled, 100m, 1m),
            new TimelineEvent(Open.AddHours(1), TimelineKind.Exit, 95m, 1m, RealizedPnl: 5m),
        ]);

        var markers = ChartMarkers.From([trade], CandleInterval.OneHour, null);

        Assert.Equal([("aboveBar", "arrowDown", "short"), ("belowBar", "circle", "exit")], markers.Select(m => (m.Position, m.Shape, m.Tone)));
    }

    [Fact]
    public void MarkersBeforeTheOldestBarShownAreLeftOut()
    {
        var trade = Trade(PositionSide.Long,
        [
            new TimelineEvent(Open.AddHours(-5), TimelineKind.EntryFilled, 100m, 1m),
            new TimelineEvent(Open.AddMinutes(5), TimelineKind.Exit, 101m, 1m, RealizedPnl: 1m),
        ]);

        var markers = ChartMarkers.From([trade], CandleInterval.OneHour, Open);

        Assert.Equal(["Exit 101"], markers.Select(m => m.Text));
        Assert.Empty(ChartMarkers.From([], CandleInterval.OneHour, null));
    }

    [Fact]
    public void ABarCarriesUnixSecondsAndDoubles()
    {
        var bar = ChartBar.From(new Candle("BTCUSDT", CandleInterval.OneHour, Open, Open.AddHours(1), 100m, 101.5m, 99.25m, 100.75m, 12.5m, 1250m, 10, true));

        Assert.Equal(new ChartBar(Open.ToUnixTimeSeconds(), 100, 101.5, 99.25, 100.75, 12.5), bar);
    }

    private static TradeTimelineView Trade(PositionSide side, IReadOnlyList<TimelineEvent> events) =>
        new(TradeId, Guid.NewGuid(), "BTCUSDT", side, false, true, 100m, 0.1m, null, 0.7m, Open, Open.AddHours(4), TradeExitReason.Target, events);
}
