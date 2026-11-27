using System.Net;
using Ecnanib.AiProviderGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.AiProviderGateway;

/// <summary>
/// Repeated failures for a provider or a key trip a breaker that pauses calls and retries later
/// (NFR-REL-4). The gateway counts the calls to each provider, of all users, and the calls with
/// each key: after as many failures in a row as the configuration file says, it makes no call
/// for a while, and then makes them again. A provider that is paused leaves the others as they
/// are, and a key that is paused leaves the other keys as they are.
/// </summary>
public class AiCallBreakerTests
{
    private const string BreakerArea = "Ecnanib.AiProviderGateway.Adapters.AiCallBreakers";

    // What the file of the tests says: the calls to a provider pause after 6 failures in a row,
    // for 40 seconds at first and 600 at the most, and the calls with a key after 4 refusals in a
    // row, for 50 seconds at first and 700 at the most.
    private const int ProviderFailures = 6;
    private const int KeyFailures = 4;
    private static readonly TimeSpan ProviderPause = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan KeyPause = TimeSpan.FromSeconds(50);

    private const string OtherKey = "another-users-key-9876543210-zyxwvutsrq-Lm7Q";

    private static readonly DateTimeOffset Morning = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    private static readonly AiProviderCode Claude = new("claude");
    private static readonly AiProviderCode Gemini = new("gemini");

    [Fact]
    public async Task After_as_many_failures_in_a_row_as_the_file_says_no_call_is_made_to_the_provider()
    {
        using var world = new World(_ => StandInSystem.Status(HttpStatusCode.ServiceUnavailable));

        for (var call = 1; call <= ProviderFailures; call++)
        {
            Assert.Equal(AiCallOutcome.Failed, (await world.AskAsync(Claude)).Outcome);
        }

        // The next call is not made: nothing is sent, nothing is used, and no time is taken.
        var paused = await world.AskAsync(Claude);

        Assert.Equal(new AiCallResult(AiCallOutcome.ProviderPaused, null, null, TimeSpan.Zero), paused);
        Assert.Equal(ProviderFailures, world.Provider.Calls.Count);

        // Whoever calls: the pause is of the provider, and not of a key.
        Assert.Equal(AiCallOutcome.ProviderPaused, (await world.AskAsync(Claude, OtherKey)).Outcome);
        Assert.Equal(ProviderFailures, world.Provider.Calls.Count);

        Assert.Equal(
            new AiCallPause(Claude, ProviderFailures, Trips: 1, Morning + ProviderPause, KeysInTrouble: 0, KeysPaused: 0),
            world.PauseOf(Claude));
    }

    [Fact]
    public async Task A_provider_that_is_paused_leaves_the_others_as_they_are()
    {
        // Claude is down, and the others answer.
        using var world = new World(request => ModelCalls.ProviderOf(request) == "claude"
            ? StandInSystem.Status(HttpStatusCode.ServiceUnavailable)
            : StandInSystem.Json(HttpStatusCode.OK, ModelCalls.Finished(ModelCalls.ProviderOf(request), ModelCalls.Result)));

        await world.FailAsync(Claude, ProviderFailures);
        Assert.Equal(AiCallOutcome.ProviderPaused, (await world.AskAsync(Claude)).Outcome);

        foreach (var other in ModelCalls.Providers.Where(code => code != "claude"))
        {
            Assert.Equal(AiCallOutcome.Succeeded, (await world.AskAsync(new AiProviderCode(other))).Outcome);
        }

        Assert.All(
            world.Pauses.Pauses().Where(pause => pause.Provider != Claude),
            pause => Assert.Equal(new AiCallPause(pause.Provider, 0, 0, null, 0, 0), pause));
    }

    [Fact]
    public async Task When_the_pause_is_over_the_calls_are_made_again_and_an_answer_ends_it()
    {
        var down = true;
        using var world = new World(request => down
            ? StandInSystem.Status(HttpStatusCode.ServiceUnavailable)
            : StandInSystem.Json(HttpStatusCode.OK, ModelCalls.Finished(ModelCalls.ProviderOf(request), ModelCalls.Result)));

        await world.FailAsync(Claude, ProviderFailures);

        // Until the last moment of the pause no call is made.
        world.Clock.Set(Morning + ProviderPause - TimeSpan.FromSeconds(1));
        Assert.Equal(AiCallOutcome.ProviderPaused, (await world.AskAsync(Claude)).Outcome);

        // Then it is, and the provider answers again.
        down = false;
        world.Clock.Set(Morning + ProviderPause);

        Assert.Equal(AiCallOutcome.Succeeded, (await world.AskAsync(Claude)).Outcome);
        Assert.Equal(ProviderFailures + 1, world.Provider.Calls.Count);
        Assert.Equal(new AiCallPause(Claude, 0, 0, null, 0, 0), world.PauseOf(Claude));

        // What was counted is gone with the pause: it takes as many failures as before to pause again.
        down = true;
        await world.FailAsync(Claude, ProviderFailures - 1);
        Assert.Null(world.PauseOf(Claude).PausedUntil);

        // The pause is said once when it begins, as a warning, and its end once, and neither with a key.
        var said = world.Application.Events().Where(written => written.Area == BreakerArea).ToList();
        Assert.Equal(["Warning", "Information"], said.Select(written => written.Level));
        Assert.All(said, written => Assert.Equal("claude", written.Text("Provider")));
        Assert.Equal(("6", "1"), (said[0]["Failures"].ToString(), said[0]["Trips"].ToString()));
    }

    [Fact]
    public async Task The_first_failure_after_a_pause_begins_one_that_is_twice_as_long_up_to_the_longest()
    {
        using var world = new World(_ => StandInSystem.Status(HttpStatusCode.ServiceUnavailable));

        await world.FailAsync(Claude, ProviderFailures);
        var now = Morning;

        // 40 seconds, then 80, 160, and 320, and then the 600 that the file says is the longest.
        foreach (var (seconds, trips) in new[] { (80, 2), (160, 3), (320, 4), (600, 5), (600, 6) })
        {
            now = world.PauseOf(Claude).PausedUntil!.Value;
            world.Clock.Set(now);
            var before = world.Provider.Calls.Count;

            // One call is made, fails, and that is enough.
            Assert.Equal(AiCallOutcome.Failed, (await world.AskAsync(Claude)).Outcome);
            Assert.Equal(AiCallOutcome.ProviderPaused, (await world.AskAsync(Claude)).Outcome);

            Assert.Equal(before + 1, world.Provider.Calls.Count);
            Assert.Equal((trips, now + TimeSpan.FromSeconds(seconds)), (world.PauseOf(Claude).Trips, world.PauseOf(Claude).PausedUntil));
        }
    }

    [Fact]
    public async Task An_answer_between_failures_ends_what_was_counted()
    {
        var answers = true;
        using var world = new World(request => answers
            ? StandInSystem.Json(HttpStatusCode.OK, ModelCalls.Finished(ModelCalls.ProviderOf(request), ModelCalls.Result))
            : StandInSystem.Status(HttpStatusCode.BadGateway));

        answers = false;
        await world.FailAsync(Claude, ProviderFailures - 1);

        answers = true;
        Assert.Equal(AiCallOutcome.Succeeded, (await world.AskAsync(Claude)).Outcome);

        // Five failures, an answer, and five more: never six in a row.
        answers = false;
        await world.FailAsync(Claude, ProviderFailures - 1);

        Assert.Equal(AiCallOutcome.Failed, (await world.AskAsync(Claude)).Outcome);
        Assert.Equal(1, world.PauseOf(Claude).Trips);
    }

    public static TheoryData<string> FaultsOfAProvider =>
        ["no connection", "broken answer", "too long", "500", "502", "503", "504", "529", "408", "not its answer"];

    [Theory]
    [MemberData(nameof(FaultsOfAProvider))]
    public async Task No_answer_and_a_fault_of_the_provider_count_for_the_provider_and_not_for_the_key(string how)
    {
        using var world = new World(_ => how switch
        {
            "no connection" => throw new HttpRequestException("The connection was refused."),
            "broken answer" => throw new IOException("The connection was reset."),
            "too long" => throw new TaskCanceledException("The provider took too long."),
            "not its answer" => StandInSystem.Json(HttpStatusCode.OK, "<html>Maintenance</html>"),
            _ => StandInSystem.Status((HttpStatusCode)int.Parse(how)),
        });

        for (var call = 1; call <= ProviderFailures; call++)
        {
            var result = await world.AskAsync(Claude);

            Assert.Contains(result.Outcome, new[] { AiCallOutcome.Failed, AiCallOutcome.TimedOut });
            Assert.Null(result.KeyTrips);
        }

        Assert.Equal(AiCallOutcome.ProviderPaused, (await world.AskAsync(Claude)).Outcome);
        Assert.Equal((1, 0), (world.PauseOf(Claude).Trips, world.PauseOf(Claude).KeysInTrouble));
    }

    [Theory]

    // A limit on the requests of the key, an account that has run out, a key that may not do
    // this, and a request that the provider would not take.
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_refusal_of_the_provider_counts_for_the_key_and_not_for_the_provider(HttpStatusCode status)
    {
        using var world = new World(_ => StandInSystem.Json(status, "{}"));

        for (var call = 1; call <= KeyFailures; call++)
        {
            var result = await world.AskAsync(Claude);

            // The call that begins the pause says so, and that it is the first in a row.
            Assert.Equal(AiCallOutcome.Failed, result.Outcome);
            Assert.Equal(call == KeyFailures ? 1 : null, result.KeyTrips);
        }

        // The calls with the key are paused. A call that is not made begins no pause, and says none.
        var paused = await world.AskAsync(Claude);

        Assert.Equal(new AiCallResult(AiCallOutcome.KeyPaused, null, null, TimeSpan.Zero), paused);
        Assert.Equal(KeyFailures, world.Provider.Calls.Count);

        // The provider answers: nothing is counted of it, and nothing of it is paused.
        Assert.Equal(
            new AiCallPause(Claude, Failures: 0, Trips: 0, PausedUntil: null, KeysInTrouble: 1, KeysPaused: 1), world.PauseOf(Claude));
    }

    [Fact]
    public async Task A_key_that_is_paused_leaves_the_other_keys_and_the_other_providers_as_they_are()
    {
        // The provider refuses the calls with one key, and takes those with any other.
        using var world = new World(request => request.Headers.TryGetValues("x-api-key", out var keys) && keys.Contains(ModelCalls.Key)
            ? StandInSystem.Json(HttpStatusCode.TooManyRequests, "{}")
            : StandInSystem.Json(HttpStatusCode.OK, ModelCalls.Finished(ModelCalls.ProviderOf(request), ModelCalls.Result)));

        await world.FailAsync(Claude, KeyFailures);

        Assert.Equal(AiCallOutcome.KeyPaused, (await world.AskAsync(Claude)).Outcome);
        Assert.Equal(AiCallOutcome.Succeeded, (await world.AskAsync(Claude, OtherKey)).Outcome);

        // The same text is another key at another provider: a key is of one provider.
        Assert.Equal(AiCallOutcome.Succeeded, (await world.AskAsync(Gemini)).Outcome);
    }

    [Fact]
    public async Task A_key_that_is_refused_again_after_its_pause_says_how_many_pauses_it_has_had_in_a_row()
    {
        var refuses = true;
        using var world = new World(request => refuses
            ? StandInSystem.Json(HttpStatusCode.TooManyRequests, "{}")
            : StandInSystem.Json(HttpStatusCode.OK, ModelCalls.Finished(ModelCalls.ProviderOf(request), ModelCalls.Result)));

        await world.FailAsync(Claude, KeyFailures);

        // 50 seconds, then 100: the first call after each is refused, and begins the next.
        world.Clock.Set(Morning + KeyPause);
        Assert.Equal(2, (await world.AskAsync(Claude)).KeyTrips);
        Assert.Null((await world.AskAsync(Claude)).KeyTrips);

        world.Clock.Set(Morning + KeyPause + (2 * KeyPause));
        Assert.Equal(3, (await world.AskAsync(Claude)).KeyTrips);
        Assert.Equal(1, world.PauseOf(Claude).KeysInTrouble);

        // An answer ends it, and the key is no longer known for anything.
        refuses = false;
        world.Clock.Set(Morning.AddHours(1));

        Assert.Equal(new AiCallResult(AiCallOutcome.Succeeded, ModelCalls.Result, new AiTokenUsage(100, 50), default, 0),
            (await world.AskAsync(Claude)) with { Latency = default });
        Assert.Equal(new AiCallPause(Claude, 0, 0, null, 0, 0), world.PauseOf(Claude));

        refuses = true;
        Assert.All(await world.FailAsync(Claude, KeyFailures - 1), result => Assert.Null(result.KeyTrips));
    }

    [Fact]
    public async Task A_refusal_says_that_the_provider_answers_and_ends_what_was_counted_of_it()
    {
        var status = HttpStatusCode.ServiceUnavailable;
        using var world = new World(_ => StandInSystem.Json(status, "{}"));

        await world.FailAsync(Claude, ProviderFailures - 1);

        // The provider answers, if only to refuse. So does one that does not know the key.
        status = HttpStatusCode.TooManyRequests;
        await world.FailAsync(Claude, 1);

        status = HttpStatusCode.ServiceUnavailable;
        await world.FailAsync(Claude, ProviderFailures - 1);

        status = HttpStatusCode.Unauthorized;
        Assert.Equal(AiCallOutcome.AuthenticationFailed, (await world.AskAsync(Claude)).Outcome);

        status = HttpStatusCode.ServiceUnavailable;
        await world.FailAsync(Claude, ProviderFailures - 1);

        Assert.Equal((0, null), (world.PauseOf(Claude).Trips, world.PauseOf(Claude).PausedUntil));
    }

    public static TheoryData<string> AnswersWithAHandlingOfTheirOwn => ["wrong key", "no such model", "no result"];

    [Theory]
    [MemberData(nameof(AnswersWithAHandlingOfTheirOwn))]
    public async Task A_key_that_is_not_known_a_model_that_is_not_there_and_an_answer_without_a_result_pause_nothing(string how)
    {
        using var world = new World(_ => how switch
        {
            "wrong key" => StandInSystem.Json(
                HttpStatusCode.Unauthorized, """{ "type": "error", "error": { "type": "authentication_error", "message": "invalid x-api-key" } }"""),
            "no such model" => StandInSystem.Json(
                HttpStatusCode.NotFound, """{ "type": "error", "error": { "type": "not_found_error", "message": "model: claude-test-model" } }"""),
            _ => StandInSystem.Json(HttpStatusCode.OK, ModelCalls.Claude(string.Empty, stopReason: "refusal")),
        });

        // Each has what follows from it elsewhere: the key is checked again, the user is turned
        // to, or the call is made once more. None says that the provider or the key fails.
        var results = await world.FailAsync(Claude, 3 * ProviderFailures);

        Assert.DoesNotContain(results, result => result.Outcome is AiCallOutcome.ProviderPaused or AiCallOutcome.KeyPaused);
        Assert.Equal(3 * ProviderFailures, world.Provider.Calls.Count);
        Assert.Equal(new AiCallPause(Claude, 0, 0, null, 0, 0), world.PauseOf(Claude));
    }

    [Fact]
    public async Task The_failures_of_calls_that_were_under_way_when_a_pause_began_begin_no_other()
    {
        // Eight calls are under way at once when the provider fails them all.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var world = new World(async (_, _) =>
        {
            await release.Task;

            return StandInSystem.Status(HttpStatusCode.ServiceUnavailable);
        });

        var calls = Enumerable.Range(0, ProviderFailures + 2).Select(_ => world.AskAsync(Claude)).ToList();
        await Wait.UntilAsync(() => world.Provider.Calls.Count == calls.Count, "the calls to reach the provider");

        release.SetResult();
        var results = await Task.WhenAll(calls);

        // Each was made, and failed. The pause is the first, and no longer for the two that
        // failed after it had begun: they say nothing that the six before them did not.
        Assert.All(results, result => Assert.Equal(AiCallOutcome.Failed, result.Outcome));
        Assert.Equal((1, Morning + ProviderPause), (world.PauseOf(Claude).Trips, world.PauseOf(Claude).PausedUntil));
    }

    [Fact]
    public async Task A_call_that_its_caller_cancels_counts_as_nothing()
    {
        using var cancelled = new CancellationTokenSource();
        var cancels = true;
        using var world = new World(_ =>
        {
            if (cancels)
            {
                throw new OperationCanceledException(cancelled.Token);
            }

            return StandInSystem.Status(HttpStatusCode.ServiceUnavailable);
        });

        await world.FailAsync(Claude, ProviderFailures - 1);

        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => world.Gateway.CompleteAsync(
            Claude, ModelCalls.RequestTo("claude"), ModelCalls.Key, cancelled.Token));

        // Five failures in a row are counted, and not six.
        Assert.Equal((ProviderFailures - 1, 0), (world.PauseOf(Claude).Failures, world.PauseOf(Claude).Trips));

        cancels = false;
        await world.FailAsync(Claude, 1);
        Assert.Equal(1, world.PauseOf(Claude).Trips);
    }

    [Fact]
    public async Task The_check_of_a_key_and_the_list_of_the_models_are_asked_for_while_the_calls_are_paused()
    {
        using var world = new World(_ => StandInSystem.Status(HttpStatusCode.ServiceUnavailable));

        await world.FailAsync(Claude, ProviderFailures);
        Assert.Equal(AiCallOutcome.ProviderPaused, (await world.AskAsync(Claude)).Outcome);

        // Whether the provider takes a key is how a user sees that it is right, and the list of
        // its models is what the catalog is kept from: each is asked for, and neither counts.
        var before = world.Provider.Calls.Count;
        var services = world.Application.Services;

        await services.GetRequiredService<IAiKeyChecks>().CheckAsync(Claude, ModelCalls.Key, TestContext.Current.CancellationToken);
        await services.GetRequiredService<IAiModelLists>().ListAsync(Claude, ModelCalls.Key, TestContext.Current.CancellationToken);

        Assert.Equal(before + 2, world.Provider.Calls.Count);
        Assert.Equal((ProviderFailures, 1), (world.PauseOf(Claude).Failures, world.PauseOf(Claude).Trips));
    }

    [Fact]
    public async Task Nothing_of_a_key_is_in_what_is_said_of_a_pause()
    {
        using var world = new World(_ => StandInSystem.Json(HttpStatusCode.TooManyRequests, "{}"));

        await world.FailAsync(Claude, KeyFailures);
        await world.FailAsync(Claude, KeyFailures, OtherKey);

        // Two keys are paused, and each pause is said with how many keys of the provider are in trouble.
        var said = world.Application.Events().Where(written => written.Area == BreakerArea).ToList();

        Assert.Equal(["Warning", "Warning"], said.Select(written => written.Level));
        Assert.Equal(["1", "2"], said.Select(written => written["KeysInTrouble"].ToString()));
        Assert.Equal(2, world.PauseOf(Claude).KeysPaused);

        // Whose key it is, is in the scope of the caller. The key is nowhere, in no form.
        var log = world.Application.LogText();

        Assert.DoesNotContain(ModelCalls.Key, log);
        Assert.DoesNotContain(OtherKey, log);
        Assert.DoesNotContain("Kx4P", log);
        Assert.DoesNotContain("Lm7Q", log);
    }

    [Fact]
    public async Task The_providers_of_the_file_are_listed_in_the_order_of_their_codes()
    {
        using var world = new World(_ => StandInSystem.Status(HttpStatusCode.ServiceUnavailable));

        Assert.Equal(["claude", "gemini", "gpt", "grok"], world.Pauses.Pauses().Select(pause => pause.Provider.Value));
        Assert.All(world.Pauses.Pauses(), pause => Assert.Equal(new AiCallPause(pause.Provider, 0, 0, null, 0, 0), pause));

        // What is counted before a pause is there to see as well.
        await world.FailAsync(Gemini, 2);

        Assert.Equal(new AiCallPause(Gemini, Failures: 2, Trips: 0, PausedUntil: null, KeysInTrouble: 0, KeysPaused: 0), world.PauseOf(Gemini));
    }

    /// <summary>The gateway of an application, with a provider of the test's behind it and a clock that the test moves.</summary>
    private sealed class World : IDisposable
    {
        public World(Func<HttpRequestMessage, HttpResponseMessage> answer)
            : this((request, _) => Task.FromResult(answer(request)))
        {
        }

        public World(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
        {
            Provider = new StandInSystem(answer);
            Application = Provider.Behind(
                AiProviderGatewayModule.HttpClientName, services: services => services.AddSingleton<TimeProvider>(Clock));
            Gateway = Application.Services.GetRequiredService<IAiProviderGateway>();
            Pauses = Application.Services.GetRequiredService<IAiCallPauses>();
        }

        public TestClock Clock { get; } = new(Morning);

        public StandInSystem Provider { get; }

        public LoggedApplication Application { get; }

        public IAiProviderGateway Gateway { get; }

        public IAiCallPauses Pauses { get; }

        public AiCallPause PauseOf(AiProviderCode provider) => Pauses.Pauses().Single(pause => pause.Provider == provider);

        public Task<AiCallResult> AskAsync(AiProviderCode provider, string key = ModelCalls.Key) =>
            Gateway.CompleteAsync(provider, ModelCalls.RequestTo(provider.Value), key, TestContext.Current.CancellationToken);

        /// <summary>Makes as many calls as it says, one after the other. None of them may be held back by a pause.</summary>
        public async Task<IReadOnlyList<AiCallResult>> FailAsync(AiProviderCode provider, int calls, string key = ModelCalls.Key)
        {
            var results = new List<AiCallResult>();

            for (var call = 0; call < calls; call++)
            {
                var result = await AskAsync(provider, key);

                Assert.DoesNotContain(result.Outcome, new[] { AiCallOutcome.ProviderPaused, AiCallOutcome.KeyPaused });
                results.Add(result);
            }

            return results;
        }

        public void Dispose() => Application.Dispose();
    }
}
