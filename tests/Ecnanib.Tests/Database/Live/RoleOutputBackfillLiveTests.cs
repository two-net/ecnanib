using System.Text.Json;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Database.Migrations;
using Ecnanib.Tests.Agents.Live;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Database.Live;

/// <summary>
/// NFR-COST-01: the backfill of <c>AddRoleOutputAttempts</c> reads a role's calls and reasoning tokens from its transcript
/// while that exists, counts a called role whose transcript was swept as one call when it reported tokens or latency,
/// and leaves disabled and skipped roles, and a swept failure that reported neither, at 0. Its own class, so the
/// migrated database it needs is not the one <c>MigrationLiveTests</c> migrates itself.
/// </summary>
[Trait("Category", "Live")]
public class RoleOutputBackfillLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public RoleOutputBackfillLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheBackfillReadsTheTranscriptsAndCountsSweptCallsThatReportedSomething()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "backfill@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var now = AgentsLiveSeed.Now;
        Guid first;
        Guid second;
        await using (var db = _fixture.CreateContext())
        {
            var retried = Decision(agent, now.AddHours(-2),
            [
                Row(CouncilRole.MarketAnalyst, 1, RoleOutputStatus.Succeeded, 600, 150, 500),
                Row(CouncilRole.SentimentFlow, 2, RoleOutputStatus.Disabled, 0, 0, 0),
                Row(CouncilRole.CrossMarketContext, 3, RoleOutputStatus.Failed, 300, 0, 200),
                Row(CouncilRole.RiskManager, 4, RoleOutputStatus.TimedOut, 0, 0, 0),
            ],
            [
                Transcript(CouncilRole.MarketAnalyst, """{"status":"Succeeded","attempts":2,"reasoningTokens":40}"""),
                Transcript(CouncilRole.RiskManager, """{"status":"TimedOut","attempts":1,"reasoningTokens":null}"""),
            ]);
            // The second decision: a transcript without the keys, a swept failure that reported nothing (a call that threw
            // before any answer, or a role never called: the backfill cannot tell), and a skipped role with its transcript.
            var cut = Decision(agent, now.AddHours(-1),
            [
                Row(CouncilRole.MarketAnalyst, 1, RoleOutputStatus.Succeeded, 500, 100, 400),
                Row(CouncilRole.SentimentFlow, 2, RoleOutputStatus.Failed, 0, 0, 0),
                Row(CouncilRole.RiskManager, 4, RoleOutputStatus.Skipped, 0, 0, 0),
            ],
            [
                Transcript(CouncilRole.MarketAnalyst, """{"status":"Succeeded"}"""),
                Transcript(CouncilRole.RiskManager, """{"status":"Skipped","attempts":0,"reasoningTokens":0}"""),
            ]);
            db.AddRange(retried, cut);
            await db.SaveChangesAsync(Ct);
            first = retried.Id;
            second = cut.Id;

            // The rows as the migration finds them: the new columns at their default.
            await db.Database.ExecuteSqlRawAsync("UPDATE decision_role_outputs SET attempts = 0, reasoning_tokens = 0", Ct);
            await db.Database.ExecuteSqlRawAsync(AddRoleOutputAttempts.BackfillSql, Ct);
        }

        await using (var db = _fixture.CreateContext())
        {
            var retried = await db.DecisionRoleOutputs.AsNoTracking().Where(r => r.DecisionId == first).OrderBy(r => r.Sequence).ToListAsync(Ct);
            Assert.Equal([2, 0, 1, 1], retried.Select(r => r.Attempts));
            Assert.Equal([40, 0, 0, 0], retried.Select(r => r.ReasoningTokens));
            var cut = await db.DecisionRoleOutputs.AsNoTracking().Where(r => r.DecisionId == second).OrderBy(r => r.Sequence).ToListAsync(Ct);
            Assert.Equal([1, 0, 0], cut.Select(r => r.Attempts));
            Assert.Equal([0, 0, 0], cut.Select(r => r.ReasoningTokens));
        }

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    private static Decision Decision(Agent agent, DateTimeOffset candleOpen, DecisionRoleOutput[] rows, DeliberationTranscript[] transcripts) => new()
    {
        Evaluation = new Evaluation
        {
            AgentId = agent.Id, Symbol = agent.Symbol, MarketType = agent.MarketType, Timeframe = agent.Timeframe, CandleOpenTime = candleOpen,
            StartedAt = candleOpen.AddHours(1), FinishedAt = candleOpen.AddHours(1).AddSeconds(20), CouncilConsulted = true, Outcome = EvaluationOutcome.Decided,
        },
        FinalAction = DecisionAction.NoTrade, Rationale = "seeded", CreatedAt = candleOpen.AddHours(1), RoleOutputs = rows, Transcripts = transcripts,
    };

    private static DecisionRoleOutput Row(CouncilRole role, int sequence, RoleOutputStatus status, int input, int output, int latencyMs) => new()
    {
        Role = role, Sequence = sequence, Provider = AiProvider.Gpt, Model = "m", Status = status, InputTokens = input, OutputTokens = output, LatencyMs = latencyMs,
    };

    private static DeliberationTranscript Transcript(CouncilRole role, string output) => new()
    {
        Role = role, InputSummary = "prompt", Output = JsonDocument.Parse(output).RootElement.Clone(), CreatedAt = AgentsLiveSeed.Now,
    };
}
