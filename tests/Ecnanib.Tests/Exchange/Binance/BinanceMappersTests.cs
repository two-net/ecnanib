using Binance.Net.Enums;
using Binance.Net.Objects.Models;
using Binance.Net.Objects.Models.Futures;
using Binance.Net.Objects.Models.Spot;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Binance;
using OrderType = Ecnanib.Exchange.Abstractions.OrderType;
using OrderSide = Ecnanib.Exchange.Abstractions.OrderSide;
using BinanceOrderSide = Binance.Net.Enums.OrderSide;
using OrderStatus = Ecnanib.Exchange.Abstractions.OrderStatus;
using TimeInForce = Ecnanib.Exchange.Abstractions.TimeInForce;
using MarginType = Ecnanib.Exchange.Abstractions.MarginType;
using BinanceOrderStatus = Binance.Net.Enums.OrderStatus;
using BinanceTimeInForce = Binance.Net.Enums.TimeInForce;

namespace Ecnanib.Tests.Exchange.Binance;

public class BinanceMappersTests
{
    private static readonly DateTime Utc = new(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SpotSymbolMapsFiltersAndPrecision()
    {
        var symbol = new BinanceSymbol
        {
            Name = "BTCUSDT",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            Status = SymbolStatus.Trading,
            BaseAssetPrecision = 8,
            QuoteAssetPrecision = 8,
            OCOAllowed = true,
            Filters =
            [
                new BinanceSymbolPriceFilter { TickSize = 0.01000000m, MinPrice = 0.01000000m, MaxPrice = 1000000m },
                new BinanceSymbolLotSizeFilter { StepSize = 0.00001000m, MinQuantity = 0.00001000m, MaxQuantity = 9000m },
                new BinanceSymbolMarketLotSizeFilter { StepSize = 0m, MinQuantity = 0m, MaxQuantity = 100m },
                new BinanceSymbolNotionalFilter { MinNotional = 5m, MaxNotional = 9000000m, ApplyMinToMarketOrders = true },
            ],
        };

        var info = BinanceMappers.ToSymbolInfo(symbol);

        Assert.Equal("BTCUSDT", info.Symbol);
        Assert.True(info.IsTrading);
        Assert.True(info.OcoAllowed);
        Assert.Equal(2, info.PricePrecision);
        Assert.Equal(8, info.QuantityPrecision);
        Assert.Equal(0.01m, info.Filters.TickSize);
        Assert.Equal("0.01", info.Filters.TickSize.ToString());
        Assert.Equal(0.00001m, info.Filters.StepSize);
        Assert.Equal(5m, info.Filters.MinNotional);
        Assert.Equal(9000000m, info.Filters.MaxNotional);
        Assert.Equal(100m, info.Filters.MarketMaxQuantity);
        Assert.True(info.Filters.MinNotionalAppliesToMarketOrders);
    }

    [Fact]
    public void FuturesSymbolUsesExchangePrecision()
    {
        var symbol = new BinanceFuturesUsdtSymbol
        {
            Name = "ETHUSDT",
            BaseAsset = "ETH",
            QuoteAsset = "USDT",
            Status = SymbolStatus.Trading,
            PricePrecision = 2,
            QuantityPrecision = 3,
            Filters =
            [
                new BinanceSymbolPriceFilter { TickSize = 0.01m, MinPrice = 39.86m, MaxPrice = 306177m },
                new BinanceSymbolLotSizeFilter { StepSize = 0.001m, MinQuantity = 0.001m, MaxQuantity = 10000m },
                new BinanceSymbolMinNotionalFilter { MinNotional = 20m },
            ],
        };

        var info = BinanceMappers.ToSymbolInfo(symbol);

        Assert.Equal(2, info.PricePrecision);
        Assert.Equal(3, info.QuantityPrecision);
        Assert.False(info.OcoAllowed);
        Assert.Equal(20m, info.Filters.MinNotional);
        Assert.Null(info.Filters.MaxNotional);
    }

    [Fact]
    public void KlineMapsToCandleWithClosedFlagFromClock()
    {
        var kline = new BinanceSpotKline
        {
            OpenTime = Utc,
            CloseTime = Utc.AddMinutes(15).AddMilliseconds(-1),
            OpenPrice = 1m, HighPrice = 2m, LowPrice = 0.5m, ClosePrice = 1.5m,
            Volume = 10m, QuoteVolume = 15m, TradeCount = 7,
        };

        var closed = BinanceMappers.ToCandle(kline, "BTCUSDT", CandleInterval.FifteenMinutes, new DateTimeOffset(Utc.AddMinutes(16)));
        var forming = BinanceMappers.ToCandle(kline, "BTCUSDT", CandleInterval.FifteenMinutes, new DateTimeOffset(Utc.AddMinutes(5)));

        Assert.True(closed.IsClosed);
        Assert.False(forming.IsClosed);
        Assert.Equal(new DateTimeOffset(Utc), closed.OpenTime);
        Assert.Equal(TimeSpan.Zero, closed.OpenTime.Offset);
        Assert.Equal(1.5m, closed.Close);
        Assert.Equal(7, closed.TradeCount);
    }

    [Theory]
    [InlineData(BinanceOrderStatus.PendingNew, OrderStatus.New)]
    [InlineData(BinanceOrderStatus.New, OrderStatus.New)]
    [InlineData(BinanceOrderStatus.PartiallyFilled, OrderStatus.PartiallyFilled)]
    [InlineData(BinanceOrderStatus.ExpiredInMatch, OrderStatus.Expired)]
    [InlineData(BinanceOrderStatus.Adl, OrderStatus.Other)]
    [InlineData(BinanceOrderStatus.Insurance, OrderStatus.Other)]
    public void OrderStatusMapping(BinanceOrderStatus source, OrderStatus expected) =>
        Assert.Equal(expected, BinanceMappers.ToOrderStatus(source));

    [Theory]
    [InlineData(SpotOrderType.StopLoss, OrderType.StopMarket)]
    [InlineData(SpotOrderType.TakeProfit, OrderType.TakeProfitMarket)]
    [InlineData(SpotOrderType.LimitMaker, OrderType.LimitMaker)]
    public void SpotOrderTypeMapping(SpotOrderType source, OrderType expected)
    {
        Assert.Equal(expected, BinanceMappers.ToOrderType(source));
        Assert.Equal(source, BinanceMappers.ToSpotOrderType(expected));
    }

    [Theory]
    [InlineData(FuturesOrderType.Stop, OrderType.StopLossLimit)]
    [InlineData(FuturesOrderType.StopMarket, OrderType.StopMarket)]
    [InlineData(FuturesOrderType.TakeProfitMarket, OrderType.TakeProfitMarket)]
    public void FuturesOrderTypeMapping(FuturesOrderType source, OrderType expected)
    {
        Assert.Equal(expected, BinanceMappers.ToOrderType(source));
        Assert.Equal(source, BinanceMappers.ToFuturesOrderType(expected));
    }

    [Fact]
    public void UnsupportedTypesAreRejectedOrOther()
    {
        Assert.Equal(OrderType.Other, BinanceMappers.ToOrderType(FuturesOrderType.TrailingStopMarket));
        Assert.Throws<ArgumentOutOfRangeException>(() => BinanceMappers.ToSpotOrderType(OrderType.Other));
        Assert.Equal(TimeInForce.Other, BinanceMappers.ToTimeInForce(BinanceTimeInForce.GoodTillCrossing));
        Assert.Throws<ArgumentOutOfRangeException>(() => BinanceMappers.FromTimeInForce(TimeInForce.Other));
    }

    [Fact]
    public void PositionIsSignedAndDefaultsMissingValues()
    {
        var now = new DateTimeOffset(Utc);
        var shortPosition = new BinancePositionV3
        {
            Symbol = "BTCUSDT", PositionAmt = -0.5m, EntryPrice = 100m, MarkPrice = 90m, UnrealizedProfit = 5m,
            LiquidationPrice = 0m, Leverage = 10m, MarginType = FuturesMarginType.Isolated, IsolatedMargin = 5m, Notional = -45m,
        };

        var position = BinanceMappers.ToPosition(shortPosition, now);

        Assert.True(position.IsOpen);
        Assert.Equal(OrderSide.Sell, position.Side);
        Assert.Null(position.LiquidationPrice);
        Assert.Equal(10, position.Leverage);
        Assert.Equal(MarginType.Isolated, position.MarginType);
        Assert.Equal(5m, position.IsolatedMargin);
        Assert.Equal(now, position.UpdatedAt);

        var flat = BinanceMappers.ToPosition(new BinancePositionV3 { Symbol = "BTCUSDT", PositionAmt = 0m }, now);
        Assert.False(flat.IsOpen);
        Assert.Null(flat.Side);
        Assert.Equal(MarginType.Cross, flat.MarginType);
    }

    [Fact]
    public void PermissionsAreVerified()
    {
        var permissions = BinanceMappers.ToPermissions(new BinanceAPIKeyPermissions
        {
            EnableReading = true, EnableSpotAndMarginTrading = true, EnableFutures = false, EnableWithdrawals = true, IpRestrict = false, CreateTime = Utc,
        });

        Assert.True(permissions.Verified);
        Assert.True(permissions.EnableWithdrawals);
        Assert.False(permissions.EnableFutures);
        Assert.Equal(new DateTimeOffset(Utc), permissions.CreatedAt);
    }

    [Fact]
    public void MakerFlagDependsOnOurSide()
    {
        Assert.True(BinanceMappers.IsMaker(BinanceOrderSide.Buy, buyerIsMaker: true));
        Assert.False(BinanceMappers.IsMaker(BinanceOrderSide.Buy, buyerIsMaker: false));
        Assert.False(BinanceMappers.IsMaker(BinanceOrderSide.Sell, buyerIsMaker: true));
        Assert.True(BinanceMappers.IsMaker(BinanceOrderSide.Sell, buyerIsMaker: false));
    }

    [Theory]
    [InlineData("0.00001000", 5)]
    [InlineData("0.01", 2)]
    [InlineData("1", 0)]
    [InlineData("1.0", 0)]
    [InlineData("0.1", 1)]
    public void ScaleCountsDecimals(string value, int expected) => Assert.Equal(expected, BinanceMappers.Scale(decimal.Parse(value)));

    [Fact]
    public void RateLimitRuleMapsIntervalAndKind()
    {
        var rule = BinanceMappers.ToRateLimitRule(new BinanceRateLimit
        {
            Type = RateLimitType.RequestWeight, Interval = RateLimitInterval.Minute, IntervalNumber = 1, Limit = 6000,
        });

        Assert.Equal(RateLimitKind.RequestWeight, rule.Kind);
        Assert.Equal(TimeSpan.FromMinutes(1), rule.Window);
        Assert.Equal(6000, rule.Limit);

        var orders = BinanceMappers.ToRateLimitRule(new BinanceRateLimit
        {
            Type = RateLimitType.Orders, Interval = RateLimitInterval.Second, IntervalNumber = 10, Limit = 100,
        });
        Assert.Equal(RateLimitKind.Orders, orders.Kind);
        Assert.Equal(TimeSpan.FromSeconds(10), orders.Window);
    }

    [Fact]
    public void LocalTimesAreConvertedToUtc()
    {
        var local = new DateTime(2026, 10, 18, 10, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal(TimeSpan.Zero, BinanceMappers.ToUtc(local).Offset);
        Assert.Equal(10, BinanceMappers.ToUtc(local).Hour);
    }
}
