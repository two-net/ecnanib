using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Database.Live;

[Trait("Category", "Live")]
public class EntityLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public EntityLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RoundTripsEveryEntityAndDeletingTheUserCascadesToEverythingTheyOwn()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        Guid userId, agentId;
        await using (var db = _fixture.CreateContext())
        {
            var (user, agent, _, _) = await LiveSeed.SeedEverythingAsync(db, Ct);
            userId = user.Id;
            agentId = agent.Id;
        }

        await using (var db = _fixture.CreateContext())
        {
            var agent = await db.Agents
                .Include(a => a.User).Include(a => a.ExchangeKey).Include(a => a.CouncilAssignments).Include(a => a.EntryWindows)
                .SingleAsync(a => a.Id == agentId, Ct);
            Assert.Equal(7, agent.Id.Version);
            Assert.Equal(0.5m, agent.RiskLimits.RiskPerTradePercent);
            Assert.Equal(10_000m, agent.RiskLimits.MaxNotional);
            Assert.Equal(14, agent.Parameters.GetProperty("atrPeriod").GetInt32());
            Assert.Equal(AgentState.Draft, agent.State);
            Assert.Equal(2, agent.CouncilAssignments.Count);
            Assert.Contains(agent.EntryWindows, w => w.Kind == EntryWindowKind.Recurring && w.StartDay == DayOfWeek.Saturday && w.EndTime == new TimeOnly(23, 59));
            Assert.Equal("s3cr3tvalue", agent.ExchangeKey!.ApiSecret);
            Assert.False(agent.ExchangeKey.PermissionsSnapshot!.Value.GetProperty("enableWithdrawals").GetBoolean());

            var decision = await db.Decisions.Include(d => d.RoleOutputs).Include(d => d.Transcripts).Include(d => d.ShadowOutcome)
                .SingleAsync(d => d.Evaluation.AgentId == agentId, Ct);
            Assert.Equal(DecisionAction.OpenLong, decision.FinalAction);
            Assert.Equal(2, decision.RoleOutputs.Count);
            Assert.Equal(2, decision.Transcripts.Count);
            Assert.Equal(ShadowResult.Pending, decision.ShadowOutcome!.Result);
            Assert.Null(decision.TradeId);

            var trade = await db.Trades.SingleAsync(t => t.EntryDecisionId == decision.Id, Ct);
            Assert.Equal(PositionSide.Long, trade.Side);
            Assert.Equal(CandleInterval.OneHour, trade.Timeframe);
            Assert.Equal(49_000m, trade.StopHistory[0].GetProperty("price").GetDecimal());

            var order = await db.Orders.SingleAsync(o => o.AgentId == agentId, Ct);
            Assert.Equal(trade.Id, order.TradeId);
            Assert.Equal(123456789L, order.ExchangeOrderId);

            Assert.Equal("Draft", await _fixture.ScalarAsync<string>("SELECT state FROM agents"));
            Assert.Equal((int)Microsoft.Extensions.Logging.LogLevel.Information, await _fixture.ScalarAsync<int>("SELECT level FROM log_entries"));
            Assert.Equal(1, await db.ExperienceStats.CountAsync(s => s.Scope == StatScope.System && s.AgentId == null && s.UserId == null, Ct));
        }

        await using (var db = _fixture.CreateContext())
        {
            // FR-ACC-04: one statement removes keys, agents, decisions and trades.
            Assert.Equal(1, await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(Ct));

            Assert.Equal(0, await db.ExchangeKeys.CountAsync(Ct));
            Assert.Equal(0, await db.ProviderKeys.CountAsync(Ct));
            Assert.Equal(0, await db.Agents.CountAsync(Ct));
            Assert.Equal(0, await db.CouncilAssignments.CountAsync(Ct));
            Assert.Equal(0, await db.AgentChanges.CountAsync(Ct));
            Assert.Equal(0, await db.EntryWindows.CountAsync(Ct));
            Assert.Equal(0, await db.Evaluations.CountAsync(Ct));
            Assert.Equal(0, await db.Decisions.CountAsync(Ct));
            Assert.Equal(0, await db.DecisionRoleOutputs.CountAsync(Ct));
            Assert.Equal(0, await db.DeliberationTranscripts.CountAsync(Ct));
            Assert.Equal(0, await db.Orders.CountAsync(Ct));
            Assert.Equal(0, await db.Trades.CountAsync(Ct));
            Assert.Equal(0, await db.ShadowOutcomes.CountAsync(Ct));
            Assert.Equal(0, await db.DisclaimerAcceptances.CountAsync(Ct));
            Assert.Equal(0, await db.ExperienceStats.CountAsync(s => s.Scope != StatScope.System, Ct));

            // Shared and system-owned rows survive; log entries have no foreign key and are deleted by the account service.
            Assert.Equal(1, await db.Candles.CountAsync(Ct));
            Assert.Equal(1, await db.ProviderModels.CountAsync(Ct));
            Assert.Equal(1, await db.ProviderSyncStates.CountAsync(Ct));
            Assert.Equal(1, await db.SystemKeys.CountAsync(Ct));
            Assert.Equal(1, await db.ExperienceStats.CountAsync(s => s.Scope == StatScope.System, Ct));
            Assert.Equal(1, await db.LogEntries.CountAsync(Ct));
            await db.LogEntries.ExecuteDeleteAsync(Ct);
            await db.Candles.ExecuteDeleteAsync(Ct);
            await db.ProviderModels.ExecuteDeleteAsync(Ct);
            await db.ProviderSyncStates.ExecuteDeleteAsync(Ct);
            await db.SystemKeys.ExecuteDeleteAsync(Ct);
            await db.ExperienceStats.ExecuteDeleteAsync(Ct);
        }
    }

    [Fact]
    public async Task EnforcesUniquenessAndCheckConstraints()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        Guid userId, agentId;
        await using (var db = _fixture.CreateContext())
        {
            var user = LiveSeed.NewUser("unique@example.com");
            var agent = LiveSeed.NewAgent(user, "unique");
            db.AddRange(user, agent, new ProviderKey { User = user, Provider = AiProvider.Gpt, ApiKey = "sk-1", CreatedAt = LiveSeed.Now });
            db.Add(LiveSeed.NewEvaluation(agent, LiveSeed.Now));
            db.Add(new Order { Agent = agent, ClientOrderId = "dup-1", Purpose = OrderPurpose.Entry, Type = OrderType.Market, Side = OrderSide.Buy, Quantity = 1m, Status = OrderStatus.New, CreatedAt = LiveSeed.Now, UpdatedAt = LiveSeed.Now });
            await db.SaveChangesAsync(Ct);
            userId = user.Id;
            agentId = agent.Id;
        }

        // FR-AGT-09: one evaluation per agent and candle.
        await AssertRejectedAsync(db => db.Add(new Evaluation { AgentId = agentId, Symbol = "BTCUSDT", MarketType = MarketType.UsdFutures, Timeframe = CandleInterval.OneHour, CandleOpenTime = LiveSeed.Now, StartedAt = LiveSeed.Now, Outcome = EvaluationOutcome.NoAction }));
        // FR-EXE-07: one order per client order id.
        await AssertRejectedAsync(db => db.Add(new Order { AgentId = agentId, ClientOrderId = "dup-1", Purpose = OrderPurpose.Entry, Type = OrderType.Market, Side = OrderSide.Buy, Quantity = 1m, Status = OrderStatus.New, CreatedAt = LiveSeed.Now, UpdatedAt = LiveSeed.Now }));
        // FR-EXP-05: a system-scope statistic must not carry an owner.
        await AssertRejectedAsync(db => db.Add(new ExperienceStat { Scope = StatScope.System, UserId = userId, Provider = AiProvider.Gpt, Model = "m", Role = CouncilRole.RiskManager, Timeframe = CandleInterval.OneHour, MarketType = MarketType.Spot, ComputedAt = LiveSeed.Now }));
        // FR-KEY-03: one key per provider per user.
        await AssertRejectedAsync(db => db.Add(new ProviderKey { UserId = userId, Provider = AiProvider.Gpt, ApiKey = "sk-2", CreatedAt = LiveSeed.Now }));
        // FR-AI-05: the Risk Manager cannot be disabled.
        await AssertRejectedAsync(db => db.Add(new CouncilAssignment { AgentId = agentId, Role = CouncilRole.RiskManager, Provider = AiProvider.Claude, Model = "m", Enabled = false }));
        // A one-off window must not carry recurring fields.
        await AssertRejectedAsync(db => db.Add(new EntryWindow { AgentId = agentId, Kind = EntryWindowKind.OneOff, StartAt = LiveSeed.Now, EndAt = LiveSeed.Now.AddHours(1), StartDay = DayOfWeek.Monday }));

        await using (var db = _fixture.CreateContext())
        {
            await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(Ct);
        }
    }

    [Fact]
    public async Task DetectsConcurrentAgentUpdatesThroughXmin()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        Guid agentId, userId;
        await using (var db = _fixture.CreateContext())
        {
            var user = LiveSeed.NewUser("xmin@example.com");
            var agent = LiveSeed.NewAgent(user, "xmin");
            db.AddRange(user, agent);
            await db.SaveChangesAsync(Ct);
            agentId = agent.Id;
            userId = user.Id;
        }

        await using var first = _fixture.CreateContext();
        await using var second = _fixture.CreateContext();
        var a1 = await first.Agents.SingleAsync(a => a.Id == agentId, Ct);
        var a2 = await second.Agents.SingleAsync(a => a.Id == agentId, Ct);
        Assert.Equal(a1.Version, a2.Version);

        a1.Name = "first wins";
        await first.SaveChangesAsync(Ct);
        a2.Name = "second loses";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(Ct));

        await first.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(Ct);
    }

    [Fact]
    public async Task StoresTimestampsInUtcWhateverOffsetWasGiven()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var bangkok = new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.FromHours(7));
        Guid userId;
        await using (var db = _fixture.CreateContext())
        {
            var user = LiveSeed.NewUser("utc@example.com");
            user.CreatedAt = bangkok;
            user.DisclaimerAcceptedAt = bangkok;
            db.Add(user);
            await db.SaveChangesAsync(Ct);
            userId = user.Id;
        }

        await using (var db = _fixture.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId, Ct);
            Assert.Equal(TimeSpan.Zero, user.CreatedAt.Offset);
            Assert.Equal(bangkok.UtcDateTime, user.CreatedAt.UtcDateTime);
            Assert.Equal(bangkok.UtcDateTime, user.DisclaimerAcceptedAt!.Value.UtcDateTime);
            Assert.Equal(1, await db.Users.CountAsync(u => u.CreatedAt < bangkok.AddMinutes(1) && u.CreatedAt > bangkok.AddMinutes(-1), Ct));
            await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(Ct);
        }
    }

    private async Task AssertRejectedAsync(Action<Ecnanib.Database.EcnanibDbContext> add)
    {
        await using var db = _fixture.CreateContext();
        add(db);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }
}
