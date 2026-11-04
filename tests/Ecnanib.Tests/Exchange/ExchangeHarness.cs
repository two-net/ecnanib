using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Exchange;

/// <summary>
/// The application with its own database, clock and <see cref="FakeBinance"/>. The adapter keeps its
/// limits, bans and clock offset in memory, so each test gets its own, and they can't leak between tests.
/// </summary>
internal sealed class ExchangeHarness : IAsyncDisposable
{
    /// <summary>The key and secret from Binance's signing example.</summary>
    public const string ExampleApiKey = "vmPUZE6mv9SD5VNHk4HlWFsOr6aKE2zvsw0MuIgwCIPy6utIco14y7Ju91duEh8A";

    public const string ExampleSecret = "NhqPtmdSJYdKjVHjA7PZj4Mge3R5YNiP1e3UZjInClVN65XAbvqqM6A7H5fATj0j";

    public ExchangeHarness(
        PostgresFixture postgres,
        Dictionary<string, string?>? settings = null,
        Action<IServiceCollection>? services = null,
        string? connectionString = null)
    {
        Binance = new FakeBinance(Clock);
        App = new AppFactory(
            connectionString ?? postgres.NewConnectionString(),
            collection =>
            {
                Use(collection, Binance, Clock);
                services?.Invoke(collection);
            },
            settings);
        App.Start();
    }

    public TestClock Clock { get; } = new();

    public FakeBinance Binance { get; }

    public AppFactory App { get; }

    public IExchangeMarketData MarketData => App.Services.GetRequiredService<IExchangeMarketData>();

    public IExchangeAccount Account => App.Services.GetRequiredService<IExchangeAccount>();

    public IExchangeTrading Trading => App.Services.GetRequiredService<IExchangeTrading>();

    public static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Sends the adapter's requests to <paramref name="handler"/>, and gives the application <paramref name="clock"/>.</summary>
    public static void Use(IServiceCollection services, HttpMessageHandler handler, TimeProvider clock)
    {
        services.AddSingleton(clock);
        services.AddHttpClient("Binance")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
    }

    public static ExchangeKey HmacKey(TradingEnvironment environment = TradingEnvironment.Demo, Guid? credentialId = null) =>
        new(credentialId ?? Guid.NewGuid(), environment, ExchangeKeyType.Hmac, SecretValue.FromString(ExampleApiKey), SecretValue.FromString(ExampleSecret));

    public ExchangeServerUsage Usage(ExchangeServer server) =>
        App.Services.GetRequiredService<IExchangeUsage>().Current().Single(usage => usage.Server == server);

    /// <summary>
    /// Moves the clock to just after a minute starts, so that Binance's weight and order windows can't
    /// roll over in the middle of a test.
    /// </summary>
    public void StartOfMinute()
    {
        var intoMinute = Clock.GetUtcNow().ToUnixTimeMilliseconds() % 60_000;
        Clock.Advance(TimeSpan.FromMilliseconds(60_000 - intoMinute + 500));
    }

    public ValueTask DisposeAsync() => App.DisposeAsync();
}
