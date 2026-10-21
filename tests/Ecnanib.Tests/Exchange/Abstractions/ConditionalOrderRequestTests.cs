using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Tests.Exchange.Abstractions;

public class ConditionalOrderRequestTests
{
    [Fact]
    public void AcceptsAReduceOnlyStopWithAQuantityAndNormalisesTheSymbol()
    {
        var request = new ConditionalOrderRequest("btcusdt", OrderSide.Sell, OrderType.StopMarket, "e-stop-1", 59_000m, 0.5m, ReduceOnly: true, WorkingType: WorkingType.ContractPrice, PriceProtect: true);

        Assert.Equal("BTCUSDT", request.Symbol);
        Assert.Equal(0.5m, request.Quantity);
        Assert.True(request.ReduceOnly);
    }

    [Fact]
    public void AClosePositionOrderCarriesNoQuantityAndIsNotReduceOnly()
    {
        var request = new ConditionalOrderRequest("BTCUSDT", OrderSide.Sell, OrderType.StopMarket, "e-stop-2", 59_000m, null, ClosePosition: true);

        Assert.Null(request.Quantity);
        Assert.True(request.ClosePosition);
        Assert.Throws<ArgumentException>(() => new ConditionalOrderRequest("BTCUSDT", OrderSide.Sell, OrderType.StopMarket, "e-stop-3", 59_000m, 0.5m, ClosePosition: true));
        Assert.Throws<ArgumentException>(() => new ConditionalOrderRequest("BTCUSDT", OrderSide.Sell, OrderType.StopMarket, "e-stop-4", 59_000m, null, ReduceOnly: true, ClosePosition: true));
    }

    [Theory]
    [InlineData(OrderType.Limit)]
    [InlineData(OrderType.Market)]
    [InlineData(OrderType.LimitMaker)]
    public void OnlyTheConditionalTypesAreAccepted(OrderType type)
    {
        Assert.Throws<ArgumentException>(() => new ConditionalOrderRequest("BTCUSDT", OrderSide.Sell, type, "e-x", 1m, 1m));
    }

    [Fact]
    public void TheTriggerAndTheQuantityMustBePositiveAndTheIdValid()
    {
        Assert.Throws<ArgumentException>(() => new ConditionalOrderRequest("BTCUSDT", OrderSide.Sell, OrderType.StopMarket, "e-x", 0m, 1m));
        Assert.Throws<ArgumentException>(() => new ConditionalOrderRequest("BTCUSDT", OrderSide.Sell, OrderType.StopMarket, "e-x", 1m, 0m));
        Assert.Throws<ArgumentException>(() => new ConditionalOrderRequest("BTCUSDT", OrderSide.Sell, OrderType.StopMarket, "has space", 1m, 1m));
    }
}
