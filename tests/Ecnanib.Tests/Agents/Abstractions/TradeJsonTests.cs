using System.Text.Json;
using Ecnanib.Agents.Abstractions;

namespace Ecnanib.Tests.Agents.Abstractions;

public class TradeJsonTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StopHistoryRoundTripsThroughTheWriterAndTheReader()
    {
        var history = TradeJson.AppendStop(TradeJson.StopHistory(At, 95m), At.AddHours(1), 100m, "breakeven");
        history = TradeJson.AppendStop(history, At.AddHours(2), 103.5m, "trail");

        var entries = TradeJson.StopHistoryEntries(history);

        Assert.Equal(
        [
            new StopHistoryEntry(At, 95m, "initial"),
            new StopHistoryEntry(At.AddHours(1), 100m, "breakeven"),
            new StopHistoryEntry(At.AddHours(2), 103.5m, "trail"),
        ], entries);
        Assert.Equal(95m, TradeJson.InitialStop(history));
    }

    [Fact]
    public void MalformedEntriesAreSkippedAndANonArrayIsEmpty()
    {
        var history = JsonDocument.Parse("""[{"at":"2026-01-05T10:00:00Z","price":95,"reason":"initial"},{"price":"x"},{"at":"not a date","price":1},7,{"at":"2026-01-05T11:00:00Z","price":99}]""").RootElement;

        var entries = TradeJson.StopHistoryEntries(history);

        Assert.Equal([new StopHistoryEntry(At, 95m, "initial"), new StopHistoryEntry(At.AddHours(1), 99m, "")], entries);
        Assert.Empty(TradeJson.StopHistoryEntries(JsonDocument.Parse("{}").RootElement));
        Assert.Empty(TradeJson.StopHistoryEntries(JsonDocument.Parse("null").RootElement));
    }

    [Fact]
    public void TargetsReadNumbersOnly()
    {
        Assert.Equal([108m, 112m], TradeJson.Targets(JsonDocument.Parse("[108, 112, \"x\"]").RootElement));
        Assert.Empty(TradeJson.Targets(null));
        Assert.Empty(TradeJson.Targets(JsonDocument.Parse("{}").RootElement));
    }
}
