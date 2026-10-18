using Ecnanib.Database.Entities;
using Ecnanib.Database.Retention;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Database.Live;

[Trait("Category", "Live")]
public class RetentionSweeperLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public RetentionSweeperLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeletesOnlyExpiredLogsCandlesAndTranscriptsInBatches()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var time = new FakeTimeProvider(LiveSeed.Now);
        var old = LiveSeed.Now.AddDays(-400);
        var recent = LiveSeed.Now.AddDays(-1);

        await using (var db = _fixture.CreateContext())
        {
            var user = LiveSeed.NewUser("retention@example.com");
            var agent = LiveSeed.NewAgent(user, "retention");
            var oldEvaluation = LiveSeed.NewEvaluation(agent, old);
            var oldDecision = LiveSeed.NewDecision(oldEvaluation, old);
            var recentEvaluation = LiveSeed.NewEvaluation(agent, recent);
            var recentDecision = LiveSeed.NewDecision(recentEvaluation, recent);
            db.AddRange(user, agent, oldEvaluation, oldDecision, recentEvaluation, recentDecision);
            for (var i = 0; i < 150; i++)
            {
                db.Add(LiveSeed.NewLogEntry(old.AddMinutes(i), user.Id));
                db.Add(LiveSeed.NewCandle(old.AddHours(i)));
            }

            for (var i = 0; i < 3; i++)
                db.Add(LiveSeed.NewLogEntry(recent.AddMinutes(i), user.Id));
            db.Add(LiveSeed.NewCandle(recent));
            db.Add(LiveSeed.NewCandle(recent.AddHours(1)));
            await db.SaveChangesAsync(Ct);

            db.Add(new Trade { Agent = agent, EntryDecision = oldDecision, Symbol = "BTCUSDT", MarketType = Ecnanib.Exchange.Abstractions.MarketType.UsdFutures, Timeframe = Ecnanib.Exchange.Abstractions.CandleInterval.OneHour, Side = PositionSide.Long, EntryPrice = 1m, Size = 1m, OpenedAt = old, ClosedAt = old.AddDays(1), RealizedPnl = 10m, ExitReason = TradeExitReason.Target });
            await db.SaveChangesAsync(Ct);
        }

        await using var services = _fixture.BuildServices(time: time, config: new() { ["Retention:DeleteBatchSize"] = "100" });
        var sweeper = services.GetRequiredService<RetentionSweeper>();

        var result = await sweeper.SweepOnceAsync(Ct);

        Assert.Equal(new RetentionSweepResult(150, 150, 2, 0), result);
        await using (var db = _fixture.CreateContext())
        {
            Assert.Equal(3, await db.LogEntries.CountAsync(Ct));
            Assert.Equal(2, await db.Candles.CountAsync(Ct));
            Assert.Equal(2, await db.DeliberationTranscripts.CountAsync(Ct));
            Assert.All(await db.DeliberationTranscripts.ToListAsync(Ct), t => Assert.Equal(recent, t.CreatedAt));
            // NFR-DATA-03: decisions, their compact role outputs and trades live for the life of the account.
            Assert.Equal(2, await db.Decisions.CountAsync(Ct));
            Assert.Equal(4, await db.DecisionRoleOutputs.CountAsync(Ct));
            Assert.Equal(2, await db.Evaluations.CountAsync(Ct));
            Assert.Equal(1, await db.Trades.CountAsync(Ct));
        }

        Assert.Equal(new RetentionSweepResult(0, 0, 0, 0), await sweeper.SweepOnceAsync(Ct));

        time.Advance(TimeSpan.FromDays(31));
        Assert.Equal(new RetentionSweepResult(3, 0, 0, 0), await sweeper.SweepOnceAsync(Ct));
    }
}
