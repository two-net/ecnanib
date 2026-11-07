using System.Collections.Concurrent;
using Ecnanib.Kernel;
using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// FR-UI-05: an observer hears of a change only once others can read it, so a page that reads again when it's told
/// never reads before the change. A change saved on its own runs its callbacks when the save ends, and one in a
/// transaction when the transaction commits. A rollback keeps them for the context's next commit. A notification raised
/// with a change is the case every raiser has.
/// </summary>
public sealed class AfterCommitTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => AgentApp.Cancellation;

    [Fact]
    public async Task A_change_saved_on_its_own_is_heard_once_it_can_be_read()
    {
        var observer = new NotificationRecorder();
        await using var app = new AgentApp(postgres, services: services => services.AddSingleton<INotificationObserver>(observer));
        var userId = Guid.NewGuid();
        observer.Read = () => CountAsync(app, userId).GetAwaiter().GetResult();

        await using (var context = await ContextAsync(app))
        {
            Notifications(app).Add(context, Notice(userId));
            Assert.Empty(observer.Heard);

            await context.SaveChangesAsync(Cancellation);
        }

        Assert.Equal([(userId, 1L)], observer.Heard);

        await Notifications(app).RaiseAsync(Notice(userId), Cancellation);

        Assert.Equal([(userId, 1L), (userId, 2L)], observer.Heard);
    }

    [Fact]
    public async Task In_a_transaction_it_waits_for_the_commit_and_a_rollback_keeps_it_for_the_next()
    {
        var observer = new NotificationRecorder();
        await using var app = new AgentApp(postgres, services: services => services.AddSingleton<INotificationObserver>(observer));
        var userId = Guid.NewGuid();
        observer.Read = () => CountAsync(app, userId).GetAwaiter().GetResult();
        var ran = 0;

        await using (var context = await ContextAsync(app))
        {
            await using (var transaction = await context.Database.BeginTransactionAsync(Cancellation))
            {
                Notifications(app).Add(context, Notice(userId));
                context.AfterCommit(() => ran++);
                await context.SaveChangesAsync(Cancellation);

                Assert.Equal(0, ran);
                Assert.Empty(observer.Heard);

                await transaction.RollbackAsync(Cancellation);
            }

            Assert.Equal(0, ran);
            Assert.Empty(observer.Heard);

            // The next commit runs them, so a caller that tries again isn't left unheard. An observer only reads again.
            context.ChangeTracker.Clear();

            await using (var transaction = await context.Database.BeginTransactionAsync(Cancellation))
            {
                Notifications(app).Add(context, Notice(userId));
                await context.SaveChangesAsync(Cancellation);
                Notifications(app).Add(context, Notice(userId));
                await context.SaveChangesAsync(Cancellation);

                Assert.Equal(0, ran);

                await transaction.CommitAsync(Cancellation);
            }
        }

        Assert.Equal(1, ran);
        Assert.Equal([(userId, 2L), (userId, 2L), (userId, 2L)], observer.Heard);
    }

    [Fact]
    public async Task A_context_disposed_without_a_commit_runs_nothing()
    {
        await using var app = new AgentApp(postgres);
        var ran = 0;

        await using (var context = await ContextAsync(app))
        {
            context.AfterCommit(() => ran++);
            Notifications(app).Add(context, Notice(Guid.NewGuid()));
        }

        Assert.Equal(0, ran);
    }

    private static INotifications Notifications(AgentApp app) => app.App.Services.GetRequiredService<INotifications>();

    private static Task<EcnanibDbContext> ContextAsync(AgentApp app) =>
        app.App.Services.GetRequiredService<IDbContextFactory<EcnanibDbContext>>().CreateDbContextAsync(Cancellation);

    private static Notice Notice(Guid userId) => new(NotificationType.TradeOpened, userId, "/agents", "A subject", demo: true);

    private static Task<long> CountAsync(AgentApp app, Guid userId) =>
        Sql.ScalarAsync<long>(app.ConnectionString, $"SELECT count(*) FROM notifications WHERE user_id = '{userId}'", Cancellation);

    private sealed class NotificationRecorder : INotificationObserver
    {
        private readonly ConcurrentQueue<(Guid UserId, long Readable)> _heard = new();

        /// <summary>How many of the user's notifications another connection can read when it's told.</summary>
        public Func<long> Read { get; set; } = () => 0;

        public IReadOnlyList<(Guid UserId, long Readable)> Heard => [.. _heard];

        public void OnAdded(Guid userId) => _heard.Enqueue((userId, Read()));
    }
}
