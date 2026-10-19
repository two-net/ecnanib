using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Configuration;
using Ecnanib.Keys.Services;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Keys.Services;

public class ExchangeKeyValidatorTests
{
    private const string ApiKey = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd";
    private const string Secret = "S3CR3T-VALUE-THAT-MUST-NEVER-LEAK-9876";
    private static readonly ExchangeCredentials Credentials = new(ApiKey, Secret);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PingsOnlyTheAllowedMarketsReadsPermissionsOnceAndReleasesTheScratchId()
    {
        var exchange = new FakeExchangeClientFactory();
        var (validator, logs) = Build(exchange);

        var result = await validator.ValidateAsync(ExchangeEnvironment.Production, Credentials, allowSpot: true, allowFutures: false, Ct);

        Assert.Equal(ValidationVerdict.Valid, result.Verdict);
        Assert.Equal(FakeExchangeClientFactory.TradingOnly, result.Permissions);
        Assert.False(result.WithdrawalEnabled);
        var ping = Assert.Single(exchange.Pings);
        Assert.Equal(MarketType.Spot, ping.Market);
        Assert.StartsWith(ExchangeKeyValidator.ScratchPrefix, ping.Handle.KeyId);
        Assert.Single(exchange.PermissionReads);
        Assert.Equal([ping.Handle.KeyId], exchange.Released);
        Assert.Empty(exchange.CachedKeyIds);
        AssertNoSecrets(logs);
    }

    [Fact]
    public async Task BothMarketsArePingedWhenBothAreAllowed()
    {
        var exchange = new FakeExchangeClientFactory();
        var (validator, _) = Build(exchange);

        var result = await validator.ValidateAsync(ExchangeEnvironment.Production, Credentials, allowSpot: true, allowFutures: true, Ct);

        Assert.Equal(ValidationVerdict.Valid, result.Verdict);
        Assert.Equal([MarketType.Spot, MarketType.UsdFutures], exchange.Pings.Select(p => p.Market));
        Assert.Single(exchange.PermissionReads);
    }

    [Fact]
    public async Task WithdrawalPermissionOnProductionIsRefused()
    {
        var exchange = new FakeExchangeClientFactory { OnPermissions = _ => FakeExchangeClientFactory.WithWithdrawals };
        var (validator, logs) = Build(exchange);

        var result = await validator.ValidateAsync(ExchangeEnvironment.Production, Credentials, allowSpot: false, allowFutures: true, Ct);

        Assert.Equal(ValidationVerdict.Invalid, result.Verdict);
        Assert.True(result.WithdrawalEnabled);
        Assert.Equal(KeyReasons.WithdrawalEnabled, result.Reason);
        Assert.True(result.Permissions!.EnableWithdrawals);
        Assert.Single(exchange.Released);
        AssertNoSecrets(logs);
    }

    [Fact]
    public async Task TestnetKeysAreAcceptedWithUnverifiedPermissions()
    {
        var exchange = new FakeExchangeClientFactory { OnPermissions = _ => FakeExchangeClientFactory.WithWithdrawals };
        var (validator, _) = Build(exchange);

        var result = await validator.ValidateAsync(ExchangeEnvironment.Testnet, Credentials, allowSpot: true, allowFutures: true, Ct);

        Assert.Equal(ValidationVerdict.Valid, result.Verdict);
        Assert.Equal(ApiKeyPermissions.Unverified, result.Permissions);
        Assert.All(exchange.Pings, p => Assert.Equal(ExchangeEnvironment.Testnet, p.Handle.Environment));
    }

    [Fact]
    public async Task AnAuthFailureIsInvalidWithTheExchangesReason()
    {
        var exchange = new FakeExchangeClientFactory { OnPing = (_, _, _) => throw new ExchangeAuthException("Binance spot account info failed: Invalid API-key, IP, or permissions for action. (code -2015)", -2015) };
        var (validator, logs) = Build(exchange);

        var result = await validator.ValidateAsync(ExchangeEnvironment.Production, Credentials, allowSpot: true, allowFutures: true, Ct);

        Assert.Equal(ValidationVerdict.Invalid, result.Verdict);
        Assert.False(result.WithdrawalEnabled);
        Assert.Contains("code -2015", result.Reason);
        Assert.Null(result.Permissions);
        Assert.Single(exchange.Pings);
        Assert.Empty(exchange.PermissionReads);
        Assert.Single(exchange.Released);
        Assert.Empty(exchange.CachedKeyIds);
        AssertNoSecrets(logs);
    }

    public static TheoryData<Exception> NonAuthFailures => new()
    {
        new ExchangeRateLimitedException("Binance spot account info failed: too many requests", TimeSpan.FromSeconds(30), null, false, -1003),
        new ExchangeUnavailableException("Binance spot account info failed: connection refused"),
        new ExchangeRequestException("Binance spot account info failed: timestamp ahead of server time (code -1021)", ExchangeErrorKind.InvalidTimestamp, -1021),
    };

    [Theory]
    [MemberData(nameof(NonAuthFailures))]
    public async Task OtherExchangeFailuresAreFailedNotInvalid(Exception failure)
    {
        var exchange = new FakeExchangeClientFactory { OnPing = (_, _, _) => throw failure };
        var (validator, _) = Build(exchange);

        var result = await validator.ValidateAsync(ExchangeEnvironment.Production, Credentials, allowSpot: true, allowFutures: false, Ct);

        Assert.Equal(ValidationVerdict.Failed, result.Verdict);
        Assert.Equal(failure.Message, result.Reason);
        Assert.Single(exchange.Released);
    }

    [Fact]
    public async Task AHangingPingTimesOutIntoFailed()
    {
        var time = new FakeTimeProvider();
        var exchange = new FakeExchangeClientFactory { OnPing = (_, _, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct) };
        var (validator, _) = Build(exchange, time, new KeysOptions { ValidationTimeoutSeconds = 30 });

        var pending = validator.ValidateAsync(ExchangeEnvironment.Production, Credentials, allowSpot: true, allowFutures: false, Ct);
        await Wait.UntilAsync(() => exchange.Pings.Count == 1, "ping started");
        Assert.False(pending.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(30));
        var result = await pending;

        Assert.Equal(ValidationVerdict.Failed, result.Verdict);
        Assert.Equal("Binance did not answer within 30 seconds.", result.Reason);
        Assert.Single(exchange.Released);
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndStillReleases()
    {
        using var cts = new CancellationTokenSource();
        var exchange = new FakeExchangeClientFactory { OnPing = (_, _, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct) };
        var (validator, _) = Build(exchange);

        var pending = validator.ValidateAsync(ExchangeEnvironment.Production, Credentials, allowSpot: true, allowFutures: false, cts.Token);
        await Wait.UntilAsync(() => exchange.Pings.Count == 1, "ping started");
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(exchange.Released);
    }

    [Fact]
    public async Task NoMarketIsAProgrammingError()
    {
        var (validator, _) = Build(new FakeExchangeClientFactory());

        await Assert.ThrowsAsync<ArgumentException>(() => validator.ValidateAsync(ExchangeEnvironment.Production, Credentials, allowSpot: false, allowFutures: false, Ct));
    }

    private static (ExchangeKeyValidator Validator, TestLoggerProvider Logs) Build(FakeExchangeClientFactory exchange, TimeProvider? time = null, KeysOptions? options = null)
    {
        var logs = new TestLoggerProvider();
        var logger = TestLogging.CreateFactory(logs).CreateLogger<ExchangeKeyValidator>();
        return (new ExchangeKeyValidator(exchange, Options.Create(options ?? new KeysOptions()), time ?? TimeProvider.System, logger), logs);
    }

    private static void AssertNoSecrets(TestLoggerProvider logs)
    {
        Assert.DoesNotContain(ApiKey, logs.AllText);
        Assert.DoesNotContain(Secret, logs.AllText);
    }
}
