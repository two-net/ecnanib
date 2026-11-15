using System.Diagnostics;
using System.Net;
using Ecnanib.AiProviderGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.AiProviderGateway;

/// <summary>
/// The catalog is filled from each provider's own list of its models (FR-CAT-1): the gateway asks
/// the provider for the list, with a key, and hands on every model with what the provider says
/// about it. What a provider does not say is unknown, and is not guessed (FR-CAT-4). A list is
/// whole or it is none, and it is given up after 45 seconds, so that a sync of one provider
/// finishes within a minute (NFR-PRF-4). The key is in no log event.
/// </summary>
public class AiModelListTests
{
    private const string Key = ModelCalls.Key;

    private const string HttpArea = "Ecnanib.Platform.Adapters.Http.OutboundCallLogger";
    private const string ListArea = "Ecnanib.AiProviderGateway.Adapters.ModelListCall";

    private const string NotAList = "The answer of the provider is not the list of its models: {Reason}";

    // What each provider lists, in the form that its documentation gives, with more than is read.
    private const string ClaudeList =
        """
        {
          "data": [
            { "type": "model", "id": "claude-opus-5-5", "display_name": "Claude Opus 5.5", "created_at": "2026-09-21T00:00:00Z",
              "max_input_tokens": 1000000, "max_tokens": 64000, "lifecycle": "active", "line": "opus",
              "deprecated_at": null, "retires_at": null, "capabilities": { "structured_outputs": { "supported": true } } },
            { "type": "model", "id": "claude-haiku-4-5", "display_name": "Claude Haiku 4.5", "created_at": "2025-10-15T00:00:00Z",
              "max_input_tokens": 200000, "max_tokens": 64000, "lifecycle": "deprecated", "line": "haiku",
              "deprecated_at": "2026-08-01T12:30:00.123Z", "retires_at": "2027-02-01T00:00:00+02:00" }
          ],
          "first_id": "claude-opus-5-5", "has_more": false, "last_id": "claude-haiku-4-5"
        }
        """;

    private const string GeminiList =
        """
        {
          "models": [
            { "name": "models/gemini-3-pro", "baseModelId": "gemini-3-pro", "version": "3.0", "displayName": "Gemini 3 Pro",
              "description": "The most capable model.", "inputTokenLimit": 1048576, "outputTokenLimit": 65536,
              "supportedGenerationMethods": [ "generateContent", "countTokens" ], "thinking": true, "temperature": 1 },
            { "name": "models/gemini-embedding-001", "version": "001", "displayName": "Gemini Embedding 001",
              "inputTokenLimit": 2048, "outputTokenLimit": 1, "supportedGenerationMethods": [ "embedContent" ] }
          ]
        }
        """;

    private const string GptList =
        """
        {
          "object": "list",
          "data": [
            { "id": "gpt-5.5", "object": "model", "created": 1767225600, "owned_by": "openai", "shutdown_date": null },
            { "id": "gpt-4.1", "object": "model", "created": 1744675200, "owned_by": "openai", "shutdown_date": "2026-10-23" },
            { "id": "ft:gpt-4.1-mini:ada-labs:candles:9xYz", "object": "model", "created": 1750000000, "owned_by": "ada-labs" }
          ]
        }
        """;

    private const string GrokList =
        """
        {
          "object": "list",
          "data": [
            { "id": "grok-4.3", "object": "model", "created": 1760000000, "owned_by": "xai", "aliases": [ "grok-4.3-latest" ],
              "context_length": 256000, "prompt_text_token_price": 30000, "completion_text_token_price": 150000 },
            { "id": "grok-imagine-image", "object": "model", "created": 1755000000, "owned_by": "xai", "aliases": [],
              "context_length": null, "image_price": 7 }
          ]
        }
        """;

    // What Gemini answers a wrong key with, and what it answers something else that is wrong with.
    private const string GeminiWrongKey =
        """
        { "error": { "code": 400, "message": "API key not valid. Please pass a valid API key.", "status": "INVALID_ARGUMENT",
            "details": [ { "@type": "type.googleapis.com/google.rpc.ErrorInfo", "reason": "API_KEY_INVALID", "domain": "googleapis.com" } ] } }
        """;

    private const string GeminiWrongRequest =
        """{ "error": { "code": 400, "message": "Invalid page size.", "status": "INVALID_ARGUMENT" } }""";

    [Fact]
    public async Task Each_provider_is_asked_for_its_models_at_its_address_with_the_key_in_its_own_header()
    {
        var provider = new StandInSystem(call => StandInSystem.Json(HttpStatusCode.OK, ListOf(ModelCalls.ProviderOf(call))));
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var lists = application.Services.GetRequiredService<IAiModelLists>();

        Assert.Equal(ModelCalls.Providers, lists.Providers.Select(code => code.Value).Order());

        foreach (var code in ModelCalls.Providers)
        {
            var list = await lists.ListAsync(new AiProviderCode(code), Key, TestContext.Current.CancellationToken);

            Assert.Equal(AiModelListOutcome.Listed, list.Outcome);
        }

        // The addresses are those of the configuration file. Every call reads, and sends nothing.
        // A provider that lists in pages is asked for the largest page it gives.
        Assert.Equal(
            [
                "GET https://claude.example/v1/models?limit=1000",
                "GET https://gemini.example/v1beta/models?pageSize=1000",
                "GET https://gpt.example/v1/models",
                "GET https://grok.example/v1/models",
            ],
            provider.Calls.Select(call => $"{call.Method} {call.Address}"));
        Assert.All(provider.Calls, call => Assert.Null(call.Body));

        var (claude, gemini, gpt, grok) = (provider.Calls[0], provider.Calls[1], provider.Calls[2], provider.Calls[3]);
        Assert.Equal(Key, claude.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", claude.Headers["anthropic-version"]);
        Assert.Equal(Key, gemini.Headers["x-goog-api-key"]);
        Assert.Equal($"Bearer {Key}", gpt.Headers["Authorization"]);
        Assert.Equal($"Bearer {Key}", grok.Headers["Authorization"]);

        // The key is where the provider takes it, and nowhere else: not in an address.
        Assert.All(provider.Calls, call => Assert.DoesNotContain(Key, call.Address.ToString()));
    }

    [Fact]
    public async Task Claude_says_the_name_the_limits_and_the_dates_of_a_model()
    {
        var list = await ListedAsync("claude", ThatLists(ClaudeList));

        // A time is in UTC, whatever offset the provider wrote it with.
        Assert.Equal(AiModelListOutcome.Listed, list.Outcome);
        Assert.Equal(
            [
                new ListedAiModel(
                    "claude-opus-5-5",
                    "Claude Opus 5.5",
                    ContextTokens: 1_000_000,
                    OutputTokens: 64_000,
                    ReleasedAt: new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero),
                    DeprecatedAt: null,
                    RetiresAt: null),
                new ListedAiModel(
                    "claude-haiku-4-5",
                    "Claude Haiku 4.5",
                    ContextTokens: 200_000,
                    OutputTokens: 64_000,
                    ReleasedAt: new DateTimeOffset(2025, 10, 15, 0, 0, 0, TimeSpan.Zero),
                    DeprecatedAt: new DateTimeOffset(2026, 8, 1, 12, 30, 0, 123, TimeSpan.Zero),
                    RetiresAt: new DateTimeOffset(2027, 1, 31, 22, 0, 0, TimeSpan.Zero)),
            ],
            list.Models);
        Assert.All(list.Models, model => Assert.Equal(TimeSpan.Zero, model.RetiresAt?.Offset ?? TimeSpan.Zero));
    }

    [Fact]
    public async Task Gemini_says_the_name_and_the_limits_of_a_model_and_no_date()
    {
        var list = await ListedAsync("gemini", ThatLists(GeminiList));

        // The id is what the list names a model by, without the "models/" before it.
        Assert.Equal(AiModelListOutcome.Listed, list.Outcome);
        Assert.Equal(
            [
                new ListedAiModel("gemini-3-pro", "Gemini 3 Pro", 1_048_576, 65_536, null, null, null),
                new ListedAiModel("gemini-embedding-001", "Gemini Embedding 001", 2_048, 1, null, null, null),
            ],
            list.Models);
    }

    [Fact]
    public async Task GPT_says_when_a_model_was_created_and_when_it_is_shut_down_and_nothing_else()
    {
        var list = await ListedAsync("gpt", ThatLists(GptList));

        // A day alone is the start of that day, in UTC.
        Assert.Equal(AiModelListOutcome.Listed, list.Outcome);
        Assert.Equal(
            [
                new ListedAiModel("gpt-5.5", null, null, null, DateTimeOffset.FromUnixTimeSeconds(1_767_225_600), null, null),
                new ListedAiModel(
                    "gpt-4.1",
                    null,
                    null,
                    null,
                    DateTimeOffset.FromUnixTimeSeconds(1_744_675_200),
                    null,
                    new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero)),
            ],
            list.Models);
    }

    [Fact]
    public async Task A_model_that_an_account_of_GPT_trained_itself_is_not_listed()
    {
        var list = await ListedAsync("gpt", ThatLists(GptList));

        // It is listed to its account alone, and names the account. With a user's key, it would
        // otherwise be a row that every user reads.
        Assert.DoesNotContain(list.Models, model => model.Id.StartsWith("ft:", StringComparison.Ordinal));
        Assert.DoesNotContain(list.Models, model => model.Id.Contains("ada-labs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Grok_says_when_a_model_was_created_and_how_much_it_takes()
    {
        var list = await ListedAsync("grok", ThatLists(GrokList));

        Assert.Equal(AiModelListOutcome.Listed, list.Outcome);
        Assert.Equal(
            [
                new ListedAiModel("grok-4.3", null, 256_000, null, DateTimeOffset.FromUnixTimeSeconds(1_760_000_000), null, null),
                new ListedAiModel("grok-imagine-image", null, null, null, DateTimeOffset.FromUnixTimeSeconds(1_755_000_000), null, null),
            ],
            list.Models);
    }

    [Fact]
    public async Task What_a_provider_does_not_say_about_a_model_is_unknown()
    {
        // Left out, null, of another kind or another form than expected, a number of tokens that
        // is none, and a date at the start of 1970, which is how a provider writes one that it
        // does not know.
        var claude = await ListedAsync(
            "claude",
            ThatLists(
                """
                {
                  "data": [
                    { "id": "claude-a" },
                    { "id": "claude-b", "display_name": null, "created_at": null, "max_input_tokens": null, "max_tokens": null },
                    { "id": "claude-c", "display_name": 7, "created_at": "1970-01-01T00:00:00Z", "max_input_tokens": 0,
                      "max_tokens": -1, "deprecated_at": "soon", "retires_at": true },
                    { "id": "claude-d", "max_input_tokens": "200000", "max_tokens": 64000.5, "created_at": "" },
                    { "id": "claude-e", "created_at": "12:30", "deprecated_at": "2026-10", "retires_at": "Oct 23" }
                  ],
                  "has_more": false
                }
                """));
        var gpt = await ListedAsync("gpt", ThatLists("""{ "data": [ { "id": "gpt-a", "created": 0 }, { "id": "gpt-b", "created": "then" } ] }"""));

        // A part of a date is none either: the rest of it would be filled in from today.
        Assert.Equal(
            ["claude-a", "claude-b", "claude-c", "claude-d", "claude-e"],
            claude.Models.Select(model => model.Id));
        Assert.All(
            claude.Models.Concat(gpt.Models),
            model => Assert.Equal(new ListedAiModel(model.Id, null, null, null, null, null, null), model));
    }

    [Fact]
    public async Task A_list_that_comes_in_pages_is_read_to_its_end()
    {
        // Claude says that there is more, and the next page is asked for after the last model.
        var claude = new StandInSystem(call => StandInSystem.Json(
            HttpStatusCode.OK,
            call.RequestUri!.Query.Contains("after_id", StringComparison.Ordinal)
                ? """{ "data": [ { "id": "claude-b" }, { "id": "claude-c" } ], "has_more": false, "last_id": "claude-c" }"""
                : """{ "data": [ { "id": "claude-a" }, { "id": "claude-b" } ], "has_more": true, "last_id": "claude b&c" }"""));

        var listed = await ListedAsync("claude", claude);

        // A model that two pages name, because the list changed between them, is one model.
        Assert.Equal(AiModelListOutcome.Listed, listed.Outcome);
        Assert.Equal(["claude-a", "claude-b", "claude-c"], listed.Models.Select(model => model.Id));
        Assert.Equal(2, claude.Calls.Count);
        Assert.Equal([("limit", "1000"), ("after_id", "claude b&c")], claude.Calls[1].Query);

        // Gemini names the next page with a token, and leaves the token out of the last one.
        var gemini = new StandInSystem(call => StandInSystem.Json(
            HttpStatusCode.OK,
            call.RequestUri!.Query.Contains("pageToken", StringComparison.Ordinal)
                ? """{ "models": [ { "name": "models/gemini-b" } ] }"""
                : """{ "models": [ { "name": "models/gemini-a" } ], "nextPageToken": "page two/of=two" }"""));

        listed = await ListedAsync("gemini", gemini);

        Assert.Equal(["gemini-a", "gemini-b"], listed.Models.Select(model => model.Id));
        Assert.Equal([("pageSize", "1000"), ("pageToken", "page two/of=two")], gemini.Calls[1].Query);
        Assert.All(gemini.Calls.Concat(claude.Calls), call => Assert.Equal(call.Endpoint, call.Address.GetLeftPart(UriPartial.Path)));
    }

    [Theory]

    // No list where the provider has it.
    [InlineData("claude", """{ "models": [ { "name": "models/claude-a" } ] }""", "NotAList")]
    [InlineData("gpt", """{ "data": { "id": "gpt-a" } }""", "NotAList")]
    [InlineData("gemini", """{ "models": "gemini-a" }""", "NotAList")]

    // Not the provider's answer at all: the page of something in between.
    [InlineData("grok", """<html>Sign in to the network</html>""", "NotAList")]
    [InlineData("gpt", """[ { "id": "gpt-a" } ]""", "NotAList")]

    // A model without an identifier: nothing could call it, and the rest would be a part of a list.
    [InlineData("claude", """{ "data": [ { "id": "claude-a" }, { "display_name": "Claude B" } ], "has_more": false }""", "NotAList")]
    [InlineData("gpt", """{ "data": [ { "id": "" } ] }""", "NotAList")]
    [InlineData("grok", """{ "data": [ { "id": 7 } ] }""", "NotAList")]
    [InlineData("gemini", """{ "models": [ { "displayName": "Gemini A" } ] }""", "NotAList")]

    // More to come, and nothing that says where.
    [InlineData("claude", """{ "data": [ { "id": "claude-a" } ], "has_more": true }""", "NotAList")]

    // No model. A provider has models, so this is no catalog: every model of it would look gone.
    [InlineData("claude", """{ "data": [], "has_more": false }""", "NoModels")]
    [InlineData("gemini", "{}", "NoModels")]
    [InlineData("grok", """{ "data": [] }""", "NoModels")]
    [InlineData("gpt", """{ "data": [ { "id": "ft:gpt-4.1-mini:ada-labs::9xYz" } ] }""", "NoModels")]
    public async Task An_answer_that_is_not_the_list_of_a_provider_is_no_list(string code, string body, string reason)
    {
        var provider = ThatLists(body);
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var lists = application.Services.GetRequiredService<IAiModelLists>();

        var list = await lists.ListAsync(new AiProviderCode(code), Key, TestContext.Current.CancellationToken);

        Assert.Equal(AiModelListOutcome.Failed, list.Outcome);
        Assert.Empty(list.Models);

        // The status said that the call went well, so the gateway says why there is no list, in
        // a word of its own and with nothing of the answer.
        var why = Assert.Single(application.Events(), written => written.Area == ListArea);
        Assert.Equal(("Warning", NotAList, reason, code), (why.Level, why.Template, why.Text("Reason"), why.Text("Provider")));
    }

    [Fact]
    public async Task A_list_whose_pages_do_not_end_is_no_list()
    {
        // A page that names itself as the next one.
        var repeating = ThatLists("""{ "models": [ { "name": "models/gemini-a" } ], "nextPageToken": "again" }""");

        Assert.Equal(AiModelListOutcome.Failed, (await ListedAsync("gemini", repeating)).Outcome);
        Assert.Equal(2, repeating.Calls.Count);

        // Pages that each name another, without end.
        var page = 0;
        var endless = new StandInSystem(_ => StandInSystem.Json(
            HttpStatusCode.OK,
            $$"""{ "data": [ { "id": "claude-{{Interlocked.Increment(ref page)}}" } ], "has_more": true, "last_id": "claude-{{page}}" }"""));

        var list = await ListedAsync("claude", endless);

        Assert.Equal(AiModelListOutcome.Failed, list.Outcome);
        Assert.Empty(list.Models);
        Assert.Equal(50, endless.Calls.Count);
    }

    [Fact]
    public async Task A_list_of_which_a_page_fails_is_no_list()
    {
        var provider = new StandInSystem(call => call.RequestUri!.Query.Contains("after_id", StringComparison.Ordinal)
            ? StandInSystem.Json(HttpStatusCode.InternalServerError, """{ "type": "error", "error": { "type": "api_error" } }""")
            : StandInSystem.Json(
                HttpStatusCode.OK, """{ "data": [ { "id": "claude-a" } ], "has_more": true, "last_id": "claude-a" }"""));

        var list = await ListedAsync("claude", provider);

        // Not the models of the pages that came: those that the failed page holds would look gone.
        Assert.Equal(AiModelListOutcome.Failed, list.Outcome);
        Assert.Empty(list.Models);
        Assert.Equal(2, provider.Calls.Count);
    }

    [Theory]
    [InlineData("claude", HttpStatusCode.Unauthorized, """{ "type": "error", "error": { "type": "authentication_error" } }""")]
    [InlineData("gpt", HttpStatusCode.Unauthorized, """{ "error": { "code": "invalid_api_key" } }""")]
    [InlineData("grok", HttpStatusCode.BadRequest, """{ "code": "invalid-argument", "error": "Incorrect API key provided." }""")]
    [InlineData("grok", HttpStatusCode.Unauthorized, "{}")]
    [InlineData("gemini", HttpStatusCode.BadRequest, GeminiWrongKey)]
    [InlineData("gemini", HttpStatusCode.Unauthorized, "{}")]
    public async Task What_a_provider_answers_a_wrong_key_with_is_told_apart(string code, HttpStatusCode status, string body)
    {
        var list = await ListedAsync(code, new StandInSystem(_ => StandInSystem.Json(status, body)));

        Assert.Equal(AiModelListOutcome.AuthenticationFailed, list.Outcome);
        Assert.Empty(list.Models);
    }

    [Theory]

    // A request that this code got wrong, which is not the key being wrong.
    [InlineData("claude", HttpStatusCode.BadRequest, "{}")]
    [InlineData("gemini", HttpStatusCode.BadRequest, GeminiWrongRequest)]
    [InlineData("gemini", HttpStatusCode.BadRequest, "not json")]

    // A block of the service's address, a limit on requests, and a fault of the provider.
    [InlineData("gpt", HttpStatusCode.Forbidden, "{}")]
    [InlineData("grok", HttpStatusCode.TooManyRequests, "{}")]
    [InlineData("claude", HttpStatusCode.InternalServerError, "{}")]
    [InlineData("gemini", HttpStatusCode.ServiceUnavailable, "{}")]
    public async Task Any_other_answer_with_an_error_is_no_list(string code, HttpStatusCode status, string body)
    {
        var list = await ListedAsync(code, new StandInSystem(_ => StandInSystem.Json(status, body)));

        Assert.Equal(AiModelListOutcome.Failed, list.Outcome);
        Assert.Empty(list.Models);
    }

    [Fact]
    public async Task A_provider_that_does_not_answer_gives_no_list_and_asking_does_not_fail()
    {
        var provider = new StandInSystem(_ => throw new HttpRequestException("The connection was refused."));

        var list = await ListedAsync("gpt", provider);

        Assert.Equal(AiModelListOutcome.Failed, list.Outcome);
        Assert.Empty(list.Models);
    }

    [Fact]
    public async Task A_list_is_given_up_after_45_seconds_whatever_the_file_gives_a_call_to_a_model()
    {
        // A provider that never answers, and a clock that shows which limit the list was given.
        var clock = new TimerClock();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StandInSystem(async (_, givenUp) =>
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, givenUp);

            throw new UnreachableException();
        });
        using var application = provider.Behind(
            AiProviderGatewayModule.HttpClientName, services: services => services.AddSingleton<TimeProvider>(clock));
        var lists = application.Services.GetRequiredService<IAiModelLists>();

        var asking = lists.ListAsync(new AiProviderCode("gemini"), Key, TestContext.Current.CancellationToken);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Shorter than the minute that a sync of one provider may take (NFR-PRF-4), and not the
        // time that the file gives a call to a model of this provider.
        var limit = TimeSpan.FromSeconds(45);
        Assert.Contains(limit, clock.Timers);
        Assert.DoesNotContain(TimeSpan.FromSeconds(31), clock.Timers);
        Assert.False(asking.IsCompleted);

        // When it runs out, asking ends: as an outcome, and not as an error.
        clock.RunOut(limit);
        var list = await asking;

        Assert.Equal(AiModelListOutcome.TimedOut, list.Outcome);
        Assert.Empty(list.Models);
        Assert.Single(provider.Calls);
    }

    [Fact]
    public async Task A_list_that_its_caller_cancels_ends_as_cancelled()
    {
        using var cancelled = new CancellationTokenSource();
        var provider = new StandInSystem(_ =>
        {
            cancelled.Cancel();

            throw new OperationCanceledException(cancelled.Token);
        });
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var lists = application.Services.GetRequiredService<IAiModelLists>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lists.ListAsync(new AiProviderCode("claude"), Key, cancelled.Token));
    }

    [Fact]
    public async Task A_provider_without_a_list_is_refused_by_name()
    {
        var provider = ThatLists(ClaudeList);
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);
        var lists = application.Services.GetRequiredService<IAiModelLists>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => lists.ListAsync(new AiProviderCode("a-fifth-provider"), Key, TestContext.Current.CancellationToken));

        Assert.Contains("a-fifth-provider", error.Message);
        Assert.DoesNotContain(Key, error.Message);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task The_calls_are_logged_with_their_provider_and_the_key_is_in_no_log_event()
    {
        // With everything that can be logged: the debug level, at which bodies are written too.
        var provider = new StandInSystem(call => ModelCalls.ProviderOf(call) == "claude"
            ? StandInSystem.Json(HttpStatusCode.OK, ClaudeList)
            : StandInSystem.Json(HttpStatusCode.Unauthorized, """{ "error": { "code": "invalid_api_key" } }"""));
        using var application = provider.Behind(
            AiProviderGatewayModule.HttpClientName, LoggedApplication.Settings().With("Logging:MinimumLevel", "Debug"));
        var lists = application.Services.GetRequiredService<IAiModelLists>();

        await lists.ListAsync(new AiProviderCode("claude"), Key, TestContext.Current.CancellationToken);
        await lists.ListAsync(new AiProviderCode("gpt"), Key, TestContext.Current.CancellationToken);

        var calls = application.Events().Where(written => written.Area == HttpArea && !written.Has("BodyOf")).ToList();
        Assert.Equal(
            ["claude claude.example/v1/models Succeeded 200", "gpt gpt.example/v1/models Failed 401"],
            calls.Select(call =>
                $"{call.Text("Provider")} {call.Text("Host")}{call.Text("Path")} {call.Text("Outcome")} {call["StatusCode"].GetInt32()}"));

        var log = application.LogText();
        Assert.DoesNotContain(Key, log);
        Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Key)), log);
    }

    /// <summary>A provider that answers every call with this, as a success.</summary>
    private static StandInSystem ThatLists(string body) => new(_ => StandInSystem.Json(HttpStatusCode.OK, body));

    /// <summary>What a provider lists, by its code.</summary>
    private static string ListOf(string provider) => provider switch
    {
        "claude" => ClaudeList,
        "gemini" => GeminiList,
        "gpt" => GptList,
        _ => GrokList,
    };

    /// <summary>What the gateway hands on of the list of a provider that answers as the test says.</summary>
    private static async Task<AiModelList> ListedAsync(string code, StandInSystem provider)
    {
        using var application = provider.Behind(AiProviderGatewayModule.HttpClientName);

        return await application.Services.GetRequiredService<IAiModelLists>()
            .ListAsync(new AiProviderCode(code), Key, TestContext.Current.CancellationToken);
    }
}
