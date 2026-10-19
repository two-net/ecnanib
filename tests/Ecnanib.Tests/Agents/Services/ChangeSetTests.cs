using System.Text.Json;
using Ecnanib.Agents.Services;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Tests.Agents.Services;

/// <summary>One change-history row per changed field, nothing for unchanged ones (FR-AGT-07).</summary>
public class ChangeSetTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecordsOnlyWhatChangedAsJson()
    {
        var agentId = Guid.NewGuid();
        var changes = new ChangeSet(agentId, At);

        Assert.False(changes.Track("name", "BTC", "BTC"));
        Assert.True(changes.Track("name", "BTC", "ETH"));
        Assert.True(changes.Track("marketType", MarketType.Spot, MarketType.UsdFutures));
        Assert.False(changes.Track("leverage", (int?)null, (int?)null));
        Assert.True(changes.Track("leverage", (int?)null, 3));
        Assert.True(changes.Track("riskLimits.riskPerTradePercent", 1m, 1.5m));
        Assert.False(changes.Track("riskLimits.riskPerTradePercent", 1.0m, 1m));

        Assert.Equal(4, changes.Count);
        var name = changes.Entries[0];
        Assert.Equal(agentId, name.AgentId);
        Assert.Equal(At, name.Timestamp);
        Assert.Equal("\"BTC\"", name.OldValue!.Value.GetRawText());
        Assert.Equal("\"ETH\"", name.NewValue!.Value.GetRawText());
        Assert.Equal("\"UsdFutures\"", changes.Entries[1].NewValue!.Value.GetRawText());
        Assert.Equal("null", changes.Entries[2].OldValue!.Value.GetRawText());
        Assert.Equal("3", changes.Entries[2].NewValue!.Value.GetRawText());
    }

    [Fact]
    public void JsonValuesCompareByTextAndUndefinedReadsAsNull()
    {
        var changes = new ChangeSet(Guid.NewGuid(), At);
        var a = JsonSerializer.SerializeToElement(14);
        var b = JsonSerializer.SerializeToElement(21);

        Assert.False(changes.TrackJson("parameters.atrPeriod", a, JsonSerializer.SerializeToElement(14)));
        Assert.True(changes.TrackJson("parameters.atrPeriod", a, b));
        Assert.True(changes.TrackJson("parameters.emaFastPeriod", null, b));
        Assert.False(changes.TrackJson("parameters.emaSlowPeriod", null, default(JsonElement)));

        Assert.Equal(2, changes.Count);
        Assert.Null(changes.Entries[1].OldValue);
        Assert.Equal("21", changes.Entries[1].NewValue!.Value.GetRawText());
    }

    [Fact]
    public void StateTransitionsAndCreationAreRecordedToo()
    {
        var state = ChangeSet.State(Guid.NewGuid(), At, AgentState.Draft, AgentState.Running, null);
        Assert.Equal(ChangeSet.StateField, state.Field);
        Assert.Equal("\"Draft\"", state.OldValue!.Value.GetRawText());
        Assert.Equal("""{"state":"Running","reason":null}""", state.NewValue!.Value.GetRawText());

        var changes = new ChangeSet(Guid.NewGuid(), At);
        changes.Note(ChangeSet.CreatedField, new { mode = AgentMode.Paper });
        Assert.Null(changes.Entries[0].OldValue);
        Assert.Equal("""{"mode":"Paper"}""", changes.Entries[0].NewValue!.Value.GetRawText());
    }
}
