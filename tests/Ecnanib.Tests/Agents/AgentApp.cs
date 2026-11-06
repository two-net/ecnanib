using System.Collections.Concurrent;
using System.Net.Http;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Ai;
using Ecnanib.Tests.Audit;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// The application with its own database and clock, a <see cref="FakeBinance"/> that lists BTCUSDT on Spot and
/// Futures and takes any Demo key, and a <see cref="FakeAiProvider"/> behind the Test and Second providers,
/// whose keys list model-a unless a test gives one its own list. Credential observers are on, so the catalog
/// syncs a new AI key and Agents hears of every change to a key. The revalidation, catalog schedule and
/// recovery jobs run only when a test runs them.
/// </summary>
internal sealed class AgentApp : IAsyncDisposable
{
    public const string Provider = CredentialApp.Provider;
    public const string Second = "Second";
    public const string Model = "model-a";
    public const string Symbol = "BTCUSDT";

    private readonly ConcurrentDictionary<string, string[]> _lists = new(StringComparer.Ordinal);

    public AgentApp(
        PostgresFixture postgres,
        Dictionary<string, string?>? settings = null,
        FakeExposure? exposure = null,
        Action<IServiceCollection>? services = null,
        string? connectionString = null,
        IReadOnlyCollection<string>? manualJobs = null,
        Action<AgentApp>? beforeStart = null)
    {
        Binance = new FakeBinance(Clock);
        App = AccountApp.Create(
            postgres,
            CredentialApp.Settings(settings),
            collection =>
            {
                ExchangeHarness.Use(collection, Binance, Clock);
                AiHarness.Use(collection, Ai, new TestAdapter(Provider), new TestAdapter(Second));

                if (exposure is not null)
                {
                    collection.Replace(ServiceDescriptor.Singleton<IAgentExposure>(exposure));
                }

                services?.Invoke(collection);
            },
            connectionString: connectionString,
            manualJobs: ["CredentialRevalidationJob", "AgentPrerequisitesJob", "CatalogSyncJob", "ModelLifecycleJob", "LossLimitResumeJob", .. manualJobs ?? []]);

        Ai.On(
            HttpMethod.Get,
            CredentialApp.ModelsPath,
            request => FakeAiProvider.Models(null, _lists.TryGetValue(request.Key ?? "", out var ids) ? ids : [Model]));
        Binance.On(HttpMethod.Get, "/api/v3/exchangeInfo", _ => FakeBinance.Json(BinanceSamples.SpotExchangeInfo()));
        Binance.On(HttpMethod.Get, "/fapi/v1/exchangeInfo", _ => FakeBinance.Json(BinanceSamples.FuturesExchangeInfo));
        BinanceAccepts();

        // Hosted services start with the application, so what they find on Binance is set up first.
        beforeStart?.Invoke(this);
        App.Start();
    }

    public TestClock Clock { get; } = new();

    public FakeBinance Binance { get; }

    public FakeAiProvider Ai { get; } = new();

    public AppFactory App { get; }

    public IAgents Agents => App.Services.GetRequiredService<IAgents>();

    public ICredentials Credentials => App.Services.GetRequiredService<ICredentials>();

    public string ConnectionString => App.ConnectionString();

    public static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>From now on, <paramref name="key"/> lists these models.</summary>
    public void Lists(string key, params string[] ids) => _lists[key] = ids;

    /// <summary>
    /// Binance takes a Demo key on the markets given. Its Spot account holds 250.12 USDT, and its Futures account
    /// 103.12345678 USDT, in One-way and Single-Asset Mode (EXT-BIN-07).
    /// </summary>
    public void BinanceAccepts(bool spot = true, bool futures = true)
    {
        Binance.On(
            HttpMethod.Get,
            CredentialApp.SpotAccountPath,
            _ => spot ? FakeBinance.Json(BinanceSamples.SpotAccount) : CredentialApp.Refusal());
        Binance.On(
            HttpMethod.Get,
            CredentialApp.FuturesAccountPath,
            _ => futures ? FakeBinance.Json(BinanceSamples.FuturesAccount) : CredentialApp.Refusal());
        Binance.On(
            HttpMethod.Get,
            FuturesAccountConfigPath,
            _ => futures ? FakeBinance.Json(BinanceSamples.FuturesAccountConfig()) : CredentialApp.Refusal());
    }

    public const string FuturesAccountConfigPath = "/fapi/v1/accountConfig";

    /// <summary>A user with a valid Binance Demo key and a valid AI key whose model the catalog lists.</summary>
    public async Task<User> UserAsync()
    {
        var user = new User(Guid.NewGuid(), Guid.Empty, Guid.Empty);
        var exchange = await AddBinanceAsync(user.Id);
        var ai = await AddAiAsync(user.Id);

        return user with { ExchangeKey = exchange, AiKey = ai };
    }

    public async Task<Guid> AddBinanceAsync(Guid userId, string label = "Demo account")
    {
        using var key = SecretValue.FromString(ExchangeHarness.ExampleApiKey);
        using var secret = SecretValue.FromString(ExchangeHarness.ExampleSecret);

        var result = await Credentials.AddBinanceKeyAsync(
            userId, TradingEnvironment.Demo, label, BinanceKeyType.Hmac, key, secret, null, Cancellation);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(CredentialStatus.Valid, result.Credential!.Status);

        return result.Credential.Id;
    }

    /// <summary>Adds a key, and waits for the catalog to sync it.</summary>
    public async Task<Guid> AddAiAsync(Guid userId, string provider = Provider, string key = CredentialApp.AiKey)
    {
        var synced = await FinishedAsync("catalog-sync-credential");

        using var apiKey = SecretValue.FromString(key);
        var result = await Credentials.AddAiKeyAsync(userId, provider, "Personal", apiKey, Cancellation);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        await LoggedApp.UntilAsync(async () => await FinishedAsync("catalog-sync-credential") > synced);

        return result.Credential!.Id;
    }

    /// <summary>Settings that pass every activation check for <paramref name="user"/>.</summary>
    public static AgentSettings Ready(User user, string name = "Scalper", Market market = Market.Spot) => new()
    {
        Name = name,
        Market = market,
        Symbol = Symbol,
        Timeframe = Timeframe.OneHour,
        ExchangeCredentialId = user.ExchangeKey,
        ExecutionMode = ExecutionMode.Confirm,
        Roles =
        [
            new RoleAssignment(AiRole.TechnicalAnalyst, new ModelRef(Provider, Model)),
            new RoleAssignment(AiRole.Strategist, new ModelRef(Provider, Model)),
            new RoleAssignment(AiRole.RiskCritic, new ModelRef(Provider, Model)),
        ],
        CapitalAllocation = 100m,
    };

    /// <summary>Creates a Draft, which must be saved.</summary>
    public async Task<AgentRecord> CreateAsync(Guid userId, AgentSettings settings)
    {
        var result = await Agents.CreateAsync(userId, settings, Cancellation);

        Assert.Equal(AgentSaveOutcome.Saved, result.Outcome);

        return result.Agent!;
    }

    /// <summary>Creates an agent and activates it, which must succeed.</summary>
    public async Task<AgentRecord> ActiveAsync(User user, string name = "Scalper", Market market = Market.Spot)
    {
        var agent = await CreateAsync(user.Id, Ready(user, name, market));
        var result = await Agents.ActivateAsync(user.Id, agent.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Activated, result.Outcome);

        return result.Agent!;
    }

    public async Task<AgentStatus> StatusAsync(Guid userId, Guid agentId) =>
        (await Agents.FindAsync(userId, agentId, Cancellation))!.Status;

    /// <summary>Runs a manual job now, under a name of its own, so its real schedule isn't moved.</summary>
    public Task<JobStatus> RunJobAsync(string className) =>
        App.Services.GetRequiredService<IJobRunner>().RunAsync(
            $"test-{className}-{Guid.NewGuid():N}", TimeSpan.FromSeconds(30), App.ManualJob(className).RunAsync);

    public Task<long> FinishedAsync(string job) =>
        Sql.ScalarAsync<long>(ConnectionString, $"SELECT count(*) FROM job_runs WHERE job_name = '{job}' AND finished_at IS NOT NULL", Cancellation);

    /// <summary>Waits until one more of the runs a credential change starts has finished than <paramref name="before"/>.</summary>
    public Task UntilCredentialCheckAsync(long before) =>
        LoggedApp.UntilAsync(async () => await FinishedAsync("agent-credential-check") > before);

    public Task<List<AuditRow>> AuditAsync(Guid userId) => AuditRows.OfUserAsync(ConnectionString, userId, Cancellation);

    public ValueTask DisposeAsync() => App.DisposeAsync();

    /// <summary>A user ready to run agents: one valid key of each kind.</summary>
    public sealed record User(Guid Id, Guid ExchangeKey, Guid AiKey);
}

/// <summary>
/// Stands in for Execution, which has no orders yet, so a test can give an agent open positions. Its Futures account
/// is ready for any agent unless a test says otherwise.
/// </summary>
internal sealed class FakeExposure : IAgentExposure
{
    public HashSet<Guid> Exposed { get; } = [];

    public decimal? Balance { get; set; }

    public FuturesAccountRead Futures { get; set; } = FuturesAccountRead.Of(new FuturesAccountState(true, false, false, 1_000m));

    public Task<FuturesAccountRead> ReadFuturesAccountAsync(Guid userId, Guid credentialId, string marginAsset, CancellationToken cancellationToken) =>
        Task.FromResult(Futures);

    public Task<bool> HasOpenPositionsOrOrdersAsync(Guid userId, Guid agentId, CancellationToken cancellationToken) =>
        Task.FromResult(Exposed.Contains(agentId));

    public Task<decimal?> GetAvailableBalanceAsync(Guid userId, Guid credentialId, Market market, string asset, CancellationToken cancellationToken) =>
        Task.FromResult(Balance);
}
