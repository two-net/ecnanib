using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.AiProviderGateway;
using Ecnanib.DecisionEngine;
using Ecnanib.Shared;
using Ecnanib.Tests.DecisionEngine;
using Ecnanib.Tests.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.AiProviderGateway;

/// <summary>
/// The adapters against the real providers. Every other test of the gateway answers in a
/// provider's place, with what its documentation says it sends. These ask the provider itself,
/// so they show that it takes the requests and answers as the adapters read it.
/// </summary>
/// <remarks>
/// <para>
/// They are skipped unless whoever runs them gives a key and a model for a provider, in the
/// environment of the run: <c>ECNANIB_LIVE_CLAUDE_KEY</c> and <c>ECNANIB_LIVE_CLAUDE_MODEL</c>,
/// and the same with <c>GEMINI</c>, <c>GPT</c>, and <c>GROK</c>. So the suite never reaches a
/// provider by itself, and no key is stored. A run costs what seven small calls to a model cost,
/// on the key that was given: three, and one with the schema of each of the four roles. The
/// lists of models cost nothing. See docs/ai-providers.md.
/// </para>
/// <para>
/// A call with live search is made only when the run also names a model that can search, as
/// <c>ECNANIB_LIVE_CLAUDE_SEARCH_MODEL</c>, and the same with the other providers. It costs one
/// more call and the searches that the model runs, which a provider charges for beside the
/// tokens.
/// </para>
/// <para>
/// The variables are read here, by the tests. The application reads none.
/// </para>
/// </remarks>
public class LiveProviderTests
{
    private const string Prompt =
        "Bitcoin closed higher on each of the last five days, on rising volume. " +
        "Say whether the bias is bullish, bearish, or neutral, and how confident you are, from 0 to 1.";

    // Nothing in it can be answered from memory: a model that is offered a search has to use it.
    private const string SearchPrompt =
        "Search for news about Bitcoin that was published in the last 24 hours, and read at least one source. " +
        "From what you found alone, say whether the bias is bullish, bearish, or neutral, and how confident you are, from 0 to 1.";

    public static TheoryData<string> Providers => [.. ModelCalls.Providers];

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_provider_answers_a_call_with_a_result_of_the_schema_and_its_usage(string code)
    {
        var (key, model) = GivenFor(code);
        using var application = OnTheNetwork();
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var result = await gateway.CompleteAsync(
            new AiProviderCode(code), new AiCallRequest(model, Prompt, ModelCalls.Schema), key, TestContext.Current.CancellationToken);

        Assert.Equal(AiCallOutcome.Succeeded, result.Outcome);

        using var answer = JsonDocument.Parse(result.ResultJson!);
        Assert.Contains(answer.RootElement.GetProperty("bias").GetString(), new[] { "bullish", "bearish", "neutral" });
        Assert.Equal(JsonValueKind.Number, answer.RootElement.GetProperty("confidence").ValueKind);
        Assert.Equal(2, answer.RootElement.EnumerateObject().Count());

        Assert.NotNull(result.Usage);
        Assert.True(result.Usage.Value.InputTokens > 0, "The provider counted no input tokens.");
        Assert.True(result.Usage.Value.OutputTokens > 0, "The provider counted no output tokens.");
        Assert.InRange(result.Latency, TimeSpan.FromMilliseconds(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Every provider with every role: a provider is sent the schema of whichever role its model is given.</summary>
    public static TheoryData<string, Role> ProvidersAndRoles => new MatrixTheoryData<string, Role>(ModelCalls.Providers, Enum.GetValues<Role>());

    [Theory]
    [MemberData(nameof(ProvidersAndRoles))]
    public async Task A_provider_takes_the_schema_of_each_role_and_answers_with_a_result_that_is_valid(string code, Role role)
    {
        var (key, model) = GivenFor(code);
        using var application = OnTheNetwork();
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();
        var results = application.Services.GetRequiredService<IRoleResults>();

        // What each role is asked for has a plain answer, so that what is shown is the schema:
        // a list of objects, values of a fixed set, and fields that are null.
        var prompt = role switch
        {
            Role.MarketAnalyst =>
                "Bitcoin closed higher on each of the last five days, on rising volume, between 58000 and 62000. " +
                "Report your bias, one support level and one resistance level, the volatility, and a rationale of one sentence.",
            Role.SentimentAnalyst =>
                "You are given no news, and you cannot search. Report a neutral bias, no notable events, a confidence of 0.1, " +
                "that live data was not available, and a rationale of one sentence.",
            Role.Strategist =>
                "The agent has no open position, and the market is quiet. Plan to hold: the action is hold, " +
                "and every other field but the rationale is null. Give a rationale of one sentence.",
            _ =>
                "A plan opens a long position with a risk fraction of 0.01, a stop-loss below a clear support, and a take-profit " +
                "at twice that distance. Approve it: the risk fraction of your review is null. Give your reasons in one sentence.",
        };

        var result = await gateway.CompleteAsync(
            new AiProviderCode(code), new AiCallRequest(model, prompt, results.SchemaOf(role)), key, TestContext.Current.CancellationToken);

        // The provider took the schema: one that it refuses is answered with an error, which is Failed.
        Assert.Equal(AiCallOutcome.Succeeded, result.Outcome);

        // And what it answered is inside the schema and the ranges of the role.
        var plan = results.ReadPlan(TeamAnswers.Plan()).Value!;
        var rejection = role switch
        {
            Role.MarketAnalyst => results.ReadMarketReport(result.ResultJson!).Rejection,
            Role.SentimentAnalyst => results.ReadSentiment(result.ResultJson!).Rejection,
            // The prompt says that the agent has no open position: a plan is held to that.
            Role.Strategist => results.ReadPlan(result.ResultJson!, PlanAgents.On(Market.Futures)).Rejection,
            _ => results.ReadReview(result.ResultJson!, plan).Rejection,
        };

        Assert.True(rejection is null, $"The answer of {code} for the {role} was rejected: {rejection}.");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_provider_searches_live_and_answers_with_a_result_of_the_schema(string code)
    {
        var (key, _) = GivenFor(code);
        var model = SearchModelFor(code);
        using var application = OnTheNetwork();
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var result = await gateway.CompleteAsync(
            new AiProviderCode(code),
            new AiCallRequest(model, SearchPrompt, ModelCalls.Schema, LiveSearch: true),
            key,
            TestContext.Current.CancellationToken);

        // The provider took its search together with the schema, which its documentation does
        // not promise of every model, and the result is the object alone: no word before the
        // search, and no source written into it.
        Assert.Equal(AiCallOutcome.Succeeded, result.Outcome);

        using var answer = JsonDocument.Parse(result.ResultJson!);
        Assert.Contains(answer.RootElement.GetProperty("bias").GetString(), new[] { "bullish", "bearish", "neutral" });
        Assert.Equal(JsonValueKind.Number, answer.RootElement.GetProperty("confidence").ValueKind);
        Assert.Equal(2, answer.RootElement.EnumerateObject().Count());

        // The searches that the provider ran are where the adapter looks for them.
        Assert.True(result.LiveSearches > 0, $"The result says that {result.LiveSearches} searches ran.");
        Assert.NotNull(result.Usage);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_provider_answers_a_wrong_key_as_the_adapter_expects(string code)
    {
        var (_, model) = GivenFor(code);
        using var application = OnTheNetwork();
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var result = await gateway.CompleteAsync(
            new AiProviderCode(code),
            new AiCallRequest(model, Prompt, ModelCalls.Schema),
            "not-a-key-0123456789-abcdefghij-klmnop",
            TestContext.Current.CancellationToken);

        Assert.Equal(AiCallOutcome.AuthenticationFailed, result.Outcome);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_provider_answers_a_model_it_does_not_have_as_the_adapter_expects(string code)
    {
        var (key, _) = GivenFor(code);
        using var application = OnTheNetwork();
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var result = await gateway.CompleteAsync(
            new AiProviderCode(code),
            new AiCallRequest("no-such-model-of-ecnanib-0000", Prompt, ModelCalls.Schema),
            key,
            TestContext.Current.CancellationToken);

        Assert.Equal(AiCallOutcome.ModelNotFound, result.Outcome);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_provider_lists_its_models_as_the_list_reads_them(string code)
    {
        var (key, model) = GivenFor(code);
        using var application = OnTheNetwork();
        var lists = application.Services.GetRequiredService<IAiModelLists>();

        var list = await lists.ListAsync(new AiProviderCode(code), key, TestContext.Current.CancellationToken);

        Assert.Equal(AiModelListOutcome.Listed, list.Outcome);
        Assert.Equal(list.Models.Count, list.Models.Select(listed => listed.Id).Distinct(StringComparer.Ordinal).Count());

        // The model that was given is among them, under the id that a call names it by. Gemini
        // lists it with "models/" before the id, which the catalog leaves out.
        var given = Assert.Single(list.Models, listed => listed.Id == model || $"models/{listed.Id}" == model);

        // What the list of this provider is read for, and what its documentation says it holds.
        switch (code)
        {
            case "claude":
                Assert.NotNull(given.DisplayName);
                Assert.NotNull(given.ContextTokens);
                Assert.NotNull(given.OutputTokens);
                Assert.NotNull(given.ReleasedAt);
                break;

            case "gemini":
                Assert.NotNull(given.DisplayName);
                Assert.NotNull(given.ContextTokens);
                Assert.NotNull(given.OutputTokens);
                break;

            case "gpt":
                Assert.NotNull(given.ReleasedAt);
                break;

            default:
                Assert.NotNull(given.ContextTokens);
                Assert.NotNull(given.ReleasedAt);
                break;
        }

        // No date that a provider gave is one that it does not know, or one of another time zone.
        Assert.All(
            list.Models.SelectMany(listed => new[] { listed.ReleasedAt, listed.DeprecatedAt, listed.RetiresAt }).OfType<DateTimeOffset>(),
            moment => Assert.True(moment > DateTimeOffset.UnixEpoch && moment.Offset == TimeSpan.Zero));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_provider_answers_a_wrong_key_to_its_list_as_the_list_expects(string code)
    {
        _ = GivenFor(code);
        using var application = OnTheNetwork();
        var lists = application.Services.GetRequiredService<IAiModelLists>();

        var list = await lists.ListAsync(
            new AiProviderCode(code), "not-a-key-0123456789-abcdefghij-klmnop", TestContext.Current.CancellationToken);

        Assert.Equal(AiModelListOutcome.AuthenticationFailed, list.Outcome);
    }

    /// <summary>The key and the model that the run was given for a provider. Without both, the test is skipped.</summary>
    private static (string Key, string Model) GivenFor(string code)
    {
        var name = $"ECNANIB_LIVE_{code.ToUpperInvariant()}";
        var key = Environment.GetEnvironmentVariable($"{name}_KEY");
        var model = Environment.GetEnvironmentVariable($"{name}_MODEL");

        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(model),
            $"Set {name}_KEY and {name}_MODEL to call {code} for real. See docs/ai-providers.md.");

        return (key!.Trim(), model!.Trim());
    }

    /// <summary>
    /// The model that the run named as one that can search live at a provider. Without it, the
    /// test is skipped: not every model of a provider takes its search, and a search is charged for.
    /// </summary>
    private static string SearchModelFor(string code)
    {
        var name = $"ECNANIB_LIVE_{code.ToUpperInvariant()}_SEARCH_MODEL";
        var model = Environment.GetEnvironmentVariable(name);

        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(model), $"Set {name} to have a model of {code} search live for real. See docs/ai-providers.md.");

        return model!.Trim();
    }

    /// <summary>
    /// The application with the network behind the client of the gateway, and with the providers
    /// as the tracked configuration file has them: their real addresses, and its time limit.
    /// </summary>
    private static LoggedApplication OnTheNetwork()
    {
        // The test project copies the tracked file into its output under this name.
        var tracked = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.tracked.json")))!;

        return new LoggedApplication(
            LoggedApplication.Settings().With("AiProviders", tracked["AiProviders"]!.DeepClone()),
            host => host.ConfigureServices(services => services
                .AddHttpClient(AiProviderGatewayModule.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler())));
    }
}
