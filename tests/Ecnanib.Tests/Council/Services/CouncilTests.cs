using System.Text.Json;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Council.Configuration;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Tests.Support;

namespace Ecnanib.Tests.Council.Services;

/// <summary>The sequential council over faked providers: FR-AI-02/03/05/06/07/12/13/14 and FR-SYNC-04 without a network or a database.</summary>
public class CouncilTests
{
    private static CancellationToken Ct => CouncilTestHost.Ct;

    [Fact]
    public async Task FourRolesRunInOrderAndTheDecisionRecordsEveryTurn()
    {
        var ai = CouncilTestHost.Answering(CouncilTestHost.FinalJson("open_long", "limit", 100m, 96m, [108m, 112m], risk: 1m, rationale: "Clean pullback."));
        var council = CouncilTestHost.Build(ai);

        var result = await council.DeliberateAsync(CouncilTestHost.Context(), Ct);

        Assert.Null(result.Failure);
        Assert.Null(result.Fault);
        var decision = Assert.IsType<Decision>(result.Decision);
        Assert.Equal(DecisionAction.OpenLong, decision.FinalAction);
        Assert.Equal(OrderType.Limit, decision.EntryType);
        Assert.Equal(100m, decision.EntryPrice);
        Assert.Equal(96m, decision.StopPrice);
        Assert.Equal([108m, 112m], decision.Targets!.Value.EnumerateArray().Select(t => t.GetDecimal()));
        Assert.Equal(1m, decision.RiskPercent);
        Assert.Equal(0.7m, decision.Confidence);
        Assert.Equal("Clean pullback.", decision.Rationale);
        Assert.Null(decision.Size);
        Assert.Null(decision.TradeId);
        Assert.Null(decision.PartialFraction);
        Assert.Equal(CouncilTestHost.Now, decision.CreatedAt);
        Assert.All(decision.LimitsCheck!.Value.EnumerateArray(), check => Assert.Equal("pass", check.GetProperty("status").GetString()));

        Assert.Equal(CouncilRoles.Order, decision.RoleOutputs.Select(r => r.Role));
        Assert.Equal([1, 2, 3, 4], decision.RoleOutputs.Select(r => r.Sequence));
        Assert.Equal([AiProvider.Gpt, AiProvider.Grok, AiProvider.Gemini, AiProvider.Claude], decision.RoleOutputs.Select(r => r.Provider));
        Assert.All(decision.RoleOutputs, r =>
        {
            Assert.Equal(RoleOutputStatus.Succeeded, r.Status);
            Assert.Equal(CouncilTestHost.Model(r.Provider), r.Model);
            Assert.Equal(10, r.InputTokens);
            Assert.Equal(5, r.OutputTokens);
            Assert.Equal(100, r.LatencyMs);
            Assert.NotNull(r.ProposedAction);
            Assert.NotNull(r.Confidence);
        });
        Assert.Equal(40, decision.TotalInputTokens);
        Assert.Equal(20, decision.TotalOutputTokens);
        Assert.Equal(400, decision.TotalLatencyMs);

        Assert.Equal(CouncilRoles.Order, decision.Transcripts.Select(t => t.Role));
        Assert.All(decision.Transcripts, t =>
        {
            Assert.Equal(CouncilTestHost.Now, t.CreatedAt);
            Assert.Contains("## Agent configuration", t.InputSummary);
            Assert.Equal("Succeeded", t.Output.GetProperty("status").GetString());
            Assert.Equal(1, t.Output.GetProperty("attempts").GetInt32());
            Assert.Equal(JsonValueKind.Object, t.Output.GetProperty("document").ValueKind);
        });
        Assert.Equal("Clean pullback.", decision.Transcripts.Single(t => t.Role == CouncilRole.RiskManager).Output.GetProperty("document").GetProperty("rationale").GetString());

        Assert.Equal(["MarketAnalyst", "SentimentFlow", "CrossMarketContext", "RiskManager"], ai.Completions.Select(c => c.Request.Purpose));
        Assert.All(ai.Completions, c =>
        {
            Assert.NotNull(c.Request.OutputSchema);
            Assert.Equal(8192, c.Request.MaxOutputTokens);
            Assert.Equal(CouncilTestHost.Model(c.Key.Provider), c.Request.Model);
            Assert.Contains("You are the", c.Request.SystemPrompt);
        });
        Assert.Contains("You are the first role.", ai.Completions[0].Request.UserPrompt);
        var riskManagerPrompt = ai.Completions[3].Request.UserPrompt;
        Assert.Contains("### Market Analyst", riskManagerPrompt);
        Assert.Contains("### Cross-Market Context", riskManagerPrompt);
        Assert.Contains("\"proposed_action\":\"no_trade\"", riskManagerPrompt);
        Assert.Contains("decide open_long with", riskManagerPrompt);
    }

    [Fact]
    public async Task EveryTurnIsPublishedForTheLiveViewInOrder()
    {
        var agent = CouncilTestHost.NewAgent(configure: a => a.CouncilAssignments.Single(c => c.Role == CouncilRole.SentimentFlow).Enabled = false);
        var events = new FakeAgentEvents();
        var council = CouncilTestHost.Build(CouncilTestHost.Answering(), events: events);

        await council.DeliberateAsync(CouncilTestHost.Context(agent), Ct);

        // FR-UI-03 b: a role that calls a model is announced before and after its turn; a role that runs no model only after.
        var published = events.Published;
        Assert.All(published, e =>
        {
            Assert.Equal(CouncilTestHost.AgentId, e.AgentId);
            Assert.Equal(CouncilTestHost.UserId, e.UserId);
            Assert.Equal(CouncilTestHost.Now, e.At);
        });
        Assert.Equal(
        [
            ("started", CouncilRole.MarketAnalyst, 1, (RoleOutputStatus?)null),
            ("finished", CouncilRole.MarketAnalyst, 1, RoleOutputStatus.Succeeded),
            ("finished", CouncilRole.SentimentFlow, 2, RoleOutputStatus.Disabled),
            ("started", CouncilRole.CrossMarketContext, 3, null),
            ("finished", CouncilRole.CrossMarketContext, 3, RoleOutputStatus.Succeeded),
            ("started", CouncilRole.RiskManager, 4, null),
            ("finished", CouncilRole.RiskManager, 4, RoleOutputStatus.Succeeded),
        ], published.Select(e => e switch
        {
            CouncilRoleStarted s => ("started", s.Role, s.Sequence, (RoleOutputStatus?)null),
            CouncilRoleFinished f => ("finished", f.Role, f.Sequence, f.Status),
            _ => throw new InvalidOperationException(e.GetType().Name),
        }));
        Assert.All(published.OfType<CouncilRoleStarted>(), s => Assert.Equal(CouncilTestHost.Context().Evaluation.Id, s.EvaluationId));
    }

    [Fact]
    public async Task ADisabledRoleIsRecordedWithoutACall()
    {
        var agent = CouncilTestHost.NewAgent(configure: a => a.CouncilAssignments.Single(c => c.Role == CouncilRole.SentimentFlow).Enabled = false);
        var ai = CouncilTestHost.Answering();
        var council = CouncilTestHost.Build(ai);

        var result = await council.DeliberateAsync(CouncilTestHost.Context(agent), Ct);

        Assert.Null(result.Failure);
        var sentiment = result.Decision!.RoleOutputs.Single(r => r.Role == CouncilRole.SentimentFlow);
        Assert.Equal(RoleOutputStatus.Disabled, sentiment.Status);
        Assert.Equal(0, sentiment.InputTokens);
        Assert.DoesNotContain(result.Decision.Transcripts, t => t.Role == CouncilRole.SentimentFlow);
        Assert.Equal(["MarketAnalyst", "CrossMarketContext", "RiskManager"], ai.Completions.Select(c => c.Request.Purpose));
        Assert.Contains("### Sentiment & Flow\nNot available: disabled for this agent.", ai.Completions[1].Request.UserPrompt.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task AnInvalidAnswerIsRetriedOnceWithTheReason()
    {
        var calls = 0;
        var ai = CouncilTestHost.Answering();
        var inner = ai.OnComplete!;
        ai.OnComplete = (key, request, ct) =>
            request.Purpose == "MarketAnalyst" && Interlocked.Increment(ref calls) == 1
                ? Task.FromResult(FakeAiClientFactory.Completion(key.Provider, request.Model, "{ not json", inputTokens: 7, outputTokens: 3))
                : inner(key, request, ct);
        var council = CouncilTestHost.Build(ai);

        var result = await council.DeliberateAsync(CouncilTestHost.Context(), Ct);

        var analyst = result.Decision!.RoleOutputs.Single(r => r.Role == CouncilRole.MarketAnalyst);
        Assert.Equal(RoleOutputStatus.Succeeded, analyst.Status);
        Assert.Equal(17, analyst.InputTokens);
        Assert.Equal(8, analyst.OutputTokens);
        Assert.Equal(200, analyst.LatencyMs);
        Assert.Equal(2, result.Decision.Transcripts.Single(t => t.Role == CouncilRole.MarketAnalyst).Output.GetProperty("attempts").GetInt32());
        Assert.Equal(5, ai.Completions.Count);
        var retry = ai.Completions[1].Request.UserPrompt;
        Assert.Contains("## Correction", retry);
        Assert.Contains("not valid JSON", retry);
        Assert.StartsWith(ai.Completions[0].Request.UserPrompt, retry);
    }

    [Fact]
    public async Task TwoInvalidAnswersFailTheRoleAndTheCouncilContinues()
    {
        var ai = CouncilTestHost.Answering(overrides: new Dictionary<CouncilRole, string> { [CouncilRole.MarketAnalyst] = CouncilTestHost.RoleJson(confidence: 7m) });
        var council = CouncilTestHost.Build(ai);

        var result = await council.DeliberateAsync(CouncilTestHost.Context(), Ct);

        Assert.Null(result.Failure);
        var analyst = result.Decision!.RoleOutputs.Single(r => r.Role == CouncilRole.MarketAnalyst);
        Assert.Equal(RoleOutputStatus.Failed, analyst.Status);
        Assert.Null(analyst.ProposedAction);
        var transcript = result.Decision.Transcripts.Single(t => t.Role == CouncilRole.MarketAnalyst);
        Assert.Contains("rejected twice", transcript.Output.GetProperty("error").GetString());
        Assert.Contains("between 0 and 1", transcript.Output.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, transcript.Output.GetProperty("document").ValueKind);
        Assert.Contains("### Market Analyst\nNot available: failed (", ai.Completions[2].Request.UserPrompt.ReplaceLineEndings("\n"));
        Assert.Equal(RoleOutputStatus.Succeeded, result.Decision.RoleOutputs.Single(r => r.Role == CouncilRole.RiskManager).Status);
    }

    [Theory]
    [InlineData(AiFinishReason.MaxTokens, "cut off")]
    [InlineData(AiFinishReason.Refusal, "refusal")]
    [InlineData(AiFinishReason.ContentFiltered, "content filter")]
    public async Task AnIncompleteAnswerIsRejected(AiFinishReason finish, string fragment)
    {
        var ai = CouncilTestHost.Answering();
        var inner = ai.OnComplete!;
        ai.OnComplete = (key, request, ct) => request.Purpose == "CrossMarketContext"
            ? Task.FromResult(FakeAiClientFactory.Completion(key.Provider, request.Model, CouncilTestHost.RoleJson(), finish))
            : inner(key, request, ct);
        var council = CouncilTestHost.Build(ai);

        var result = await council.DeliberateAsync(CouncilTestHost.Context(), Ct);

        var context = result.Decision!.RoleOutputs.Single(r => r.Role == CouncilRole.CrossMarketContext);
        Assert.Equal(RoleOutputStatus.Failed, context.Status);
        Assert.Contains(fragment, result.Decision.Transcripts.Single(t => t.Role == CouncilRole.CrossMarketContext).Output.GetProperty("error").GetString());
        Assert.Equal(5, ai.Completions.Count);
    }

    [Fact]
    public async Task ARiskManagerFailureIsAFailureThatStillCarriesTheDeliberation()
    {
        var ai = CouncilTestHost.Answering(overrides: new Dictionary<CouncilRole, string> { [CouncilRole.RiskManager] = "nope" });
        var council = CouncilTestHost.Build(ai);

        var result = await council.DeliberateAsync(CouncilTestHost.Context(), Ct);

        Assert.NotNull(result.Failure);
        Assert.StartsWith("the Risk Manager failed: the answer was rejected twice", result.Failure);
        Assert.Null(result.Fault);
        var decision = Assert.IsType<Decision>(result.Decision);
        Assert.Equal(DecisionAction.NoTrade, decision.FinalAction);
        Assert.StartsWith("No decision: the Risk Manager failed", decision.Rationale);
        Assert.Null(decision.LimitsCheck);
        Assert.Equal(RoleOutputStatus.Failed, decision.RoleOutputs.Single(r => r.Role == CouncilRole.RiskManager).Status);
        Assert.Equal(3, decision.RoleOutputs.Count(r => r.Status == RoleOutputStatus.Succeeded));
        Assert.Equal(4, decision.Transcripts.Count);
        Assert.Equal(50, decision.TotalInputTokens);
    }

    [Fact]
    public async Task ARoleThatExceedsItsBudgetTimesOutAndTheNextRoleRuns()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ai = CouncilTestHost.Answering();
        var inner = ai.OnComplete!;
        ai.OnComplete = async (key, request, ct) =>
        {
            if (request.Purpose == "MarketAnalyst")
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            return await inner(key, request, ct);
        };
        var time = new FakeTimeProvider(CouncilTestHost.Now);
        var council = CouncilTestHost.Build(ai, time: time);

        var run = council.DeliberateAsync(CouncilTestHost.Context(), Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        time.Advance(TimeSpan.FromSeconds(61));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Null(result.Failure);
        var analyst = result.Decision!.RoleOutputs.Single(r => r.Role == CouncilRole.MarketAnalyst);
        Assert.Equal(RoleOutputStatus.TimedOut, analyst.Status);
        Assert.Contains("budget of 60 s", result.Decision.Transcripts.Single(t => t.Role == CouncilRole.MarketAnalyst).Output.GetProperty("error").GetString());
        Assert.Equal([RoleOutputStatus.Succeeded, RoleOutputStatus.Succeeded, RoleOutputStatus.Succeeded], result.Decision.RoleOutputs.Where(r => r.Role != CouncilRole.MarketAnalyst).Select(r => r.Status));
        Assert.Contains("### Market Analyst\nNot available: timed out", ai.Completions[1].Request.UserPrompt.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task TooLittleTimeBeforeTheDeadlineSkipsTheNonFinalRoles()
    {
        var ai = CouncilTestHost.Answering();
        var council = CouncilTestHost.Build(ai);

        var result = await council.DeliberateAsync(CouncilTestHost.Context(deadline: CouncilTestHost.Now.AddSeconds(50)), Ct);

        Assert.Null(result.Failure);
        Assert.Equal([RoleOutputStatus.Skipped, RoleOutputStatus.Skipped, RoleOutputStatus.Skipped, RoleOutputStatus.Succeeded], result.Decision!.RoleOutputs.Select(r => r.Status));
        Assert.Equal(["RiskManager"], ai.Completions.Select(c => c.Request.Purpose));
        Assert.Contains("not enough time", result.Decision.Transcripts.Single(t => t.Role == CouncilRole.MarketAnalyst).Output.GetProperty("error").GetString());
    }

    [Fact]
    public async Task NoTimeAtAllTimesOutTheRiskManagerWithoutACall()
    {
        var ai = CouncilTestHost.Answering();
        var council = CouncilTestHost.Build(ai);

        var result = await council.DeliberateAsync(CouncilTestHost.Context(deadline: CouncilTestHost.Now.AddSeconds(4)), Ct);

        Assert.Equal("the Risk Manager timed out: not enough time left before the evaluation deadline", result.Failure);
        Assert.Equal(DecisionAction.NoTrade, result.Decision!.FinalAction);
        Assert.Empty(ai.Completions);
    }

    [Fact]
    public async Task TheDeadlinePassingInsideARoleTimesItOutAndSkipsTheRest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<AiCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ai = CouncilTestHost.Answering();
        var inner = ai.OnComplete!;
        ai.OnComplete = (key, request, ct) =>
        {
            if (request.Purpose != "MarketAnalyst")
                return inner(key, request, ct);
            started.TrySetResult();
            return gate.Task;
        };
        var time = new FakeTimeProvider(CouncilTestHost.Now);
        var council = CouncilTestHost.Build(ai, time: time);
        using var evaluation = new CancellationTokenSource();

        var run = council.DeliberateAsync(CouncilTestHost.Context(), evaluation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        time.SetUtcNow(CouncilTestHost.Deadline);
        await evaluation.CancelAsync();
        gate.TrySetCanceled(evaluation.Token);
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal("the Risk Manager skipped: the evaluation deadline passed", result.Failure);
        Assert.Equal([RoleOutputStatus.TimedOut, RoleOutputStatus.Skipped, RoleOutputStatus.Skipped, RoleOutputStatus.Skipped], result.Decision!.RoleOutputs.Select(r => r.Status));
        Assert.Single(ai.Completions);
    }

    [Fact]
    public async Task ALifecycleCancellationPropagates()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<AiCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ai = new FakeAiClientFactory
        {
            OnComplete = (_, _, _) =>
            {
                started.TrySetResult();
                return gate.Task;
            },
        };
        var council = CouncilTestHost.Build(ai);
        using var evaluation = new CancellationTokenSource();

        var run = council.DeliberateAsync(CouncilTestHost.Context(), evaluation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await evaluation.CancelAsync();
        gate.TrySetCanceled(evaluation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task AModelTheProviderRejectsIsAFaultThatStopsTheCouncil()
    {
        var ai = CouncilTestHost.Answering();
        var inner = ai.OnComplete!;
        ai.OnComplete = (key, request, ct) => request.Purpose == "SentimentFlow"
            ? throw new AiRequestException("The model `m-grok` does not exist.", AiProvider.Grok, AiErrorKind.UnknownModel, 404, "model_not_found", "m-grok")
            : inner(key, request, ct);
        var council = CouncilTestHost.Build(ai);

        var result = await council.DeliberateAsync(CouncilTestHost.Context(), Ct);

        Assert.Equal("The Sentiment & Flow role's model 'm-grok' was rejected by Grok: The model `m-grok` does not exist.", result.Fault);
        Assert.StartsWith("the Risk Manager skipped: the council stopped: The Sentiment & Flow role's model", result.Failure);
        Assert.Equal([RoleOutputStatus.Succeeded, RoleOutputStatus.Failed, RoleOutputStatus.Skipped, RoleOutputStatus.Skipped], result.Decision!.RoleOutputs.Select(r => r.Status));
        Assert.Equal(DecisionAction.NoTrade, result.Decision.FinalAction);
        Assert.Equal(2, ai.Completions.Count);
    }

    [Fact]
    public async Task AnInvalidKeyFailsTheRoleWithoutAFault()
    {
        var ai = CouncilTestHost.Answering();
        var inner = ai.OnComplete!;
        ai.OnComplete = (key, request, ct) => request.Purpose == "MarketAnalyst"
            ? throw new AiAuthException("Incorrect API key provided: sk-...wxyz.", AiProvider.Gpt, 401, "invalid_api_key", "m-gpt")
            : inner(key, request, ct);
        var council = CouncilTestHost.Build(ai);

        var result = await council.DeliberateAsync(CouncilTestHost.Context(), Ct);

        Assert.Null(result.Failure);
        Assert.Null(result.Fault);
        var analyst = result.Decision!.RoleOutputs.Single(r => r.Role == CouncilRole.MarketAnalyst);
        Assert.Equal(RoleOutputStatus.Failed, analyst.Status);
        Assert.Equal("GPT Unauthorized: Incorrect API key provided: sk-...wxyz.", result.Decision.Transcripts.Single(t => t.Role == CouncilRole.MarketAnalyst).Output.GetProperty("error").GetString());
    }

    [Fact]
    public async Task AMissingProviderKeyFailsTheRoleWithTheReason()
    {
        var keys = FakeKeyResolver.WithAllProviders();
        keys.Providers.Remove(AiProvider.Grok);
        var ai = CouncilTestHost.Answering();
        var council = CouncilTestHost.Build(ai, keys);

        var result = await council.DeliberateAsync(CouncilTestHost.Context(), Ct);

        Assert.Null(result.Failure);
        var sentiment = result.Decision!.RoleOutputs.Single(r => r.Role == CouncilRole.SentimentFlow);
        Assert.Equal(RoleOutputStatus.Failed, sentiment.Status);
        Assert.Equal("No Grok key.", result.Decision.Transcripts.Single(t => t.Role == CouncilRole.SentimentFlow).Output.GetProperty("error").GetString());
        Assert.DoesNotContain(ai.Completions, c => c.Key.Provider == AiProvider.Grok);
        Assert.Equal([AiProvider.Gpt, AiProvider.Grok, AiProvider.Gemini, AiProvider.Claude], keys.Resolutions.Select(r => r.Provider));
        Assert.All(keys.Resolutions, r => Assert.Equal(CouncilTestHost.UserId, r.UserId));
    }

    [Fact]
    public async Task TheSameProviderIsResolvedOnceAndAKeyNeverReachesAPrompt()
    {
        const string secret = "sk-SECRET-VALUE-THAT-MUST-NEVER-LEAK-0001";
        var keys = FakeKeyResolver.WithAllProviders(secret);
        var agent = CouncilTestHost.NewAgent(configure: a =>
        {
            foreach (var assignment in a.CouncilAssignments)
                (assignment.Provider, assignment.Model) = (AiProvider.Claude, "m-claude");
        });
        var ai = CouncilTestHost.Answering();
        var council = CouncilTestHost.Build(ai, keys);

        var result = await council.DeliberateAsync(CouncilTestHost.Context(agent), Ct);

        Assert.Null(result.Failure);
        Assert.Single(keys.Resolutions);
        Assert.Equal(4, ai.Completions.Count);
        Assert.All(ai.Completions, c =>
        {
            Assert.DoesNotContain(secret, c.Request.SystemPrompt);
            Assert.DoesNotContain(secret, c.Request.UserPrompt);
            Assert.Equal(secret, c.Key.Credentials.ToString().Contains(secret) ? "leaked" : secret);
        });
        Assert.All(result.Decision!.Transcripts, t =>
        {
            Assert.DoesNotContain(secret, t.InputSummary);
            Assert.DoesNotContain(secret, t.Output.GetRawText());
        });
        Assert.Equal(4, result.Decision.RoleOutputs.Count(r => r.Provider == AiProvider.Claude && r.Model == "m-claude"));
    }

    [Fact]
    public async Task AManagingDecisionCarriesTheTradeAndAnOpeningOneDoesNot()
    {
        var agent = CouncilTestHost.NewAgent();
        var trade = CouncilTestHost.OpenTrade(agent);
        var ai = CouncilTestHost.Answering(CouncilTestHost.FinalJson("modify", stop: 97m));
        var council = CouncilTestHost.Build(ai);

        var result = await council.DeliberateAsync(CouncilTestHost.Context(agent, trades: [trade]), Ct);

        Assert.Null(result.Failure);
        Assert.Equal(DecisionAction.Modify, result.Decision!.FinalAction);
        Assert.Equal(97m, result.Decision.StopPrice);
        Assert.Equal(trade.Id, result.Decision.TradeId);
        Assert.Null(result.Decision.Trade);
        Assert.Contains("A position is open", ai.Completions[3].Request.UserPrompt);
        Assert.Contains("long 0.5 BTCUSDT entered at 100", ai.Completions[0].Request.UserPrompt);
    }

    [Fact]
    public async Task TheExperienceSummaryIsAskedPerRoleAndAFailureIsTolerated()
    {
        var experience = new FakeExperienceSummaries
        {
            OnSummarize = query => query.Provider == AiProvider.Gpt ? throw new InvalidOperationException("stats offline") : $"{query.Role}: 12 decisions, win rate 58%.",
        };
        var logs = new TestLoggerProvider();
        var ai = CouncilTestHost.Answering();
        var council = CouncilTestHost.Build(ai, experience: experience, logs: logs);

        var result = await council.DeliberateAsync(CouncilTestHost.Context(), Ct);

        Assert.Null(result.Failure);
        Assert.Equal(4, experience.Queries.Count);
        Assert.All(experience.Queries, q =>
        {
            Assert.Equal(CouncilTestHost.UserId, q.UserId);
            Assert.Equal(CouncilTestHost.AgentId, q.AgentId);
            Assert.Equal(CandleInterval.OneHour, q.Timeframe);
            Assert.Equal(MarketType.Spot, q.MarketType);
            Assert.Equal(CouncilTestHost.Model(q.Provider), q.Model);
        });
        Assert.Contains("No experience recorded yet.", ai.Completions[0].Request.UserPrompt);
        Assert.Contains("RiskManager: 12 decisions, win rate 58%.", ai.Completions[3].Request.UserPrompt);
        Assert.Contains(logs.Entries, e => e.Message.Contains("Experience summary") && e.Exception?.Message == "stats offline");
    }

    [Fact]
    public async Task WithoutARiskManagerNothingIsCalled()
    {
        var agent = CouncilTestHost.NewAgent(configure: a => a.CouncilAssignments.Remove(a.CouncilAssignments.Single(c => c.Role == CouncilRole.RiskManager)));
        var ai = CouncilTestHost.Answering();
        var council = CouncilTestHost.Build(ai);

        var result = await council.DeliberateAsync(CouncilTestHost.Context(agent), Ct);

        Assert.Equal("the agent has no enabled Risk Manager", result.Failure);
        Assert.Null(result.Decision);
        Assert.Empty(ai.Completions);
    }

    [Fact]
    public async Task ARoleWithoutAModelFails()
    {
        var agent = CouncilTestHost.NewAgent(configure: a => a.CouncilAssignments.Single(c => c.Role == CouncilRole.CrossMarketContext).Model = " ");
        var ai = CouncilTestHost.Answering();
        var council = CouncilTestHost.Build(ai);

        var result = await council.DeliberateAsync(CouncilTestHost.Context(agent), Ct);

        Assert.Null(result.Failure);
        Assert.Equal(RoleOutputStatus.Failed, result.Decision!.RoleOutputs.Single(r => r.Role == CouncilRole.CrossMarketContext).Status);
        Assert.Equal(3, ai.Completions.Count);
    }

    [Fact]
    public async Task ARejectedEntryIsRecordedAsNoTradeWithTheLimits()
    {
        var ai = CouncilTestHost.Answering(CouncilTestHost.FinalJson("open_long", "limit", 100m, 96m, [108m], risk: 5m, rationale: "Go."));
        var council = CouncilTestHost.Build(ai);

        var result = await council.DeliberateAsync(CouncilTestHost.Context(entriesAllowed: false), Ct);

        Assert.Null(result.Failure);
        var decision = result.Decision!;
        Assert.Equal(DecisionAction.NoTrade, decision.FinalAction);
        Assert.Null(decision.EntryPrice);
        Assert.Null(decision.StopPrice);
        Assert.StartsWith("Rejected by hard limits (entriesAllowed): Go.", decision.Rationale);
        Assert.Equal(DecisionAction.OpenLong, decision.RoleOutputs.Single(r => r.Role == CouncilRole.RiskManager).ProposedAction);
        var checks = decision.LimitsCheck!.Value.EnumerateArray().ToDictionary(c => c.GetProperty("limit").GetString()!, c => c.GetProperty("status").GetString());
        Assert.Equal("fail", checks["entriesAllowed"]);
        Assert.Equal("clamped", checks["riskPerTrade"]);
    }

    [Fact]
    public void TheBudgetFollowsTheDeadlineAndTheReserve()
    {
        var options = new CouncilOptions { RoleTimeoutSeconds = 60, RiskManagerReserveSeconds = 45, MinimumRoleBudgetSeconds = 5 };
        var now = CouncilTestHost.Now;

        Assert.Equal(TimeSpan.FromSeconds(60), Ecnanib.Council.Services.Council.Budget(options, now.AddSeconds(500), now, final: false));
        Assert.Equal(TimeSpan.FromSeconds(35), Ecnanib.Council.Services.Council.Budget(options, now.AddSeconds(90), now, final: false));
        Assert.Equal(TimeSpan.FromSeconds(60), Ecnanib.Council.Services.Council.Budget(options, now.AddSeconds(90), now, final: true));
        Assert.Equal(TimeSpan.FromSeconds(20), Ecnanib.Council.Services.Council.Budget(options, now.AddSeconds(30), now, final: true));
        Assert.Null(Ecnanib.Council.Services.Council.Budget(options, now.AddSeconds(51), now, final: false));
        Assert.Null(Ecnanib.Council.Services.Council.Budget(options, now.AddSeconds(6), now, final: true));
    }
}
