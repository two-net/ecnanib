using System.Globalization;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.Logging;
using static Ecnanib.Tests.Council.ContractSamples;

namespace Ecnanib.Tests.Council;

/// <summary>
/// FR-CYC-05: an answer that breaks its contract is sent back once, with what was wrong, as later turns after the
/// composed message. A second violation fails the role (FR-ROLE-06), and every billed call is kept for the trace.
/// </summary>
public sealed class ContractRetryTests
{
    private static readonly AiModelPrice Price = new(3m, 15m);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_answer_that_conforms_takes_one_call_with_the_roles_schema()
    {
        var provider = new ScriptedProvider(Technical);
        var logger = new RecordingLogger();

        var answer = await OutputContracts.CompleteAsync(provider.SendAsync, Request(AiRole.TechnicalAnalyst), ContextFor(AiRole.TechnicalAnalyst), logger, Cancellation);

        var sent = Assert.Single(provider.Sent);
        var schema = OutputContracts.SchemaFor(AiRole.TechnicalAnalyst, Market.Futures);
        Assert.Equal((schema.Name, schema.Schema.GetRawText()), (sent.Schema!.Name, sent.Schema.Schema.GetRawText()));
        Assert.IsType<TechnicalAssessment>(answer.Output);
        Assert.Empty(Assert.Single(answer.Attempts).Violations);
        Assert.Equal((new AiUsage(1000, 200), (decimal?)0.006m), (answer.Usage, answer.Cost));
        Assert.Empty(logger.Entries);
    }

    /// <summary>The Strategist's schema is its market's, so the provider holds it to FR-CYC-03's actions there.</summary>
    [Fact]
    public async Task The_schema_is_the_contexts_markets()
    {
        var provider = new ScriptedProvider(With(Proposal, "action", Quoted("BUY")));

        await OutputContracts.CompleteAsync(provider.SendAsync, Request(AiRole.Strategist), ContextFor(AiRole.Strategist, Market.Spot), new RecordingLogger(), Cancellation);

        Assert.Equal(
            OutputContracts.SchemaFor(AiRole.Strategist, Market.Spot).Schema.GetRawText(),
            Assert.Single(provider.Sent).Schema!.Schema.GetRawText());
    }

    [Fact]
    public async Task A_broken_answer_is_sent_back_once_with_its_violations()
    {
        var canary = CanaryCases.NewCanary();
        var broken = With(With(Technical, "confidence", "1.5"), "summary", Quoted(canary));
        var provider = new ScriptedProvider(broken, Technical);
        var logger = new RecordingLogger();
        var request = Request(AiRole.TechnicalAnalyst) with { LiveSearch = true };

        var answer = await OutputContracts.CompleteAsync(provider.SendAsync, request, ContextFor(AiRole.TechnicalAnalyst), logger, Cancellation);

        Assert.Equal(2, provider.Sent.Count);
        var (first, retry) = (provider.Sent[0], provider.Sent[1]);
        Assert.Equal(
            (first.Model, first.Role, first.Instructions, first.MaxOutputTokens, first.Schema, first.LiveSearch, first.Price),
            (retry.Model, retry.Role, retry.Instructions, retry.MaxOutputTokens, retry.Schema, retry.LiveSearch, retry.Price));

        Assert.Equal(3, retry.Messages.Count);
        Assert.Same(request.Messages[0], retry.Messages[0]);
        Assert.Equal((AiSpeaker.Assistant, broken), (retry.Messages[1].Speaker, Text(retry.Messages[1])));
        Assert.Equal(AiSpeaker.User, retry.Messages[2].Speaker);

        var retryText = Text(retry.Messages[2]);
        Assert.StartsWith("Your answer didn't follow your output contract:", retryText, StringComparison.Ordinal);
        Assert.Contains("- `$.confidence` must be from 0 to 1.\n", retryText, StringComparison.Ordinal);
        Assert.EndsWith("one JSON object and nothing else, with no text before or after it and no code fence.", retryText, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, retryText, StringComparison.Ordinal);

        Assert.IsType<TechnicalAssessment>(answer.Output);
        Assert.Equal(2, answer.Attempts.Count);
        Assert.Equal("$.confidence", Assert.Single(answer.Attempts[0].Violations).Path);
        Assert.Empty(answer.Attempts[1].Violations);
        Assert.Equal((new AiUsage(2000, 400), (decimal?)0.012m), (answer.Usage, answer.Cost));

        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(
            ("test-model", "TechnicalAnalyst", "1", "1", "$.confidence"),
            (warning.Values["Model"], warning.Values["Role"], warning.Values["ContractAttempt"], warning.Values["ViolationCount"], warning.Values["ViolationPaths"]));
        Assert.DoesNotContain(canary, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_broken_answer_fails_the_role_with_both_attempts()
    {
        var canary = CanaryCases.NewCanary();
        var provider = new ScriptedProvider(With(Technical, "trend", Quoted(canary)), With(Technical, "confidence", "-1"));
        var logger = new RecordingLogger();

        var exception = await Assert.ThrowsAsync<OutputContractException>(
            () => OutputContracts.CompleteAsync(provider.SendAsync, Request(AiRole.TechnicalAnalyst), ContextFor(AiRole.TechnicalAnalyst), logger, Cancellation));

        Assert.Equal(AiRole.TechnicalAnalyst, exception.Role);
        Assert.Equal("The TechnicalAnalyst's answer broke its output contract twice.", exception.Message);
        Assert.Null(exception.RetryFailure);
        Assert.Equal(["$.trend", "$.confidence"], exception.Attempts.Select(attempt => Assert.Single(attempt.Violations).Path));
        Assert.Equal((new AiUsage(2000, 400), (decimal?)0.012m), (exception.Usage, exception.Cost));
        Assert.Equal(["1", "2"], logger.Entries.Select(entry => entry.Values["ContractAttempt"]));
        Assert.All(logger.Entries, entry => Assert.DoesNotContain(canary, entry.Message, StringComparison.Ordinal));
    }

    /// <summary>The first call was billed, so a retry that fails at the provider can't lose it.</summary>
    [Fact]
    public async Task A_retry_that_fails_at_the_provider_keeps_the_first_attempt()
    {
        var failure = new AiException("Test", AiFailure.Overloaded, "Test is overloaded.") { Usage = new AiUsage(10, 5), Cost = 0.001m, Attempts = 3 };
        var provider = new ScriptedProvider(With(Technical, "confidence", "2")) { Failure = (2, failure) };

        var exception = await Assert.ThrowsAsync<OutputContractException>(
            () => OutputContracts.CompleteAsync(provider.SendAsync, Request(AiRole.TechnicalAnalyst), ContextFor(AiRole.TechnicalAnalyst), new RecordingLogger(), Cancellation));

        Assert.Same(failure, exception.InnerException);
        Assert.Same(failure, exception.RetryFailure);
        Assert.Equal("The TechnicalAnalyst's answer broke its output contract, and the retry failed at Test.", exception.Message);
        Assert.Equal("$.confidence", Assert.Single(Assert.Single(exception.Attempts).Violations).Path);
        Assert.Equal((new AiUsage(1010, 205), (decimal?)0.007m), (exception.Usage, exception.Cost));
    }

    [Fact]
    public async Task A_first_call_that_fails_at_the_provider_is_its_own_failure()
    {
        var failure = new AiException("Test", AiFailure.Refusal, "Test refused.");
        var provider = new ScriptedProvider() { Failure = (1, failure) };

        var thrown = await Assert.ThrowsAsync<AiException>(
            () => OutputContracts.CompleteAsync(provider.SendAsync, Request(AiRole.Strategist), ContextFor(AiRole.Strategist), new RecordingLogger(), Cancellation));

        Assert.Same(failure, thrown);
        Assert.Single(provider.Sent);
    }

    /// <summary>Providers refuse an empty turn, so a blank answer's violations join the user's message.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("  \n")]
    public async Task A_blank_answer_isnt_sent_back_as_a_turn(string blank)
    {
        var provider = new ScriptedProvider(blank, Reflection);
        var request = Request(AiRole.Reviewer);

        await OutputContracts.CompleteAsync(provider.SendAsync, request, ContextFor(AiRole.Reviewer), new RecordingLogger(), Cancellation);

        var retry = Assert.Single(provider.Sent[1].Messages);
        Assert.Equal(AiSpeaker.User, retry.Speaker);
        Assert.Equal(2, retry.Content.Count);
        Assert.Same(request.Messages[0].Content[0], retry.Content[0]);
        Assert.Contains("- Your answer is empty.", ((AiText)retry.Content[1]).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_truncated_answer_is_asked_to_be_briefer()
    {
        var provider = new ScriptedProvider(Technical[..40], Technical) { TruncatedFirst = true };

        await OutputContracts.CompleteAsync(provider.SendAsync, Request(AiRole.TechnicalAnalyst), ContextFor(AiRole.TechnicalAnalyst), new RecordingLogger(), Cancellation);

        Assert.Contains("- Your answer was cut off at the output limit, so it's incomplete: answer more briefly.", Text(provider.Sent[1].Messages[2]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_stops_it()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var provider = new ScriptedProvider(Technical);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => OutputContracts.CompleteAsync(provider.SendAsync, Request(AiRole.TechnicalAnalyst), ContextFor(AiRole.TechnicalAnalyst), new RecordingLogger(), cancelled.Token));
    }

    private static AiRequest Request(AiRole role) =>
        new("test-model", role, "You are on the council.", [AiMessage.User("The runtime context.")], 2000) { Price = Price };

    private static string Text(AiMessage message) => ((AiText)Assert.Single(message.Content)).Text;

    /// <summary>A provider that answers each call with the next of its answers, billing each at 1000 and 200 tokens.</summary>
    private sealed class ScriptedProvider(params string[] answers)
    {
        public List<AiRequest> Sent { get; } = [];

        /// <summary>Which call throws, counting from 1, and what.</summary>
        public (int Call, AiException Exception)? Failure { get; init; }

        public bool TruncatedFirst { get; init; }

        public Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Add(request);

            if (Failure is { } failure && failure.Call == Sent.Count)
            {
                throw failure.Exception;
            }

            var usage = new AiUsage(1000, 200);

            return Task.FromResult(new AiResponse(
                answers[Sent.Count - 1],
                usage,
                Price.CostOf(usage),
                Truncated: TruncatedFirst && Sent.Count == 1,
                Attempts: 1,
                TimeSpan.FromSeconds(1)));
        }
    }

    /// <summary>Every event, with its message and its values as text.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Dictionary<string, string?> Values)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];

            Entries.Add((
                logLevel,
                formatter(state, exception),
                values.ToDictionary(pair => pair.Key, pair => Convert.ToString(pair.Value, CultureInfo.InvariantCulture))));
        }
    }
}
