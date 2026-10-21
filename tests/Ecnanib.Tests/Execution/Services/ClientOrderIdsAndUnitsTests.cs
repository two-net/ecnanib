using Ecnanib.Agents.Abstractions;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Tests.Execution.Services;

/// <summary>FR-EXE-07 ids and the Appendix A protection units.</summary>
public class ClientOrderIdsAndUnitsTests
{
    [Fact]
    public void ClientOrderIdsAreDeterministicPerPrefixScopePurposeAndSequenceAndValidForBinance()
    {
        var agent = Guid.NewGuid();
        var scope = Guid.NewGuid();

        var live = ClientOrderIds.For(ClientOrderIds.LivePrefix, agent, scope, ClientOrderIds.Entry, 0);
        var again = ClientOrderIds.For(ClientOrderIds.LivePrefix, agent, scope, ClientOrderIds.Entry, 0);
        var paper = ClientOrderIds.For(ClientOrderIds.PaperPrefix, agent, scope, ClientOrderIds.Entry, 0);
        var fallback = ClientOrderIds.For(ClientOrderIds.LivePrefix, agent, scope, ClientOrderIds.Entry, 1);
        var stop = ClientOrderIds.For(ClientOrderIds.LivePrefix, agent, scope, ClientOrderIds.StopLoss, 0);

        Assert.Equal(live, again);
        Assert.Equal(32, live.Length);
        Assert.StartsWith("e", live, StringComparison.Ordinal);
        Assert.StartsWith("p", paper, StringComparison.Ordinal);
        Assert.Equal(live[1..], paper[1..]);
        Assert.NotEqual(live, fallback);
        Assert.NotEqual(live, stop);
        Assert.True(OrderRequest.IsValidClientOrderId(live));
    }

    [Fact]
    public void UnitsSplitThePartialOnTheLotGridAndTheRemainderTakesTheFinalTargetOrTrails()
    {
        var two = ProtectionUnits.Split(1m, [110m, 130m], 0.33m, 0.001m);
        var trailing = ProtectionUnits.Split(1m, [110m], 0.33m, 0.001m);
        var none = ProtectionUnits.Split(1m, [], 0.33m, 0.001m);

        Assert.Equal([new ProtectionUnit(0.33m, 110m), new ProtectionUnit(0.67m, 130m)], two);
        Assert.Equal([new ProtectionUnit(0.33m, 110m), new ProtectionUnit(0.67m, null)], trailing);
        Assert.Equal([new ProtectionUnit(1m, null)], none);
    }

    [Fact]
    public void APositionTooSmallToSplitIsProtectedAsOneUnit()
    {
        Assert.Equal([new ProtectionUnit(0.002m, 130m)], ProtectionUnits.Split(0.002m, [110m, 130m], 0.33m, 0.001m));
        Assert.Equal([new ProtectionUnit(0.001m, 110m)], ProtectionUnits.Split(0.001m, [110m], 0.5m, 0.001m));
        Assert.Equal([new ProtectionUnit(1m, 110m)], ProtectionUnits.Split(1m, [110m, 130m], 1m, 0.001m));
        Assert.Equal([new ProtectionUnit(0.05m, 130m)], ProtectionUnits.Split(0.05m, [110m, 130m], 0.5m, 0.001m, minQuantity: 0.03m));
        Assert.Empty(ProtectionUnits.Split(0m, [110m], 0.5m));
    }

    [Fact]
    public void WithoutAGridThePaperVenueRoundsToEightDecimals()
    {
        Assert.Equal([new ProtectionUnit(0.033m, 110m), new ProtectionUnit(0.067m, null)], ProtectionUnits.Split(0.1m, [110m], 0.33m));
        Assert.Equal(0.12345678m, ProtectionUnits.Floor(0.123456789m, 0m));
    }
}
