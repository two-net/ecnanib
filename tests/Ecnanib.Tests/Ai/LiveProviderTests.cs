using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Modules.AiCouncil.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecnanib.Tests.Ai;

/// <summary>
/// EXT-AI-01, EXT-AI-02, EXT-AI-04 against the real providers, at the base URLs appsettings.json ships,
/// through the real pipeline. They pin what the fixtures can only take from the docs. Each provider needs
/// ECNANIB_LIVE_&lt;PROVIDER&gt;_KEY and ECNANIB_LIVE_&lt;PROVIDER&gt;_MODEL, such as ECNANIB_LIVE_XAI_KEY, and is
/// skipped without them, so CI never calls a provider or spends money. A run costs a few cents a provider.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveProviderTests
{
    private static readonly AiOutputSchema Color = AiOutputSchema.Parse("image_color", """
        {"type":"object","additionalProperties":false,"required":["color"],
         "properties":{"color":{"type":"string","enum":["red","green","blue"]}}}
        """);

    private static readonly AiOutputSchema Headline = AiOutputSchema.Parse("news_headline", """
        {"type":"object","additionalProperties":false,"required":["headline","url"],
         "properties":{"headline":{"type":"string"},"url":{"type":"string"}}}
        """);

    private static readonly string[] ProviderNames = ["Anthropic", "Google", "OpenAI", "xAI"];

    public static TheoryData<string> Providers => new(ProviderNames);

    public static TheoryData<string, AiRole> ProvidersAndRoles
    {
        get
        {
            var data = new TheoryData<string, AiRole>();

            foreach (var provider in ProviderNames)
            {
                foreach (var role in Enum.GetValues<AiRole>())
                {
                    data.Add(provider, role);
                }
            }

            return data;
        }
    }

    /// <summary>Enough for any role to answer, so a failure is the schema's or the model's, not the data's.</summary>
    private static string MadeUpContext =>
        "This is a check of your answer's format, with made-up data. BTCUSDT on Binance USDⓈ-M Futures, 15m candles. "
        + "Last price 64000, EMA20 63800, EMA50 63200, EMA200 61000, RSI14 58, ATR14 400. The 1H trend is up. "
        + "Allowed actions: " + string.Join(", ", TradeActions.For(Market.Futures).Select(TradeActions.Name)) + ". "
        + "Capital allocation 10000 USDT, no position and no pending entry. Risk settings: at most 1% risk per trade, "
        + "a reward-to-risk of at least 1.5, and a stop 1 to 3 ATR away. The Strategist proposed OPEN_LONG, a limit entry "
        + "at 63850 with a stop at 63200, targets 65000 and 66000, and 0.8% risk. A trade that closed: a long from 63000 to "
        + "64200 with its stop at 62500, an R-multiple of 2.4 and a P&L of 48 USDT. There is no news.";

    private static string ContractInstructions(AiRole role)
    {
        using var stream = typeof(OutputContracts).Assembly.GetManifestResourceStream($"Ecnanib.Prompts.OutputContract.{role}.md")!;
        using var reader = new StreamReader(stream);

        return "You are one member of an AI council for a crypto trading agent.\n\n" + reader.ReadToEnd();
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task The_key_lists_the_model(string provider)
    {
        var (secret, model) = Credentials(provider);
        using var key = secret;
        using var services = Adapters.Shipped();

        var models = await services.GetRequiredService<IAiProviders>().ListModelsAsync(new AiKey(provider, Guid.NewGuid(), key), Cancellation);

        Assert.Contains(model, models.Select(listed => listed.Id));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_chart_is_read_into_the_schema(string provider)
    {
        var (secret, model) = Credentials(provider);
        using var key = secret;
        using var services = Adapters.Shipped();
        var request = new AiRequest(
            model,
            AiRole.TechnicalAnalyst,
            "You name the main color of an image.",
            [AiMessage.User(new AiText("What color is this image?"), new AiImage("image/png", ProviderSamples.RedSquare))],
            4000)
        {
            Schema = Color,
        };

        var response = await services.GetRequiredService<IAiCompletions>().CompleteAsync(new AiKey(provider, Guid.NewGuid(), key), request, Cancellation);

        using var answer = JsonDocument.Parse(response.Text);
        Assert.Equal("red", answer.RootElement.GetProperty("color").GetString());
        Assert.False(response.Truncated);
        Assert.True(response.Usage is { InputTokens: > 0, OutputTokens: > 0 }, response.Usage.ToString());
    }

    /// <summary>Anthropic and Google can't combine search with a schema, so only OpenAI's and xAI's answers must be JSON.</summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Live_search_answers_with_a_schema(string provider)
    {
        var (secret, model) = Credentials(provider);
        using var key = secret;
        using var services = Adapters.Shipped();
        var request = new AiRequest(
            model,
            AiRole.SentimentAnalyst,
            "You find recent news. Answer only with JSON that has a headline and its url.",
            [AiMessage.User("Find one news headline about Bitcoin from the last seven days.")],
            8000)
        {
            Schema = Headline,
            LiveSearch = true,
        };

        var response = await services.GetRequiredService<IAiCompletions>().CompleteAsync(new AiKey(provider, Guid.NewGuid(), key), request, Cancellation);

        Assert.False(string.IsNullOrWhiteSpace(response.Text));
        Assert.False(response.Truncated);

        if (provider is "OpenAI" or "xAI")
        {
            using var answer = JsonDocument.Parse(response.Text);
            Assert.False(string.IsNullOrWhiteSpace(answer.RootElement.GetProperty("headline").GetString()));
        }
    }

    /// <summary>
    /// FR-PRM-08, FR-CYC-05: each provider takes every role's schema, which the fixtures can only take from the docs,
    /// and its model fills it, retrying once if it doesn't. The data is made up, and the instructions are only the
    /// role's seeded output contract.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProvidersAndRoles))]
    public async Task Each_roles_answer_conforms_to_its_contract(string provider, AiRole role)
    {
        var (secret, model) = Credentials(provider);
        using var key = secret;
        using var services = Adapters.Shipped();
        var completions = services.GetRequiredService<IAiCompletions>();
        var aiKey = new AiKey(provider, Guid.NewGuid(), key);
        var request = new AiRequest(model, role, ContractInstructions(role), [AiMessage.User(MadeUpContext)], 8000);
        var context = new ContractContext(Market.Futures) { MaxRiskPercent = 1m };

        var answer = await OutputContracts.CompleteAsync(
            (sent, cancellationToken) => completions.CompleteAsync(aiKey, sent, cancellationToken),
            request,
            context,
            NullLogger.Instance,
            Cancellation);

        Assert.Equal(role, answer.Output.Role);
        Assert.True(answer.Usage is { InputTokens: > 0, OutputTokens: > 0 }, answer.Usage.ToString());
    }

    /// <summary>FR-KEY-04: Credentials marks a key Invalid on exactly this.</summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_wrong_key_is_an_authentication_failure(string provider)
    {
        var (secret, _) = Credentials(provider);
        secret.Dispose();
        using var wrong = SecretValue.FromString("wrong-" + Guid.NewGuid().ToString("N"));
        using var services = Adapters.Shipped();

        var exception = await Assert.ThrowsAsync<AiException>(
            () => services.GetRequiredService<IAiProviders>().ListModelsAsync(new AiKey(provider, Guid.NewGuid(), wrong), Cancellation));

        Assert.Equal((AiFailure.Authentication, 1), (exception.Failure, exception.Attempts));
    }

    /// <summary>The key and model for <paramref name="provider"/>, or a skip.</summary>
    private static (SecretValue Key, string Model) Credentials(string provider)
    {
        var prefix = "ECNANIB_LIVE_" + provider.ToUpperInvariant();
        var key = Environment.GetEnvironmentVariable(prefix + "_KEY");
        var model = Environment.GetEnvironmentVariable(prefix + "_MODEL");

        Assert.SkipWhen(string.IsNullOrEmpty(key) || string.IsNullOrEmpty(model), $"Set {prefix}_KEY and {prefix}_MODEL to call {provider}.");

        return (SecretValue.FromString(key!), model!);
    }
}
