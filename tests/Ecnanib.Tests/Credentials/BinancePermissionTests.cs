using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;

namespace Ecnanib.Tests.Credentials;

/// <summary>
/// A Binance key that the exchange reports as able to withdraw is rejected: Ecnanib never takes
/// custody of funds (FR-KEY-4, NFR-LEG-2). Where the exchange reports it, a key with no IP
/// restriction is stored with a warning, and the service's fixed outbound IP addresses are shown
/// so that the user can allow-list them. What the key may trade on is recorded (FR-KEY-3).
/// </summary>
public class BinancePermissionTests
{
    private const string Ada = "ada@example.com";

    private const string KeysPage = KeyBrowser.KeysPage;

    // The address that the configuration file of the tests lists under Binance:OutboundIps.
    private const string OutboundAddress = "203.0.113.7";

    private const string AnyAddress = "Binance reports that this key works from any IP address.";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_Binance_key_that_can_withdraw_is_refused_and_nothing_of_it_is_stored()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        application.Providers.BinanceAnswer = (_, _) => StandInProviders.TakenInLive with { CanWithdraw = true };

        var (path, page) = await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);

        // Binance took the keys, and they are refused all the same. The page says why, and
        // what to do about it.
        Assert.Equal(KeyBrowser.ExchangeKeyPage("live"), path);
        Assert.Contains("role=\"alert\">Binance reports that this API key can withdraw.", page);
        Assert.Contains("Turn off withdrawals for the key at Binance", page);
        Assert.DoesNotContain(Keys.BinanceApi, page);
        Assert.DoesNotContain(Keys.BinanceSecret, page);

        // Nothing of the keys is stored: not encrypted, and not their last characters.
        Assert.Empty(StoredExchangeCredential.All(application.Database));
        Assert.DoesNotContain("Lm3N", application.EverythingStored());
        Assert.DoesNotContain(
            StoredAuditEvent.All(application.Database), recorded => recorded.Action.StartsWith("Key", StringComparison.Ordinal));

        var refused = Assert.Single(application.Events(), written => written.Area == "Ecnanib.Credentials.StoredKeys");
        Assert.Equal(
            "User {UserId} was refused storing a key of Binance for {TradingMode}: Binance reports that it can withdraw", refused.Template);
        Assert.Equal((user.Id.ToString(), "Live"), (refused.Text("UserId"), refused.Text("TradingMode")));
    }

    [Fact]
    public async Task A_key_that_can_withdraw_does_not_take_the_place_of_a_stored_one()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);
        var stored = StoredExchangeCredential.Of(application.Database, user.Id, "Live");

        application.Providers.BinanceAnswer = (_, _) => StandInProviders.TakenInLive with { CanWithdraw = true };
        var (path, _) = await client.StoreExchangeKeyAsync("live", Keys.Of("another", "live"), Keys.Of("another-sealed", "live"));

        Assert.Equal(KeyBrowser.ExchangeKeyPage("live"), path);

        var kept = StoredExchangeCredential.Of(application.Database, user.Id, "Live");
        Assert.Equal(stored.EncryptedValue, kept.EncryptedValue);
        Assert.Equal(stored with { EncryptedValue = kept.EncryptedValue }, kept);
        Assert.Equal(["KeyAdded"], KeyEventsOf(application));
    }

    [Fact]
    public async Task What_a_key_may_trade_on_and_whether_it_is_restricted_to_addresses_is_recorded_and_shown()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        application.Providers.BinanceAnswer = (environment, _) => environment == TradingMode.Live
            ? new ExchangeKeyCheck(KeyVerdict.Accepted, SpotTrading: true, FuturesTrading: false, CanWithdraw: false, IpRestricted: true)
            : new ExchangeKeyCheck(KeyVerdict.Accepted, SpotTrading: false, FuturesTrading: true);

        await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);
        await client.StoreExchangeKeyAsync("demo", Keys.Of("ada", "demo"), Keys.Of("ada-sealed", "demo"));

        // What Binance said of each credential is stored beside it.
        var live = StoredExchangeCredential.Of(application.Database, user.Id, "Live");
        Assert.Equal(("Valid", Morning), (live.Status, live.ValidatedAt));
        Assert.Equal((true, false, true), (live.SpotTrading, live.FuturesTrading, live.IpRestricted));

        // The test environment reports no restriction to addresses, so none is recorded.
        var demo = StoredExchangeCredential.Of(application.Database, user.Id, "Demo");
        Assert.Equal((false, true, null), (demo.SpotTrading, demo.FuturesTrading, demo.IpRestricted));

        // The list says what each may trade on. Neither gets the warning: one is restricted,
        // and of the other it is not known.
        var list = KeyBrowser.Read(await client.PageAsync(KeysPage));
        Assert.Contains("Spot trading: allowed. Futures trading: not allowed.", list);
        Assert.Contains("Spot trading: not allowed. Futures trading: allowed.", list);
        Assert.DoesNotContain(AnyAddress, list);
        Assert.DoesNotContain(OutboundAddress, list);
    }

    [Fact]
    public async Task A_key_with_no_IP_restriction_is_stored_with_a_warning_and_the_addresses_of_the_service()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        application.Providers.BinanceAnswer = (_, _) => StandInProviders.TakenInLive with { IpRestricted = false };

        var (path, list) = await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);

        // It is stored: the warning is advice, and the key works.
        Assert.Equal(KeysPage, path);
        Assert.False(StoredExchangeCredential.Of(application.Database, user.Id, "Live").IpRestricted);

        // The list warns, and names the addresses to allow at Binance.
        Assert.Contains(
            $"<p class=\"key-warning\" role=\"note\">{AnyAddress} A key that works only from listed addresses is safer. " +
            $"Restrict it at Binance to the addresses of this service, {OutboundAddress}, and check it again.</p>",
            KeyBrowser.Read(list));

        // Once the user has restricted the key, and it is checked again, the warning is gone.
        application.Providers.BinanceAnswer = (_, _) => StandInProviders.TakenInLive;
        var (_, restricted) = await client.CheckExchangeKeyAsync("live");

        Assert.True(StoredExchangeCredential.Of(application.Database, user.Id, "Live").IpRestricted);
        Assert.DoesNotContain(AnyAddress, restricted);
        Assert.DoesNotContain("key-warning", restricted);
    }

    [Fact]
    public async Task The_warning_stands_alone_where_the_configuration_file_lists_no_address()
    {
        using var application = new AccountsApplication(settings: file => file.WithJson("Binance:OutboundIps", "[]"));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        application.Providers.BinanceAnswer = (_, _) => StandInProviders.TakenInLive with { IpRestricted = false };

        var form = await client.PageAsync(KeyBrowser.ExchangeKeyPage("live"));
        var (_, list) = await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);

        Assert.Contains(
            $"<p class=\"key-warning\" role=\"note\">{AnyAddress} A key that works only from listed addresses is safer.</p>",
            KeyBrowser.Read(list));
        Assert.DoesNotContain("outbound-addresses", form);
        Assert.DoesNotContain("list the addresses", form);
    }

    [Fact]
    public async Task The_page_that_takes_the_keys_shows_the_addresses_of_the_service_and_says_that_no_key_may_withdraw()
    {
        using var application = new AccountsApplication(
            settings: file => file.WithJson("Binance:OutboundIps", """[ "203.0.113.7", "2001:db8::17" ]"""));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        foreach (var environment in new[] { "demo", "live" })
        {
            var form = KeyBrowser.Read(await client.PageAsync(KeyBrowser.ExchangeKeyPage(environment)));

            // Before a key is made at Binance: what it must not be able to do, and where the
            // service calls from, each address whole.
            Assert.Contains("<strong>Make the API key without the permission to withdraw.</strong>", form);
            Assert.Contains("stores no key that can withdraw", form);
            Assert.Contains("list the addresses that Ecnanib calls Binance from", form);
            Assert.Contains("<li>203.0.113.7</li>", form);
            Assert.Contains("<li>2001:db8::17</li>", form);
        }

        // The addresses are for the pages. No log event holds one.
        Assert.DoesNotContain("203.0.113.7", application.LogText());
        Assert.DoesNotContain("2001:db8::17", application.LogText());
    }

    /// <summary>The events of the audit trail that are about a key, in the order they were recorded.</summary>
    private static IReadOnlyList<string> KeyEventsOf(AccountsApplication application) =>
        [
            .. StoredAuditEvent.All(application.Database)
                .Select(recorded => recorded.Action)
                .Where(action => action.StartsWith("Key", StringComparison.Ordinal)),
        ];
}
