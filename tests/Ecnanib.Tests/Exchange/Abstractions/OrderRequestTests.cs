using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Tests.Exchange.Abstractions;

public class OrderRequestTests
{
    [Fact]
    public void AcceptsValidRequestAndNormalisesSymbol()
    {
        var request = new OrderRequest("btcusdt", OrderSide.Buy, OrderType.Limit, "agent-1:2026-10-18T10:00:00Z/e", 0.001m, 50000m,
            TimeInForce: TimeInForce.GoodTillCanceled);

        Assert.Equal("BTCUSDT", request.Symbol);
        Assert.Equal("agent-1:2026-10-18T10:00:00Z/e", request.ClientOrderId);
        Assert.False(request.ReduceOnly);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("bang!")]
    [InlineData("a234567890123456789012345678901234567")] // 37 chars
    public void RejectsInvalidClientOrderId(string id)
    {
        Assert.Throws<ArgumentException>(() => new OrderRequest("BTCUSDT", OrderSide.Buy, OrderType.Market, id, 1m));
    }

    [Fact]
    public void AcceptsMaximumLengthClientOrderId()
    {
        var id = new string('x', OrderRequest.MaxClientOrderIdLength);
        var request = new OrderRequest("BTCUSDT", OrderSide.Sell, OrderType.StopMarket, id, 1m, StopPrice: 1m, ReduceOnly: true);
        Assert.Equal(id, request.ClientOrderId);
    }

    [Fact]
    public void OcoRequestValidatesAllThreeIds()
    {
        Assert.Throws<ArgumentException>(() =>
            new SpotOcoRequest("BTCUSDT", OrderSide.Sell, 1m, 110m, 90m, 89m, "list", "tp", "bad id"));

        var ok = new SpotOcoRequest("btcusdt", OrderSide.Sell, 1m, 110m, 90m, 89m, "list", "tp", "sl");
        Assert.Equal("BTCUSDT", ok.Symbol);
    }

    [Fact]
    public void OrderRefFactories()
    {
        Assert.Equal(new OrderRef(5, null), OrderRef.ById(5));
        Assert.Equal(new OrderRef(null, "c1"), OrderRef.ByClientId("c1"));
        Assert.True(default(OrderRef).IsEmpty);
    }

    [Theory]
    [InlineData(OrderStatus.New, true, false)]
    [InlineData(OrderStatus.PartiallyFilled, true, false)]
    [InlineData(OrderStatus.Filled, false, true)]
    [InlineData(OrderStatus.Canceled, false, true)]
    [InlineData(OrderStatus.PendingCancel, false, false)]
    public void OrderStatusHelpers(OrderStatus status, bool open, bool terminal)
    {
        Assert.Equal(open, status.IsOpen());
        Assert.Equal(terminal, status.IsTerminal());
    }

    [Fact]
    public void CandleIntervalHelpers()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), CandleInterval.FifteenMinutes.ToTimeSpan());
        Assert.Equal(TimeSpan.FromDays(7), CandleInterval.OneWeek.ToTimeSpan());
        Assert.Equal("4h", CandleInterval.FourHours.ToLabel());
    }
}
