using System.Net;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Notifications;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Exchange;

/// <summary>EXT-BIN-04, FR-NTF-01: Execution tells administrators about a ban the adapter found.</summary>
public sealed class BanNotificationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Administrators_are_notified_of_a_ban_once()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var administrator = Visitor.NewEmail();
        var connectionString = postgres.NewConnectionString();
        await using var exchange = new ExchangeHarness(
            postgres,
            AccountApp.Settings(new()
            {
                ["Admin:AdminEmails:0"] = administrator,
                ["Admin:BootstrapSecret"] = new string('s', 40),
            }),
            connectionString: connectionString);
        var administratorId = await TestUsers.AddAsync(connectionString, administrator, verified: true, cancellation);
        var userId = await TestUsers.AddAsync(connectionString, Visitor.NewEmail(), verified: true, cancellation);
        exchange.Binance.On(
            HttpMethod.Get,
            "/fapi/v3/account",
            _ => FakeBinance.Error((HttpStatusCode)418, -1003, $"Way too many requests; IP banned until {exchange.Binance.ServerTime() + 300_000}."));
        var key = ExchangeHarness.HmacKey(TradingEnvironment.Live);

        await Assert.ThrowsAsync<ExchangeException>(() => exchange.Account.GetAccountAsync(Market.Futures, key, cancellation));
        await Assert.ThrowsAsync<ExchangeException>(() => exchange.Account.GetAccountAsync(Market.Futures, key, cancellation));
        await LoggedApp.UntilAsync(async () => (await NotificationRows.OfAsync(connectionString, administratorId, cancellation)).Count > 0);

        var row = Assert.Single(await NotificationRows.OfAsync(connectionString, administratorId, cancellation));
        Assert.Equal(("ExchangeRateLimitBan", "/admin/health"), (row.Type, row.Link));
        Assert.Contains("Binance USDⓈ-M Futures Live", row.Payload, StringComparison.Ordinal);
        Assert.Empty(await NotificationRows.OfAsync(connectionString, userId, cancellation));
    }
}
