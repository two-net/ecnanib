using System.Reflection;
using Ecnanib.Credentials;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Persistence;
using Ecnanib.WebUi.Pages.Account;

namespace Ecnanib.Tests.Credentials;

/// <summary>
/// A Binance key that lacks the permission an agent's market needs (Spot trading or Futures
/// trading) blocks that agent from starting, with a message naming the missing permission
/// (FR-KEY-5). Agents are not built yet: Credentials says what blocks a start, from what the
/// last check of the key recorded, and Agents asks it before an agent starts.
/// </summary>
public class AgentStartBlockTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true, true, Market.Spot, null)]
    [InlineData(true, true, Market.Futures, null)]
    [InlineData(true, false, Market.Spot, null)]
    [InlineData(true, false, Market.Futures, StartBlock.FuturesTradingMissing)]
    [InlineData(false, true, Market.Spot, StartBlock.SpotTradingMissing)]
    [InlineData(false, true, Market.Futures, null)]
    [InlineData(false, false, Market.Spot, StartBlock.SpotTradingMissing)]
    [InlineData(false, false, Market.Futures, StartBlock.FuturesTradingMissing)]
    public async Task A_key_that_lacks_the_permission_of_the_market_blocks_the_start_and_any_other_key_does_not(
        bool spotTrading, bool futuresTrading, Market market, StartBlock? expected)
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        application.Providers.BinanceAnswer = (_, _) =>
            new ExchangeKeyCheck(KeyVerdict.Accepted, spotTrading, futuresTrading, CanWithdraw: false, IpRestricted: true);
        await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);

        var block = await application.KeyValidation.BlockForAsync(user.Id, TradingMode.Live, market, TestContext.Current.CancellationToken);

        Assert.Equal(expected, block);
    }

    [Fact]
    public void The_message_names_the_permission_that_is_missing()
    {
        // What the user is told when the start is blocked: which key, and which permission.
        Assert.Equal(
            "Your Binance key for Live lacks the Futures trading permission, which this agent's market needs. " +
            "Turn on Futures trading for the key at Binance, and check the key again on the page of your keys.",
            KeyPages.Says(StartBlock.FuturesTradingMissing, TradingMode.Live));
        Assert.Equal(
            "Your Binance key for Demo lacks the Spot trading permission, which this agent's market needs. " +
            "Turn on Spot trading for the key at Binance, and check the key again on the page of your keys.",
            KeyPages.Says(StartBlock.SpotTradingMissing, TradingMode.Demo));

        // Each names its own permission, and not the other one.
        Assert.DoesNotContain("Spot", KeyPages.Says(StartBlock.FuturesTradingMissing, TradingMode.Live));
        Assert.DoesNotContain("Futures", KeyPages.Says(StartBlock.SpotTradingMissing, TradingMode.Live));

        // Every block has a message, for both environments.
        Assert.All(
            Enum.GetValues<StartBlock>().SelectMany(block => Enum.GetValues<TradingMode>().Select(environment => (block, environment))),
            blocked => Assert.Contains($"Binance key", KeyPages.Says(blocked.block, blocked.environment)));
    }

    [Fact]
    public async Task The_key_that_counts_is_the_one_of_the_agents_user_for_the_agents_environment()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        using var gracesBrowser = application.CreateClient();
        var validation = application.KeyValidation;
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);

        // Ada's Live key trades on Spot only, and her Demo key on both. Grace has a Live key
        // that trades on both.
        application.Providers.BinanceAnswer = (environment, key) => key == Keys.BinanceApi && environment == TradingMode.Live
            ? StandInProviders.TakenInLive with { FuturesTrading = false }
            : environment == TradingMode.Live ? StandInProviders.TakenInLive : StandInProviders.TakenInDemo;

        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);
        await client.StoreExchangeKeyAsync("demo", Keys.Of("ada", "demo"), Keys.Of("ada-sealed", "demo"));
        await gracesBrowser.SignInAsync(Grace);
        await gracesBrowser.ConfirmIdentityAsync();
        await gracesBrowser.StoreExchangeKeyAsync("live", Keys.Of("grace", "live"), Keys.Of("grace-sealed", "live"));

        var cancellationToken = TestContext.Current.CancellationToken;

        // Ada's Futures agent is blocked in Live, and not in Demo. Grace's key does not help her.
        Assert.Equal(StartBlock.FuturesTradingMissing, await validation.BlockForAsync(ada.Id, TradingMode.Live, Market.Futures, cancellationToken));
        Assert.Null(await validation.BlockForAsync(ada.Id, TradingMode.Demo, Market.Futures, cancellationToken));
        Assert.Null(await validation.BlockForAsync(grace.Id, TradingMode.Live, Market.Futures, cancellationToken));

        // Grace has no Demo key, which blocks a start there. So does having no account at all.
        Assert.Equal(StartBlock.NoKey, await validation.BlockForAsync(grace.Id, TradingMode.Demo, Market.Spot, cancellationToken));
        Assert.Equal(StartBlock.NoKey, await validation.BlockForAsync(Guid.CreateVersion7(), TradingMode.Live, Market.Spot, cancellationToken));
    }

    [Fact]
    public async Task A_key_that_is_not_valid_blocks_the_start_whatever_it_could_trade_on()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var validation = application.KeyValidation;
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning.AddDays(-1));
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Null(await validation.BlockForAsync(user.Id, TradingMode.Demo, Market.Futures, cancellationToken));

        // Binance refuses the key when it is checked again: it was revoked.
        application.Providers.BinanceAnswer = (_, _) => ExchangeKeyCheck.Refused;
        await application.CheckKeysAsync(clock.GetUtcNow());

        Assert.Equal(StartBlock.NotValid, await validation.BlockForAsync(user.Id, TradingMode.Demo, Market.Spot, cancellationToken));
        Assert.Equal(StartBlock.NotValid, await validation.BlockForAsync(user.Id, TradingMode.Demo, Market.Futures, cancellationToken));

        // A key that was never checked is not known to be good either.
        application.Database.Execute("UPDATE exchange_credentials SET status = 'Unchecked', validated_at = NULL");

        Assert.Equal(StartBlock.NotValid, await validation.BlockForAsync(user.Id, TradingMode.Demo, Market.Spot, cancellationToken));
    }

    [Fact]
    public async Task The_block_is_gone_once_the_permission_is_turned_on_and_the_key_is_checked_again()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        application.Providers.BinanceAnswer = (_, _) => StandInProviders.TakenInLive with { FuturesTrading = false };
        await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(
            StartBlock.FuturesTradingMissing,
            await application.KeyValidation.BlockForAsync(user.Id, TradingMode.Live, Market.Futures, cancellationToken));

        // The block asks Binance nothing: it is answered from what the last check recorded.
        Assert.Single(application.Providers.Asked);

        // The user does what the message says: turns Futures on at Binance, and checks the key
        // again on the page of their keys. The same key is then good for the agent.
        application.Providers.BinanceAnswer = (_, _) => StandInProviders.TakenInLive;
        clock.Set(Morning.AddMinutes(5));
        await client.CheckExchangeKeyAsync("live");

        Assert.Null(await application.KeyValidation.BlockForAsync(user.Id, TradingMode.Live, Market.Futures, cancellationToken));
    }

    [Fact]
    public void Once_agents_are_stored_the_component_that_keeps_them_asks_what_blocks_a_start()
    {
        using var model = ModelOnly.Context();

        // Agents are not built yet, and nothing starts one. As soon as the component has a
        // table of its own, it must ask: Credentials cannot keep an agent from starting itself.
        var storesRows = model.Model.GetEntityTypes().Any(entity => IsOfAgents(entity.ClrType));
        var asks = typeof(IKeyValidation).Assembly.GetTypes()
            .Where(IsOfAgents)
            .SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter => parameter.ParameterType == typeof(IKeyValidation));

        Assert.True(
            asks || !storesRows,
            "Agents stores rows, and nothing of it asks whether the user's Binance key lets an agent start. A key that lacks " +
            "the permission of the agent's market blocks the start, with a message naming the permission (FR-KEY-5). Take " +
            $"{nameof(IKeyValidation)} in Ecnanib.Agents, ask {nameof(IKeyValidation.BlockForAsync)} before an agent starts, and " +
            "show what KeyPages.Says makes of the answer. See docs/credentials.md.");

        static bool IsOfAgents(Type type) =>
            type.Namespace is { } area && (area == "Ecnanib.Agents" || area.StartsWith("Ecnanib.Agents.", StringComparison.Ordinal));
    }
}
