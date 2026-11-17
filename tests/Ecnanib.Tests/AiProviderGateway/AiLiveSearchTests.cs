using System.Net;
using System.Text.Json.Nodes;
using Ecnanib.AiProviderGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.AiProviderGateway;

/// <summary>
/// A model searches live with the search of its own provider, where the call asks for it
/// (FR-CYC-3). Each adapter offers the model its provider's search as a tool of the request, and
/// adds nothing to the prompt, which stays the same for every provider (FR-PRM-10). The result
/// is what the model wrote after its last search, and it says how many searches the provider
/// ran, which is how a caller knows whether a report rests on live data.
/// </summary>
public class AiLiveSearchTests
{
    private const string ModelCallArea = "Ecnanib.AiProviderGateway.Adapters.ModelCall";
    private const string RouterArea = "Ecnanib.AiProviderGateway.AiProviderRouter";

    public static TheoryData<string> Providers => [.. ModelCalls.Providers];

    /// <summary>An answer of each provider whose model searched, with how many searches it shows.</summary>
    public static TheoryData<string, string, int> Searches => new()
    {
        // Claude counts its searches in the usage.
        { "claude", ModelCalls.ClaudeSearched(ModelCalls.Result, searches: 3), 3 },

        // Gemini lists the queries that the model ran, and charges for none that is empty.
        { "gemini", ModelCalls.GeminiSearched(ModelCalls.Result, queries: ["bitcoin news", "", " ", "bitcoin etf flows"]), 2 },

        // GPT has an item for each search. A page that the model opened is no search, and an
        // item of a tool that is none of GPT's is not counted.
        { "gpt", ModelCalls.ResponsesSearched(ModelCalls.Result), 1 },

        // Grok counts them in its usage, those of X beside those of the web.
        { "grok", ModelCalls.ResponsesSearched(ModelCalls.Result, countedByGrok: (Web: 2, X: 3)), 5 },

        // Where its usage does not say, the items do: one search of the web, and one of X.
        { "grok", ModelCalls.ResponsesSearched(ModelCalls.Result), 2 },
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_call_that_asks_for_live_search_offers_the_search_of_its_provider_and_changes_nothing_else(string code)
    {
        var provider = ModelCalls.ThatAnswer();
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        await gateway.CompleteAsync(
            new AiProviderCode(code), ModelCalls.RequestTo(code), ModelCalls.Key, TestContext.Current.CancellationToken);
        await gateway.CompleteAsync(
            new AiProviderCode(code), ModelCalls.SearchingRequestTo(code), ModelCalls.Key, TestContext.Current.CancellationToken);

        var plain = JsonNode.Parse(provider.Calls[0].Body!)!.AsObject();
        var searching = JsonNode.Parse(provider.Calls[1].Body!)!.AsObject();

        // A call that asks for none is offered no tool at all.
        Assert.False(plain.ContainsKey("tools"));

        // The search is a tool of the request, as the provider takes it.
        JsonNode tools = code switch
        {
            "claude" => new JsonArray(new JsonObject { ["type"] = "web_search_20250305", ["name"] = "web_search", ["max_uses"] = 5 }),
            "gemini" => new JsonArray(new JsonObject { ["googleSearch"] = new JsonObject() }),
            "gpt" => new JsonArray(new JsonObject { ["type"] = "web_search" }),
            _ => new JsonArray(new JsonObject { ["type"] = "web_search" }, new JsonObject { ["type"] = "x_search" }),
        };

        Assert.True(JsonNode.DeepEquals(tools, searching["tools"]), $"{code} was offered other tools than expected: {searching["tools"]}");
        searching.Remove("tools");

        // Grok would write its sources into the text, which is the structured result, and is
        // held to so many turns of searching.
        if (code == "grok")
        {
            Assert.True(JsonNode.DeepEquals(new JsonArray("no_inline_citations"), searching["include"]));
            Assert.Equal(5, searching["max_turns"]!.GetValue<int>());
            searching.Remove("include");
            searching.Remove("max_turns");
        }

        // Everything else is the body of a call without search: the prompt as it is, the schema,
        // and no text of the application's own beside the prompt (FR-PRM-10).
        Assert.True(JsonNode.DeepEquals(plain, searching), $"{code} was sent another body than a call without search: {searching}");
        Assert.All(provider.Calls, call => Assert.Equal(provider.Calls[0].Address, call.Address));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task The_result_is_what_the_model_wrote_after_its_last_search(string code)
    {
        var provider = new StandInSystem(_ => StandInSystem.Json(HttpStatusCode.OK, ModelCalls.Searched(code, ModelCalls.Result)));
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var result = await gateway.CompleteAsync(
            new AiProviderCode(code), ModelCalls.SearchingRequestTo(code), ModelCalls.Key, TestContext.Current.CancellationToken);

        // What the model said before it searched is no part of the result, and neither is what
        // a search found. An answer that names its sources is still the one result.
        Assert.Equal(AiCallOutcome.Succeeded, result.Outcome);
        Assert.Equal(ModelCalls.Result, result.ResultJson);
        Assert.True(result.LiveSearches > 0, $"The answer of {code} shows searches, and the result says {result.LiveSearches}.");

        // A model that searched is nothing to warn of.
        Assert.DoesNotContain(application.Events(), written => written.Area == ModelCallArea);
    }

    [Theory]
    [MemberData(nameof(Searches))]
    public async Task A_result_says_how_many_searches_the_provider_ran(string code, string answer, int searches)
    {
        var provider = new StandInSystem(_ => StandInSystem.Json(HttpStatusCode.OK, answer));
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var result = await gateway.CompleteAsync(
            new AiProviderCode(code), ModelCalls.SearchingRequestTo(code), ModelCalls.Key, TestContext.Current.CancellationToken);

        Assert.Equal(AiCallOutcome.Succeeded, result.Outcome);
        Assert.Equal(searches, result.LiveSearches);

        // The one event of the call says it too: the provider charges for a search beside the tokens.
        var call = Assert.Single(application.Events(), written => written.Area == RouterArea);
        Assert.Equal("Information", call.Level);
        Assert.Equal(searches, call["LiveSearches"].GetInt32());
        Assert.Equal(100, call["InputTokens"].GetInt32() - (code == "gemini" ? 400 : 0));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_model_that_was_offered_a_search_and_ran_none_says_so_with_a_warning(string code)
    {
        // The answer of a call without search: the model saw no reason to look anything up.
        var provider = ModelCalls.ThatAnswer();
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var result = await gateway.CompleteAsync(
            new AiProviderCode(code), ModelCalls.SearchingRequestTo(code), ModelCalls.Key, TestContext.Current.CancellationToken);

        // The result is there, and says that it rests on no search: none, and not unknown.
        Assert.Equal(AiCallOutcome.Succeeded, result.Outcome);
        Assert.Equal(ModelCalls.Result, result.ResultJson);
        Assert.Equal(0, result.LiveSearches);

        var none = Assert.Single(application.Events(), written => written.Area == ModelCallArea);
        Assert.Equal("Warning", none.Level);
        Assert.Equal([code, ModelCalls.ModelOf(code)], new[] { "Provider", "Model" }.Select(none.Text));

        Assert.Equal(0, Assert.Single(application.Events(), written => written.Area == RouterArea)["LiveSearches"].GetInt32());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_call_that_asked_for_no_search_ran_none_and_is_logged_as_before(string code)
    {
        var provider = ModelCalls.ThatAnswer();
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var result = await gateway.CompleteAsync(
            new AiProviderCode(code), ModelCalls.RequestTo(code), ModelCalls.Key, TestContext.Current.CancellationToken);

        Assert.Equal(AiCallOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, result.LiveSearches);

        // Nothing is said of searches where none was asked for.
        Assert.DoesNotContain(application.Events(), written => written.Area == ModelCallArea);
        Assert.False(Assert.Single(application.Events(), written => written.Area == RouterArea).Has("LiveSearches"));
    }

    [Fact]
    public async Task What_a_search_put_before_a_model_of_gemini_is_counted_as_input()
    {
        var provider = new StandInSystem(_ => StandInSystem.Json(
            HttpStatusCode.OK, ModelCalls.GeminiSearched(ModelCalls.Result, toolUsePrompt: 730)));
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var result = await gateway.CompleteAsync(
            new AiProviderCode("gemini"), ModelCalls.SearchingRequestTo("gemini"), ModelCalls.Key, TestContext.Current.CancellationToken);

        // Gemini counts it apart from the prompt, and charges both as input.
        Assert.Equal(new AiTokenUsage(830, 50), result.Usage);
    }

    [Fact]
    public async Task A_turn_that_the_provider_paused_while_searching_is_no_result_and_keeps_its_searches()
    {
        var provider = new StandInSystem(_ => StandInSystem.Json(
            HttpStatusCode.OK, ModelCalls.ClaudeSearched(ModelCalls.Result, searches: 4, stopReason: "pause_turn")));
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        var result = await gateway.CompleteAsync(
            new AiProviderCode("claude"), ModelCalls.SearchingRequestTo("claude"), ModelCalls.Key, TestContext.Current.CancellationToken);

        // The gateway calls once, and does not take the turn up again: whether the call is made
        // again is its caller's to decide (FR-TEAM-9). The searches ran, and are charged for.
        Assert.Equal(AiCallOutcome.InvalidResult, result.Outcome);
        Assert.Null(result.ResultJson);
        Assert.Equal(4, result.LiveSearches);
        Assert.Single(provider.Calls);

        var call = Assert.Single(application.Events(), written => written.Area == RouterArea);
        Assert.Equal("Warning", call.Level);
        Assert.Equal(4, call["LiveSearches"].GetInt32());
    }

    [Fact]
    public async Task A_call_that_got_no_answer_does_not_know_whether_a_search_ran()
    {
        var provider = new StandInSystem(_ => StandInSystem.Status(HttpStatusCode.BadRequest));
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var gateway = application.Services.GetRequiredService<IAiProviderGateway>();

        // What a provider answers a model that does not take its search together with a schema.
        var result = await gateway.CompleteAsync(
            new AiProviderCode("gemini"), ModelCalls.SearchingRequestTo("gemini"), ModelCalls.Key, TestContext.Current.CancellationToken);

        // Unknown, and not none, as the usage is.
        Assert.Equal(AiCallOutcome.Failed, result.Outcome);
        Assert.Null(result.LiveSearches);
        Assert.False(Assert.Single(application.Events(), written => written.Area == RouterArea).Has("LiveSearches"));
    }
}
