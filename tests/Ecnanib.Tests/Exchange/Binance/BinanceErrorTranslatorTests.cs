using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Errors;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Binance;

namespace Ecnanib.Tests.Exchange.Binance;

public class BinanceErrorTranslatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AStopSentToThePlainOrderEndpointIsTheConditionalOrderRequiredKind()
    {
        var error = new ServerError(-4120, new ErrorInfo(ErrorType.Unknown, "Order type not supported for this endpoint. Please use the Algo Order API endpoints instead."), null);

        var ex = Assert.IsType<ExchangeRequestException>(BinanceErrorTranslator.Translate(error, "place order", MarketType.UsdFutures, Now));

        Assert.Equal(ExchangeErrorKind.ConditionalOrderRequired, ex.Kind);
        Assert.Equal(-4120, ex.ExchangeCode);
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public void ServerRateLimitBecomesRateLimitedWithRetryAfter()
    {
        var error = new ServerRateLimitError("Too many requests", null)
        {
            RetryAfter = Now.AddSeconds(7).UtcDateTime,
        };

        var ex = Assert.IsType<ExchangeRateLimitedException>(BinanceErrorTranslator.Translate(error, "klines", MarketType.Spot, Now));

        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
        Assert.Equal(Now.AddSeconds(7), ex.RetryAt);
        Assert.False(ex.IsIpBan);
        Assert.True(ex.IsTransient);
        Assert.Equal(ExchangeErrorKind.RateLimited, ex.Kind);
    }

    [Fact]
    public void BannedMessageIsParsedIntoRetryAt()
    {
        var until = Now.AddMinutes(3);
        var error = new ServerError(-1003, new ErrorInfo(ErrorType.Unknown, $"Too many requests; IP banned until {until.ToUnixTimeMilliseconds()}."), null);

        var ex = Assert.IsType<ExchangeRateLimitedException>(BinanceErrorTranslator.Translate(error, "klines", MarketType.UsdFutures, Now));

        Assert.True(ex.IsIpBan);
        Assert.Equal(until, ex.RetryAt);
        Assert.Equal(TimeSpan.FromMinutes(3), ex.RetryAfter);
        Assert.Equal(-1003, ex.ExchangeCode);
    }

    [Fact]
    public void HttpStatusAloneClassifiesRateLimitAndAuth()
    {
        var limited = HttpResult.Fail<int>("Binance", new ServerError(429, new ErrorInfo(ErrorType.Unknown, "limit"), null));
        Assert.IsType<ExchangeRateLimitedException>(Record.Exception(() => BinanceErrorTranslator.EnsureSuccess(limited, "op", MarketType.Spot, Now)));

        var unauthorized = HttpResult.Fail<int>("Binance", new ServerError(-2015, new ErrorInfo(ErrorType.Unauthorized, "Invalid API-key, IP, or permissions for action."), null));
        var auth = Assert.IsType<ExchangeAuthException>(Record.Exception(() => BinanceErrorTranslator.EnsureSuccess(unauthorized, "ping", MarketType.Spot, Now)));
        Assert.Equal(-2015, auth.ExchangeCode);
        Assert.False(auth.IsTransient);
    }

    [Fact]
    public void UnknownCodeWithAuthMeaningIsAuth()
    {
        var error = new ServerError(-1022, new ErrorInfo(ErrorType.Unknown, "Signature for this request is not valid."), null);
        Assert.IsType<ExchangeAuthException>(BinanceErrorTranslator.Translate(error, "ping", MarketType.Spot, Now));
    }

    [Theory]
    [InlineData(ErrorType.Timeout, ExchangeErrorKind.Timeout)]
    [InlineData(ErrorType.NetworkError, ExchangeErrorKind.Network)]
    [InlineData(ErrorType.SystemError, ExchangeErrorKind.SystemError)]
    public void TransientFailuresAreUnavailable(ErrorType type, ExchangeErrorKind kind)
    {
        var error = new ServerError(type, "boom", null);

        var ex = Assert.IsType<ExchangeUnavailableException>(BinanceErrorTranslator.Translate(error, "klines", MarketType.Spot, Now));

        Assert.Equal(kind, ex.Kind);
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public void ServerSideFiveHundredIsUnavailable()
    {
        var error = new ServerError(503, new ErrorInfo(ErrorType.Unknown, "Service Unavailable"), null);
        var ex = Assert.IsType<ExchangeUnavailableException>(BinanceErrorTranslator.Translate(error, "klines", MarketType.Spot, Now));
        Assert.Equal(ExchangeErrorKind.SystemError, ex.Kind);
    }

    [Theory]
    [InlineData(ErrorType.UnknownOrder, -2013, ExchangeErrorKind.UnknownOrder)]
    [InlineData(ErrorType.InsufficientBalance, -2019, ExchangeErrorKind.InsufficientBalance)]
    [InlineData(ErrorType.InvalidStopParameters, -2021, ExchangeErrorKind.InvalidStop)]
    [InlineData(ErrorType.NoPosition, -2022, ExchangeErrorKind.NoPosition)]
    [InlineData(ErrorType.DuplicateClientOrderId, -4116, ExchangeErrorKind.DuplicateClientOrderId)]
    [InlineData(ErrorType.Unknown, -2011, ExchangeErrorKind.UnknownOrder)]
    [InlineData(ErrorType.Unknown, -4061, ExchangeErrorKind.IncorrectState)]
    [InlineData(ErrorType.Unknown, -9999, ExchangeErrorKind.Unknown)]
    public void RequestErrorsCarryKindAndCode(ErrorType type, int code, ExchangeErrorKind kind)
    {
        var error = new ServerError(code, new ErrorInfo(type, "rejected"), null);

        var ex = Assert.IsType<ExchangeRequestException>(BinanceErrorTranslator.Translate(error, "place order", MarketType.UsdFutures, Now));

        Assert.Equal(kind, ex.Kind);
        Assert.Equal(code, ex.ExchangeCode);
        Assert.False(ex.IsTransient);
    }

    /// <summary>NFR-REL-02: a duplicate client id is recognised through the library's classification and, should that change, through Binance's own code and text.</summary>
    [Theory]
    [InlineData(ErrorType.DuplicateClientOrderId, -4116, "ClientOrderId is duplicated.", MarketType.UsdFutures, ExchangeErrorKind.DuplicateClientOrderId)]
    [InlineData(ErrorType.Unknown, -4116, "ClientOrderId is duplicated.", MarketType.UsdFutures, ExchangeErrorKind.DuplicateClientOrderId)]
    [InlineData(ErrorType.Unknown, -2010, "Duplicate order sent.", MarketType.Spot, ExchangeErrorKind.DuplicateClientOrderId)]
    [InlineData(ErrorType.Unknown, -2010, "Account has insufficient balance for requested action.", MarketType.Spot, ExchangeErrorKind.RejectedOrder)]
    public void DuplicateClientIdsAreRecognisedByTypeOrByCodeAndMessage(ErrorType type, int code, string message, MarketType market, ExchangeErrorKind kind)
    {
        var error = new ServerError(code, new ErrorInfo(type, message), null);

        var ex = Assert.IsType<ExchangeRequestException>(BinanceErrorTranslator.Translate(error, "place order", market, Now));

        Assert.Equal(kind, ex.Kind);
        Assert.Equal(code, ex.ExchangeCode);
    }

    [Fact]
    public void MessageNamesMarketOperationAndExchangeTextOnly()
    {
        var error = new ServerError(-2010, new ErrorInfo(ErrorType.RejectedOrderConfiguration, "Account has insufficient balance for requested action."), null);

        var ex = BinanceErrorTranslator.Translate(error, "place order", MarketType.Spot, Now);

        Assert.Equal("Binance Spot place order failed: Account has insufficient balance for requested action. (code -2010)", ex.Message);
        Assert.DoesNotContain("http", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signature=", ex.Message);
    }

    [Fact]
    public void NullErrorAndSuccessfulResults()
    {
        Assert.IsType<ExchangeRequestException>(BinanceErrorTranslator.Translate(null, "op", MarketType.Spot, Now));
        Assert.Equal(5, BinanceErrorTranslator.EnsureSuccess(new HttpResult<int>("Binance", 5, null), "op", MarketType.Spot, Now));
    }

    [Theory]
    [InlineData(-4046, true)]
    [InlineData(-4059, true)]
    [InlineData(-4061, false)]
    public void RecognisesNoChangeNeeded(int code, bool expected) =>
        Assert.Equal(expected, BinanceErrorTranslator.IsNoChangeNeeded(new ServerError(code, new ErrorInfo(ErrorType.Unknown, "x"), null)));
}
